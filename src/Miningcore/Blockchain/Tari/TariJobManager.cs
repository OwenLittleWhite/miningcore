/*
Copyright 2019. The Tari Project

 Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
 following conditions are met:

 1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following
 disclaimer.

 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
 following disclaimer in the documentation and/or other materials provided with the distribution.

 3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote
 products derived from this software without specific prior written permission.

 THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE
 USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.Tari.StratumRequests;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Native;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using MoreLinq;
using Newtonsoft.Json;
using NLog;
using Contract = Miningcore.Contracts.Contract;
using GetInfoResponse = Miningcore.Blockchain.Tari.DaemonResponses.GetInfoResponse;
using GetBlockTemplateResponse = Miningcore.Blockchain.Tari.DaemonResponses.GetBlockTemplateResponse;
using GetBlockTemplateRequest = Miningcore.Blockchain.Tari.DaemonRequests.GetBlockTemplateRequest;
using SubmitResponse = Miningcore.Blockchain.Tari.DaemonResponses.SubmitResponse;

namespace Miningcore.Blockchain.Tari
{
    public class TariJobManager : JobManagerBase<TariJob>
    {
        public TariJobManager(
            IComponentContext ctx,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(ctx, messageBus)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(clock, nameof(clock));
            Contract.RequiresNonNull(messageBus, nameof(messageBus));

            this.clock = clock;

        }

        private byte[] instanceId;
        private DaemonEndpointConfig[] daemonEndpoints;
        private RpcClient daemon;
        private RpcClient walletDaemon;
        private readonly IMasterClock clock;
        private TariNetworkType networkType;
        public UInt64 infoDiff;
        private DaemonEndpointConfig[] walletDaemonEndpoints;
        private TariCoinTemplate coin;

        public TariCoinTemplate Coin => coin;

        protected async Task<bool> UpdateJob(CancellationToken ct, string via = null, string json = null)
        {
            logger.LogInvoke();

            try
            {
                var response = string.IsNullOrEmpty(json) ? await GetBlockTemplateAsync(ct) : GetBlockTemplateFromJson(json);

                // may happen if daemon is currently not connected to peers
                if(response.Error != null)
                {
                    logger.Warn(() => $"Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                    return false;
                }

                var blockTemplate = response.Response;
                var job = currentJob;
                var newHash = blockTemplate.Blob.HexToByteArray().Slice(7, 32).ToHexString();

                var isNew = job == null || newHash != job.PrevHash;

                if(isNew)
                {
                    messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

                    if(via != null)
                        logger.Info(() => $"Detected new block {blockTemplate.Height} [{via}]");
                    else
                        logger.Info(() => $"Detected new block {blockTemplate.Height}");

                    job = new TariJob(blockTemplate, instanceId, NextJobId(), coin, poolConfig, clusterConfig, newHash);
                    currentJob = job;

                    // update stats
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = job.BlockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.BlockTemplate.Difficulty;
                    BlockchainStats.NextNetworkTarget = "";
                    BlockchainStats.NextNetworkBits = "";
                }
                else
                {
                    if(via != null)
                        logger.Debug(() => $"Template update {blockTemplate.Height} [{via}]");
                    else
                        logger.Debug(() => $"Template update {blockTemplate.Height}");
                }

                return isNew;
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
            }

            return false;
        }

        private async Task<RpcResponse<GetBlockTemplateResponse>> GetBlockTemplateAsync(CancellationToken ct)
        {
            logger.LogInvoke();

            var request = new GetBlockTemplateRequest{};

            return await daemon.ExecuteAsync<GetBlockTemplateResponse>(logger, TariCommands.GetBlockTemplate, ct, request);
        }

        private RpcResponse<GetBlockTemplateResponse> GetBlockTemplateFromJson(string json)
        {
            logger.LogInvoke();

            var result = JsonConvert.DeserializeObject<JsonRpcResponse>(json);

            return new RpcResponse<GetBlockTemplateResponse>(result.ResultAs<GetBlockTemplateResponse>());
        }

        private async Task ShowDaemonSyncProgressAsync(CancellationToken ct)
        {
            var response = await daemon.ExecuteAsync<GetInfoResponse>(logger, TariCommands.GetInfo, ct);
            var info = response.Response;

            if(info != null)
            {
                var lowestHeight = info.LocalHeight;
                var totalBlocks = info.TargetHeight;
                var percent = (double) lowestHeight / totalBlocks * 100;

                logger.Info(() => $"Daemons have downloaded {percent:0.00}% of blockchain from peers");
            }
        }

        private async Task UpdateNetworkStatsAsync(CancellationToken ct)
        {
            logger.LogInvoke();

            try
            {
                var infoResponse = await daemon.ExecuteAsync(logger, TariCommands.GetInfo, ct);

                if(infoResponse.Error != null)
                    logger.Warn(() => $"Error(s) refreshing network stats: {infoResponse.Error.Message} (Code {infoResponse.Error.Code})");

                if(infoResponse.Response != null)
                {
                    var info = infoResponse.Response.ToObject<GetInfoResponse>();

                    BlockchainStats.NetworkHashrate = (double) info.Difficulty;
                    BlockchainStats.ConnectedPeers = 1;
                }
            }

            catch(Exception e)
            {
                logger.Error(e);
            }
        }

        private async Task<bool> SubmitBlockAsync(Share share, string blobHex, string blobHash)
        {
            var response = await daemon.ExecuteAsync<SubmitResponse>(logger, TariCommands.SubmitBlock, CancellationToken.None, new[] { blobHex });

            if(response.Error != null || response?.Response?.Status != "OK")
            {
                var error = response.Error?.Message ?? response.Response?.Status;

                logger.Warn(() => $"Block {share.BlockHeight} [{blobHash[..6]}] submission failed with: {error}");
                messageBus.SendMessage(new AdminNotification("Block submission failed", $"Pool {poolConfig.Id} {(!string.IsNullOrEmpty(share.Source) ? $"[{share.Source.ToUpper()}] " : string.Empty)}failed to submit block {share.BlockHeight}: {error}"));
                return false;
            }

            return true;
        }

        #region API-Surface

        public IObservable<Unit> Blocks { get; private set; }

        public override void Configure(PoolConfig poolConfig, ClusterConfig clusterConfig)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(clusterConfig, nameof(clusterConfig));

            logger = LogUtil.GetPoolScopedLogger(typeof(JobManagerBase<TariJob>), poolConfig);
            this.poolConfig = poolConfig;
            this.clusterConfig = clusterConfig;
            coin = poolConfig.Template.As<TariCoinTemplate>();

            // extract standard daemon endpoints
            daemonEndpoints = poolConfig.Daemons
                .Where(x => string.IsNullOrEmpty(x.Category))
                .Select(x =>
                {
                    if(string.IsNullOrEmpty(x.HttpPath))
                        x.HttpPath = TariConstants.DaemonRpcLocation;

                    return x;
                })
                .ToArray();

            if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
            {
                // extract wallet daemon endpoints
                walletDaemonEndpoints = daemonEndpoints;

                if(walletDaemonEndpoints.Length == 0)
                    logger.ThrowLogPoolStartupException("Wallet-RPC daemon is not configured (Daemon configuration for monero-pools require an additional entry of category \'wallet' pointing to the wallet daemon)");
            }

            ConfigureDaemons();
        }

        public bool ValidateAddress(string address)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(address), $"{nameof(address)} must not be empty");
            return LibTari.ValidateAddress(address);
        }

        public BlockchainStats BlockchainStats { get; } = new BlockchainStats();

        public void PrepareWorkerJob(TariWorkerJob workerJob, out string blob, out string target)
        {
            blob = null;
            target = null;

            var job = currentJob;

            if(job != null)
            {
                lock(job)
                {
                    job.PrepareWorkerJob(workerJob, out blob, out target);
                }
            }
        }

        public async ValueTask<Share> SubmitShareAsync(StratumConnection worker,
            TariSubmitShareRequest request, TariWorkerJob workerJob, double stratumDifficultyBase, CancellationToken ct)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.RequiresNonNull(request, nameof(request));

            logger.LogInvoke(new[] { worker.ConnectionId });
            var context = worker.ContextAs<TariWorkerContext>();

            var job = currentJob;
            if(workerJob.Height != job?.BlockTemplate.Height)
                throw new StratumException(StratumError.MinusOne, "block expired");

            // validate & process
            var (share, blobHex) = job.ProcessShare(request.Nonce, request.Hash, worker);

            // enrich share with common data
            share.PoolId = poolConfig.Id;
            share.IpAddress = worker.RemoteEndpoint.Address.ToString();
            share.Miner = context.Miner;
            share.Worker = context.Worker;
            share.UserAgent = context.UserAgent;
            share.Source = clusterConfig.ClusterName;
            share.NetworkDifficulty = job.BlockTemplate.Difficulty;
            share.Created = clock.Now;

            // if block candidate, submit & check if accepted by network
            if(share.IsBlockCandidate)
            {
                logger.Info(() => $"Submitting block {share.BlockHeight} [{share.BlockHash.Substring(0, 6)}]");

                share.IsBlockCandidate = await SubmitBlockAsync(share, blobHex, share.BlockHash);

                if(share.IsBlockCandidate)
                {
                    logger.Info(() => $"Daemon accepted block {share.BlockHeight} [{share.BlockHash.Substring(0, 6)}] submitted by {context.Miner}");

                    OnBlockFound();

                    share.TransactionConfirmationData = share.BlockHash;
                }

                else
                {
                    // clear fields that no longer apply
                    share.TransactionConfirmationData = null;
                }
            }

            return share;
        }

        #endregion // API-Surface

        #region Overrides

        protected override void ConfigureDaemons()
        {
            var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();

            daemon = new RpcClient(daemonEndpoints.First(), jsonSerializerSettings, messageBus, poolConfig.Id);

            if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
            {
                // also setup wallet daemon
                walletDaemon = new RpcClient(daemonEndpoints.First(), jsonSerializerSettings, messageBus, poolConfig.Id);
            }
        }

        protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
        {
            // test json rpc to transcoder
            var response = await daemon.ExecuteAsync<GetInfoResponse>(logger, TariCommands.GetInfo, ct);

            if(response.Error != null)
                return false;

            return true;
        }

        protected override async Task<bool> AreDaemonsConnectedAsync(CancellationToken ct)
        {
            var response = await daemon.ExecuteAsync<GetInfoResponse>(logger, TariCommands.GetInfo, ct);

            return response.Error == null && response.Response != null;
        }

        protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
        {
            var syncPendingNotificationShown = false;

            while(true)
            {

                var response = await daemon.ExecuteAsync<GetInfoResponse>(logger, TariCommands.GetInfo, ct);

                var isSynched = response.Response.Synced;

                if(isSynched)
                {
                    logger.Info(() => $"All daemons synched with blockchain");
                    break;
                }

                if(!syncPendingNotificationShown)
                {
                    logger.Info(() => $"Daemons still syncing with network. Manager will be started once synced");
                    syncPendingNotificationShown = true;
                }

                await ShowDaemonSyncProgressAsync(ct);

                // delay retry by 5s
                await Task.Delay(5000, ct);
            }
        }

        protected override async Task PostStartInitAsync(CancellationToken ct)
        {
            SetInstanceId();

            var coin = poolConfig.Template.As<TariCoinTemplate>();
            var infoResponse = await daemon.ExecuteAsync(logger, TariCommands.GetInfo, ct);

            if(infoResponse.Error != null)
                logger.ThrowLogPoolStartupException($"Init RPC failed: {infoResponse.Error.Message} (Code {infoResponse.Error.Code})");

            // todo: chain detection
            //networkType = info.IsTestnet ? TariNetworkType.Test : TariNetworkType.Main;
            networkType = TariNetworkType.Main;

            if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
                ConfigureRewards();

            // update stats
            BlockchainStats.RewardType = "POW";
            BlockchainStats.NetworkType = networkType.ToString();

            await UpdateNetworkStatsAsync(ct);

            // Periodically update network stats
            Observable.Interval(TimeSpan.FromMinutes(1))
                .Select(via => Observable.FromAsync(async () =>
                {
                    try
                    {
                        await UpdateNetworkStatsAsync(ct);
                    }

                    catch(Exception ex)
                    {
                        logger.Error(ex);
                    }
                }))
                .Concat()
                .Subscribe();

            SetupJobUpdates(ct);
        }

        private void SetInstanceId()
        {
            instanceId = new byte[TariConstants.InstanceIdSize];

            using(var rng = RandomNumberGenerator.Create())
            {
                rng.GetNonZeroBytes(instanceId);
            }

            if(clusterConfig.InstanceId.HasValue)
                instanceId[0] = clusterConfig.InstanceId.Value;
        }


        private void ConfigureRewards()
        {
            // Donation to MiningCore development
            if(networkType == TariNetworkType.Main &&
                DevDonation.Addresses.TryGetValue(poolConfig.Template.Symbol, out var address))
            {
                poolConfig.RewardRecipients = poolConfig.RewardRecipients.Concat(new[]
                {
                    new RewardRecipient
                    {
                        Address = address,
                        Percentage = DevDonation.Percent,
                        Type = "dev"
                    }
                }).ToArray();
            }
        }

        protected virtual void SetupJobUpdates(CancellationToken ct)
        {
            var blockSubmission = blockFoundSubject.Synchronize();
            var pollTimerRestart = blockFoundSubject.Synchronize();

            var triggers = new List<IObservable<(string Via, string Data)>>
            {
                blockSubmission.Select(x => (JobRefreshBy.BlockFound, (string) null))
            };

            // periodically update block-template
            var pollingInterval = poolConfig.BlockRefreshInterval > 0 ? poolConfig.BlockRefreshInterval : 1000;
            triggers.Add(Observable.Timer(TimeSpan.FromMilliseconds(pollingInterval))
                        .TakeUntil(pollTimerRestart)
                        .Select(_ => (JobRefreshBy.Poll, (string) null))
                        .Repeat());

            Blocks = Observable.Merge(triggers)
                .Select(x => Observable.FromAsync(() => UpdateJob(ct, x.Via, x.Data)))
                .Concat()
                .Where(isNew => isNew)
                .Do(_ => hasInitialBlockTemplate = true)
                .Select(_ => Unit.Default)
                .Publish()
                .RefCount();
        }

        #endregion // Overrides
    }
}
