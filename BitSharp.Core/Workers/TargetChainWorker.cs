﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core.Domain;
using BitSharp.Core.Domain.Builders;
using BitSharp.Core.Rules;
using BitSharp.Core.Storage;
using Ninject;
using Ninject.Parameters;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BitSharp.Core.Workers
{
    public class TargetChainWorker : Worker
    {
        public event Action OnTargetBlockChanged;
        public event Action OnTargetChainChanged;

        private readonly Logger logger;
        private readonly IBlockchainRules rules;
        private readonly ChainedBlockCache chainedBlockCache;
        private readonly InvalidBlockCache invalidBlockCache;

        private readonly TargetBlockWorker targetBlockWorker;
        private Chain targetChain;

        private readonly AutoResetEvent rescanEvent;

        public TargetChainWorker(WorkerConfig workerConfig, Logger logger, IKernel kernel, IBlockchainRules rules, ChainedBlockCache chainedBlockCache, InvalidBlockCache invalidBlockCache)
            : base("TargetChainWorker", workerConfig.initialNotify, workerConfig.minIdleTime, workerConfig.maxIdleTime, logger)
        {
            this.logger = logger;
            this.rules = rules;
            this.chainedBlockCache = chainedBlockCache;
            this.invalidBlockCache = invalidBlockCache;

            this.rescanEvent = new AutoResetEvent(false);

            this.targetBlockWorker = kernel.Get<TargetBlockWorker>(
                new ConstructorArgument("workerConfig", new WorkerConfig(initialNotify: true, minIdleTime: TimeSpan.Zero, maxIdleTime: TimeSpan.MaxValue)));

            this.targetBlockWorker.OnTargetBlockChanged += HandleTargetBlockChanged;
            this.chainedBlockCache.OnAddition += HandleChainedBlock;
            this.invalidBlockCache.OnAddition += HandleInvalidBlock;
        }

        protected override void SubDispose()
        {
            // cleanup events
            this.targetBlockWorker.OnTargetBlockChanged -= HandleTargetBlockChanged;
            this.chainedBlockCache.OnAddition -= HandleChainedBlock;
            this.invalidBlockCache.OnAddition -= HandleInvalidBlock;

            // cleanup workers
            this.targetBlockWorker.Dispose();
        }

        public Chain TargetChain { get { return this.targetChain; } }

        public ChainedBlock TargetBlock { get { return this.targetBlockWorker.TargetBlock; } }

        internal TargetBlockWorker TargetBlockWorker { get { return this.targetBlockWorker; } }

        protected override void SubStart()
        {
            this.targetBlockWorker.Start();
        }

        protected override void SubStop()
        {
            this.targetBlockWorker.Stop();
        }

        protected override void WorkAction()
        {
            try
            {
                if (this.rescanEvent.WaitOne(0))
                {
                    this.targetChain = null;
                }

                var targetBlockLocal = this.targetBlockWorker.TargetBlock;
                var targetChainLocal = this.targetChain;

                if (targetBlockLocal != null &&
                    (targetChainLocal == null || targetBlockLocal.BlockHash != targetChainLocal.LastBlock.BlockHash))
                {
                    var newTargetChain =
                        targetChainLocal != null
                        ? targetChainLocal.ToBuilder()
                        : new ChainBuilder(Chain.CreateForGenesisBlock(this.rules.GenesisChainedBlock));

                    var deltaBlockPath = new MethodTimer(false).Time("deltaBlockPath", () =>
                        new BlockchainWalker().GetBlockchainPath(newTargetChain.LastBlock, targetBlockLocal, blockHash => this.chainedBlockCache[blockHash]));

                    foreach (var rewindBlock in deltaBlockPath.RewindBlocks)
                    {
                        if (this.invalidBlockCache.ContainsKey(rewindBlock.BlockHash))
                        {
                            this.rescanEvent.Set();
                            return;
                        }

                        newTargetChain.RemoveBlock(rewindBlock);
                    }

                    var invalid = false;
                    foreach (var advanceBlock in deltaBlockPath.AdvanceBlocks)
                    {
                        if (this.invalidBlockCache.ContainsKey(advanceBlock.BlockHash))
                            invalid = true;

                        if (!invalid)
                            newTargetChain.AddBlock(advanceBlock);
                        else
                            this.invalidBlockCache.TryAdd(advanceBlock.BlockHash, "");
                    }

                    this.logger.Debug("Winning chained block {0} at height {1}, total work: {2}".Format2(newTargetChain.LastBlock.BlockHash.ToHexNumberString(), newTargetChain.Height, newTargetChain.LastBlock.TotalWork.ToString("X")));
                    this.targetChain = newTargetChain.ToImmutable();

                    var handler = this.OnTargetChainChanged;
                    if (handler != null)
                        handler();
                }
            }
            catch (MissingDataException) { }
        }

        private void HandleTargetBlockChanged()
        {
            this.NotifyWork();

            var handler = this.OnTargetBlockChanged;
            if (handler != null)
                handler();
        }

        private void HandleChainedBlock(UInt256 blockHash, ChainedBlock chainedBlock)
        {
            this.NotifyWork();
        }

        private void HandleInvalidBlock(UInt256 blockHash, string data)
        {
            this.rescanEvent.Set();
            this.NotifyWork();
        }
    }
}