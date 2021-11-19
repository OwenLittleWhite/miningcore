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
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Tari.StratumRequests;
using Miningcore.Blockchain.Tari.StratumResponses;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Tari
{
    [CoinFamily(CoinFamily.Tari)]
    public class TariPool : PoolBase
    {
        public TariPool(IComponentContext ctx,
            JsonSerializerSettings serializerSettings,
            IConnectionFactory cf,
            IStatsRepository statsRepo,
            IMapper mapper,
            IMasterClock clock,
            IMessageBus messageBus,
            NicehashService nicehashService) :
            base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, nicehashService)
        {
        }

        private long currentJobId;

        private TariJobManager manager;

        private async Task OnLoginAsync(StratumConnection client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;
            var context = client.ContextAs<TariWorkerContext>();

            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            var loginRequest = request.ParamsAs<TariLoginRequest>();

            if(string.IsNullOrEmpty(loginRequest?.Login))
                throw new StratumException(StratumError.MinusOne, "missing login");

            // extract worker/miner/paymentid
            var split = loginRequest.Login.ToLower().Split('.');
            context.Miner = split.Length > 1 ? split[0] : loginRequest.Login;
            context.Worker = split.Length > 1 ? split[1].Trim() : null;
            context.UserAgent = loginRequest.UserAgent?.Trim();

            var addressToValidate = context.Miner;

            // extract paymentid
            var index = context.Miner.IndexOf('#');
            if(index != -1)
            {
                var paymentId = context.Miner.Substring(index + 1).Trim();

                // validate
                if(!string.IsNullOrEmpty(paymentId) && paymentId.Length != TariConstants.PaymentIdHexLength)
                    throw new StratumException(StratumError.MinusOne, "invalid payment id");

                // re-append to address
                addressToValidate = context.Miner.Substring(0, index).Trim();
                context.Miner = addressToValidate + PayoutConstants.PayoutInfoSeperator + paymentId;
            }

            // validate login
            var result = manager.ValidateAddress(addressToValidate);
            if(!result)
                throw new StratumException(StratumError.MinusOne, "invalid login");

            context.IsSubscribed = result;
            context.IsAuthorized = result;

            if (context.IsAuthorized)
            {
                // extract control vars from password
                var passParts = loginRequest.Password?.Split(PasswordControlVarsSeparator);
                var staticDiff = GetStaticDiffFromPassparts(passParts);

                var nicehashDiff = await GetNicehashStaticMinDiff(client, context.UserAgent, manager.Coin.Name, manager.Coin.GetAlgorithmName());

                if(nicehashDiff.HasValue)
                {
                    if(!staticDiff.HasValue || nicehashDiff > staticDiff)
                    {
                        logger.Info(() => $"[{client.ConnectionId}] Nicehash detected. Using API supplied difficulty of {nicehashDiff.Value}");

                        staticDiff = nicehashDiff;
                    }

                    else
                        logger.Info(() => $"[{client.ConnectionId}] Nicehash detected. Using miner supplied difficulty of {staticDiff.Value}");
                }

                if(staticDiff.HasValue &&
                    (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                     context.VarDiff == null && staticDiff.Value > context.Difficulty))
                {
                    context.VarDiff = null; // disable vardiff
                    context.SetDifficulty(staticDiff.Value);

                    logger.Info(() => $"[{client.ConnectionId}] Setting static difficulty of {staticDiff.Value}");
                }
                else
                {
                    context.SetDifficulty(context.VarDiff.Config.MinDiff);
                }

                // respond
                var loginResponse = new TariLoginResponse
                {
                    Id = client.ConnectionId,
                    Job = CreateWorkerJob(client)
                };

                await client.RespondAsync(loginResponse, request.Id);

                // log association
                if(!string.IsNullOrEmpty(context.Worker))
                    logger.Info(() => $"[{client.ConnectionId}] Authorized worker {context.Worker}@{context.Miner}");
                else
                    logger.Info(() => $"[{client.ConnectionId}] Authorized miner {context.Miner}");
            }
            else {
                await client.RespondErrorAsync(StratumError.MinusOne, "invalid login", request.Id);

                logger.Info(() => $"[{client.ConnectionId}] Banning unauthorized worker {context.Miner} for {loginFailureBanTimeout.TotalSeconds} sec");

                banManager.Ban(client.RemoteEndpoint.Address, loginFailureBanTimeout);

                CloseConnection(client);
            }
        }

        private async Task OnGetJobAsync(StratumConnection client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;
            var context = client.ContextAs<TariWorkerContext>();

            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            var getJobRequest = request.ParamsAs<TariGetJobRequest>();

            // validate worker
            if(client.ConnectionId != getJobRequest?.WorkerId || !context.IsAuthorized)
                throw new StratumException(StratumError.MinusOne, "unauthorized");

            // respond
            var job = CreateWorkerJob(client);
            await client.RespondAsync(job, request.Id);
        }

        private TariJobParams CreateWorkerJob(StratumConnection client)
        {
            var context = client.ContextAs<TariWorkerContext>();
            var job = new TariWorkerJob(NextJobId(), context.Difficulty);

            manager.PrepareWorkerJob(job, out var blob, out var target);

            // should never happen
            if(string.IsNullOrEmpty(blob) || string.IsNullOrEmpty(blob))
                return null;

            var result = new TariJobParams
            {
                JobId = job.Id,
                Blob = blob,
                Target = target,
                Height = job.Height,
            };

            // update context
            lock(context)
            {
                context.AddJob(job);
            }

            return result;
        }

        private async Task OnSubmitAsync(StratumConnection client, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
        {
            var request = tsRequest.Value;
            var context = client.ContextAs<TariWorkerContext>();

            try
            {
                if(request.Id == null)
                    throw new StratumException(StratumError.MinusOne, "missing request id");

                // check age of submission (aged submissions are usually caused by high server load)
                var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;

                if(requestAge > maxShareAge)
                {
                    logger.Warn(() => $"[{client.ConnectionId}] Dropping stale share submission request (server overloaded?)");
                    return;
                }

                // check request
                var submitRequest = request.ParamsAs<TariSubmitShareRequest>();

                // validate worker
                if(client.ConnectionId != submitRequest?.WorkerId || !context.IsAuthorized)
                    throw new StratumException(StratumError.MinusOne, "unauthorized");

                // recognize activity
                context.LastActivity = clock.Now;

                TariWorkerJob job;

                lock(context)
                {
                    var jobId = submitRequest?.JobId;

                    if((job = context.FindJob(jobId.ToString())) == null)
                        throw new StratumException(StratumError.MinusOne, "invalid jobid");
                }

                // dupe check
                if(!job.Submissions.TryAdd(submitRequest.Nonce.ToString(), true))
                    throw new StratumException(StratumError.MinusOne, "duplicate share");

                var poolEndpoint = poolConfig.Ports[client.LocalEndpoint.Port];

                var share = await manager.SubmitShareAsync(client, submitRequest, job, poolEndpoint.Difficulty, ct);
                await client.RespondAsync(new TariResponseBase(), request.Id);

                // publish
                messageBus.SendMessage(new StratumShare(client, share));

                // telemetry
                PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

                logger.Info(() => $"[{client.ConnectionId}] Share accepted: D={Math.Round(share.Difficulty, 3)}");

                // update pool stats
                if(share.IsBlockCandidate)
                    poolStats.LastPoolBlockTime = clock.Now;

                // update client stats
                context.Stats.ValidShares++;
                await UpdateVarDiffAsync(client);
            }

            catch(StratumException ex)
            {
                // telemetry
                PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

                // update client stats
                context.Stats.InvalidShares++;
                logger.Info(() => $"[{client.ConnectionId}] Share rejected: {ex.Message} [{context.UserAgent}]");

                // banning
                ConsiderBan(client, context, poolConfig.Banning);

                throw;
            }
        }

        private string NextJobId()
        {
            return Interlocked.Increment(ref currentJobId).ToString(CultureInfo.InvariantCulture);
        }

        private Task OnNewJobAsync()
        {
            logger.Info(() => "Broadcasting job");

            return Guard(() => Task.WhenAll(ForEachConnection(async connection =>
            {
                if(!connection.IsAlive)
                    return;

                var context = connection.ContextAs<TariWorkerContext>();

                if(!context.IsSubscribed || !context.IsAuthorized || CloseIfDead(connection, context))
                    return;

                // send job
                var job = CreateWorkerJob(connection);
                await connection.NotifyAsync(TariStratumMethods.JobNotify, job);
            })), ex => logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"));
        }

        #region Overrides

        protected override async Task SetupJobManager(CancellationToken ct)
        {
            manager = ctx.Resolve<TariJobManager>();
            manager.Configure(poolConfig, clusterConfig);

            await manager.StartAsync(ct);
            
            if(poolConfig.EnableInternalStratum == true)
            {
                disposables.Add(manager.Blocks
                    .Select(_ => Observable.FromAsync(async () =>
                    {
                        try
                        {
                            await OnNewJobAsync();
                        }

                        catch(Exception ex)
                        {
                            logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}");
                        }
                    }))
                    .Concat()
                    .Subscribe(_ => { }, ex =>
                    {
                        logger.Debug(ex, nameof(OnNewJobAsync));
                    }));

                // we need work before opening the gates
                await manager.Blocks.Take(1).ToTask(ct);
            }

            else
            {
                // keep updating NetworkStats
                disposables.Add(manager.Blocks.Subscribe());
            }
            
        }

        protected override async Task InitStatsAsync()
        {
            await base.InitStatsAsync();

            blockchainStats = manager.BlockchainStats;
        }

        protected override WorkerContextBase CreateWorkerContext()
        {
            return new TariWorkerContext();
        }

        protected override async Task OnRequestAsync(StratumConnection client,
            Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
        {
            var request = tsRequest.Value;
            var context = client.ContextAs<TariWorkerContext>();

            try
            {
                switch(request.Method)
                {
                    case TariStratumMethods.Login:
                        await OnLoginAsync(client, tsRequest);
                        break;

                    case TariStratumMethods.GetJob:
                        await OnGetJobAsync(client, tsRequest);
                        break;

                    case TariStratumMethods.Submit:
                        await OnSubmitAsync(client, tsRequest, ct);
                        break;

                    case TariStratumMethods.KeepAlive:
                        // recognize activity
                        context.LastActivity = clock.Now;
                        break;

                    default:
                        logger.Debug(() => $"[{client.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                        await client.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                        break;
                }
            }

            catch(StratumException ex)
            {
                await client.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
            }
        }

        public override double ShareMultiplier => 1;

        public override double HashrateFromShares(double shares, double interval)
        {
            var result = shares / interval;
            return result;
        }

        protected override async Task OnVarDiffUpdateAsync(StratumConnection client, double newDiff)
        {
            await base.OnVarDiffUpdateAsync(client, newDiff);

            // apply immediately and notify client
            var context = client.ContextAs<TariWorkerContext>();

            if(context.HasPendingDifficulty)
            {
                context.ApplyPendingDifficulty();

                // re-send job
                var job = CreateWorkerJob(client);
                await client.NotifyAsync(TariStratumMethods.JobNotify, job);
            }
        }

        #endregion // Overrides
    }
}
