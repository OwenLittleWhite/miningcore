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
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Tari.Configuration;
using Miningcore.Configuration;
using System.Threading;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Contract = Miningcore.Contracts.Contract;
using GetInfoResponse = Miningcore.Blockchain.Tari.DaemonResponses.GetInfoResponse;
using GetBlockHeaderResponse = Miningcore.Blockchain.Tari.DaemonResponses.GetBlockHeaderResponse;
using GetBlockHeaderByHeightRequest = Miningcore.Blockchain.Tari.DaemonRequests.GetBlockHeaderByHeightRequest;
using TransferRequest = Miningcore.Blockchain.Tari.WalletRequests.TransferRequest;
using TransferResponse = Miningcore.Blockchain.Tari.WalletResponses.TransferResponse;
using GetBalanceResponse = Miningcore.Blockchain.Tari.WalletResponses.GetBalanceResponse;
using Miningcore.Blockchain.Tari.WalletRequests;
using Miningcore.Blockchain.Tari.GBTWalletRequests;
using Miningcore.Blockchain.Tari.GBTWalletResponses;
using Miningcore.JsonRpc;
using Miningcore.Mining;

namespace Miningcore.Blockchain.Tari
{
    [CoinFamily(CoinFamily.Tari)]
    public class TariPayoutHandler : PayoutHandlerBase,
        IPayoutHandler
    {
        public TariPayoutHandler(
            IComponentContext ctx,
            IConnectionFactory cf,
            IMapper mapper,
            IShareRepository shareRepo,
            IBlockRepository blockRepo,
            IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(balanceRepo, nameof(balanceRepo));
            Contract.RequiresNonNull(paymentRepo, nameof(paymentRepo));

            this.ctx = ctx;
        }

        private readonly IComponentContext ctx;
        private RpcClient daemon;
        private RpcClient walletDaemon;
        private TariNetworkType? networkType;
        private TariPoolPaymentProcessingConfigExtra extraConfig;

        protected override string LogCategory => "Tari Payout Handler";

        private async Task<bool> HandleTransferResponseAsync(RpcResponse<TransferResponse> response, params Balance[] balances)
        {
            var coin = poolConfig.Template.As<TariCoinTemplate>();

            if(response.Error == null)
            {
                var txResults = response.Response.TransactionResults;
                foreach (WalletResponses.TransactionResult txResult in txResults) {
                    if(txResult.Success == true)
                    {
                        var txHash = txResult.TxId;
                        var request = new GetTransactionFeeRequest
                        {
                            Transactions = new[]
                            {
                                new Transaction
                                {
                                TxId = txHash,
                                }
                            },
                        };
                        var feeResponse = await walletDaemon.ExecuteAsync<GetTransactionFeeResponse>(logger, TariWalletCommands.GetFee, CancellationToken.None, request);
                        var fee = feeResponse.Response.FeeResults.FirstOrDefault().Fee / coin.SmallestUnit;

                        logger.Info(() => $"[{LogCategory}] Payout transaction id: {txHash}, TxFee {FormatAmount(fee)}");
                        Balance[] balance = { Array.Find(balances, b => b.Address == txResult.Address) };
                        await PersistPaymentsAsync(balance, txHash);

                        NotifyPayoutSuccess(poolConfig.Id, balance, new[] { txHash }, fee);
                    } else
                    {
                        logger.Error(() => $"[{LogCategory}] Daemon command '{TariWalletCommands.Transfer}' returned error: {txResult.FailureMessage} for payment to {txResult.Address}");

                        NotifyPayoutFailure(poolConfig.Id, balances, $"Daemon command '{TariWalletCommands.Transfer}' returned error: {txResult.FailureMessage} for payment to {txResult.Address}", null);
                    }
                }
                return true;
            }

            else
            {
                logger.Error(() => $"[{LogCategory}] Daemon command '{TariWalletCommands.Transfer}' returned error: {response.Error.Message} code {response.Error.Code}");

                NotifyPayoutFailure(poolConfig.Id, balances, $"Daemon command '{TariWalletCommands.Transfer}' returned error: {response.Error.Message} code {response.Error.Code}", null);
                return false;
            }
        }

        private async Task<TariNetworkType> GetNetworkTypeAsync(CancellationToken ct)
        {
            if(!networkType.HasValue)
            {
                var infoResponse = await daemon.ExecuteAsync(logger, TariCommands.GetInfo, ct, true);
                var info = infoResponse.Response.ToObject<GetInfoResponse>();
                //TODO: network selection
                networkType = TariNetworkType.Main;
            }

            return networkType.Value;
        }

        private async Task<bool> EnsureBalance(decimal requiredAmount, TariCoinTemplate coin, CancellationToken ct)
        {
            var response = await walletDaemon.ExecuteAsync<GetBalanceResponse>(logger, TariWalletCommands.GetBalance, ct);

            if(response.Error != null)
            {
                logger.Error(() => $"[{LogCategory}] Daemon command '{TariWalletCommands.GetBalance}' returned error: {response.Error.Message} code {response.Error.Code}");
                return false;
            }

            var unlockedBalance = Math.Floor(response.Response.AvailableBalance / coin.SmallestUnit);
            var balance = Math.Floor((response.Response.AvailableBalance + response.Response.IncomingBalance) / coin.SmallestUnit);

            if(response.Response.AvailableBalance < requiredAmount)
            {
                logger.Info(() => $"[{LogCategory}] {FormatAmount(requiredAmount)} unlocked balance required for payment, but only have {FormatAmount(unlockedBalance)} of {FormatAmount(balance)} available yet. Will try again.");
                return false;
            }

            logger.Error(() => $"[{LogCategory}] Current balance is {FormatAmount(unlockedBalance)}");
            return true;
        }

        private async Task<bool> PayoutBatch(Balance[] balances, CancellationToken ct)
        {
            var coin = poolConfig.Template.As<TariCoinTemplate>();

            // ensure there's enough balance
            if(!await EnsureBalance(balances.Sum(x => x.Amount), coin, ct))
                return false;

            // build request
            var request = new TransferRequest
            {
                Destinations = balances
                    .Where(x =>  x.Amount > 0)
                    .Select(x =>
                    {
                        ExtractAddressAndPaymentId(x.Address, out var address, out var paymentId);
                        return new Recipient
                        {
                            Address = address,
                            Amount = (ulong) Math.Floor(x.Amount * coin.SmallestUnit),
                            FeePerGram = 25,
                            Message = "Tari Pool Payout - Miningcore"
                        };
                    }).ToArray(),
            };

            if(request.Destinations.Length == 0)
                return true;

            logger.Info(() => $"[{LogCategory}] Paying out {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses:\n{string.Join("\n", balances.OrderByDescending(x => x.Amount).Select(x => $"{FormatAmount(x.Amount)} to {x.Address}"))}");

            // send command
            var transferResponse = await walletDaemon.ExecuteAsync<TransferResponse>(logger, TariWalletCommands.Transfer, ct, request);

            return await HandleTransferResponseAsync(transferResponse, balances);
        }

        private void ExtractAddressAndPaymentId(string input, out string address, out string paymentId)
        {
            paymentId = null;
            var index = input.IndexOf(PayoutConstants.PayoutInfoSeperator);

            if(index != -1)
            {
                address = input.Substring(0, index);

                if(index + 1 < input.Length)
                {
                    paymentId = input.Substring(index + 1);

                    // ignore invalid payment ids
                    if(paymentId.Length != TariConstants.PaymentIdHexLength)
                        paymentId = null;
                }
            }

            else
                address = input;
        }

        private async Task<bool> PayoutToPaymentId(Balance balance, CancellationToken ct)
        {
            var coin = poolConfig.Template.As<TariCoinTemplate>();

            ExtractAddressAndPaymentId(balance.Address, out var address, out var paymentId);
            var isIntegratedAddress = string.IsNullOrEmpty(paymentId);

            // ensure there's enough balance
            if(!await EnsureBalance(balance.Amount, coin, ct))
                return false;

            // build request
            var request = new TransferRequest
            {
                Destinations = new[]
                {
                    new Recipient
                    {
                        Address = address,
                        Amount = (ulong) Math.Floor(balance.Amount * coin.SmallestUnit),
                        FeePerGram = 25,
                        Message = "Tari Mining Pool Payout - Miningcore"
                    }
                },
            };

            logger.Info(() => $"[{LogCategory}] Paying out {FormatAmount(balance.Amount)} to address {balance.Address} with paymentId {paymentId}");

            // send command
            var result = await walletDaemon.ExecuteAsync<TransferResponse>(logger, TariWalletCommands.Transfer, ct, request);

            return await HandleTransferResponseAsync(result, balance);
        }

        #region IPayoutHandler

        public async Task ConfigureAsync(ClusterConfig clusterConfig, PoolConfig poolConfig, CancellationToken ct)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));

            this.poolConfig = poolConfig;
            this.clusterConfig = clusterConfig;
            extraConfig = poolConfig.PaymentProcessing.Extra.SafeExtensionDataAs<TariPoolPaymentProcessingConfigExtra>();

            logger = LogUtil.GetPoolScopedLogger(typeof(TariPayoutHandler), poolConfig);

            // configure standard daemon
            var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();

            var daemonEndpoints = poolConfig.Daemons
                .Where(x => string.IsNullOrEmpty(x.Category))
                .Select(x =>
                {
                    if(string.IsNullOrEmpty(x.HttpPath))
                        x.HttpPath = TariConstants.DaemonRpcLocation;

                    return x;
                })
                .ToArray();

            daemon = new RpcClient(daemonEndpoints.First(), jsonSerializerSettings, messageBus, poolConfig.Id);

            // configure wallet daemon
            var walletDaemonEndpoints = daemonEndpoints;

            walletDaemon = new RpcClient(daemonEndpoints.First(), jsonSerializerSettings, messageBus, poolConfig.Id);

            // detect network
            await GetNetworkTypeAsync(ct);

        }

        public async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(blocks, nameof(blocks));

            var coin = poolConfig.Template.As<TariCoinTemplate>();
            var pageSize = 100;
            var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
            var result = new List<Block>();

            for(var i = 0; i < pageCount; i++)
            {
                // get a page full of blocks
                var page = blocks
                    .Skip(i * pageSize)
                    .Take(pageSize)
                    .ToArray();

                for(var j = 0; j < page.Length; j++)
                {
                    var block = page[j];

                    var rpcResult = await daemon.ExecuteAsync<GetBlockHeaderResponse>(logger,
                        TariCommands.GetBlockHeaderByHeight,
                        ct,
                        new GetBlockHeaderByHeightRequest
                        {
                            Height = block.BlockHeight
                        });

                    if(rpcResult.Error != null)
                    {
                        logger.Debug(() => $"[{LogCategory}] Daemon reports error '{rpcResult.Error.Message}' (Code {rpcResult.Error.Code}) for block {block.BlockHeight}");
                        continue;
                    }

                    if(rpcResult.Response?.BlockHeader == null)
                    {
                        logger.Debug(() => $"[{LogCategory}] Daemon returned no header for block {block.BlockHeight}");
                        continue;
                    }

                    var blockHeader = rpcResult.Response.BlockHeader;

                    // update progress
                    block.ConfirmationProgress = Math.Min(1.0d, (double) blockHeader.Depth / TariConstants.PayoutMinBlockConfirmations);
                    result.Add(block);

                    messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);

                    // orphaned?
                    if(blockHeader.IsOrphaned || blockHeader.Hash != block.TransactionConfirmationData)
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                        continue;
                    }

                    // matured and spendable?
                    if(blockHeader.Depth >= TariConstants.PayoutMinBlockConfirmations)
                    {
                        block.Status = BlockStatus.Confirmed;
                        block.ConfirmationProgress = 1;
                        block.Reward = ((decimal) blockHeader.Reward / coin.SmallestUnit) * coin.BlockrewardMultiplier;

                        logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }
                }
            }

            return result.ToArray();
        }

        public Task CalculateBlockEffortAsync(IMiningPool pool, Block block, double accumulatedBlockShareDiff, CancellationToken ct)
        {
            block.Effort = accumulatedBlockShareDiff / block.NetworkDifficulty;

            return Task.FromResult(true);
        }

        public override async Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx,
            IMiningPool pool, Block block, CancellationToken ct)
        {
            var blockRewardRemaining = await base.UpdateBlockRewardBalancesAsync(con, tx, pool, block, ct);

            // Deduct static reserve for tx fees
            blockRewardRemaining -= TariConstants.StaticTransactionFeeReserve;

            return blockRewardRemaining;
        }

        public async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
        {
            Contract.RequiresNonNull(balances, nameof(balances));

            var coin = poolConfig.Template.As<TariCoinTemplate>();

            // validate addresses
            balances = balances
                .Where(x =>
                {
                    ExtractAddressAndPaymentId(x.Address, out var address, out var paymentId);
                    switch(networkType)
                    {
                        //Todo: Network Handling, currently testnet in all cases
                        case TariNetworkType.Main:
                            break;

                        case TariNetworkType.Test:
                            break;
                    }

                    return Native.LibTari.ValidateAddress(address);
                })
                .ToArray();

            // simple balances first
            var simpleBalances = balances
                .Where(x =>
                {
                    ExtractAddressAndPaymentId(x.Address, out var address, out var paymentId);
                    if (!Native.LibTari.ValidateAddress(address))
                    {
                        return false;
                    }

                    var hasPaymentId = paymentId != null;

                    //Todo: Network Handling, currently testnet in all cases
                    switch(networkType)
                    {
                        case TariNetworkType.Main:
                            break;

                        case TariNetworkType.Test:
                            break;
                    }

                    return !hasPaymentId;
                })
                .OrderByDescending(x => x.Amount)
                .ToArray();

            if(simpleBalances.Length > 0)
#if false
                await PayoutBatch(simpleBalances);
#else
            {
                var maxBatchSize = 15; 
                var pageSize = maxBatchSize;
                var pageCount = (int) Math.Ceiling((double) simpleBalances.Length / pageSize);

                for(var i = 0; i < pageCount; i++)
                {
                    var page = simpleBalances
                        .Skip(i * pageSize)
                        .Take(pageSize)
                        .ToArray();

                    if(!await PayoutBatch(page, ct))
                        break;
                }
            }
#endif
            // balances with paymentIds
            var minimumPaymentToPaymentId = 0.1m;
            if(extraConfig != null)
            {
                minimumPaymentToPaymentId = extraConfig.MinimumPaymentToPaymentId;
            }
            else
            {
                minimumPaymentToPaymentId = poolConfig.PaymentProcessing.MinimumPayment;
            }

            var paymentIdBalances = balances.Except(simpleBalances)
                .Where(x => x.Amount >= minimumPaymentToPaymentId)
                .ToArray();

            foreach(var balance in paymentIdBalances)
            {
                if(!await PayoutToPaymentId(balance, ct))
                    break;
            }

        }

        #endregion // IPayoutHandler
    }
}
