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
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Native;
using Miningcore.Stratum;
using Contract = Miningcore.Contracts.Contract;
using GetBlockTemplateResponse = Miningcore.Blockchain.Tari.DaemonResponses.GetBlockTemplateResponse;

namespace Miningcore.Blockchain.Tari
{
    public class TariJob
    {
        public TariJob(GetBlockTemplateResponse blockTemplate, byte[] instanceId, string jobId,
            TariCoinTemplate coin, PoolConfig poolConfig, ClusterConfig clusterConfig, string prevHash)
        {
            Contract.RequiresNonNull(blockTemplate, nameof(blockTemplate));
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(clusterConfig, nameof(clusterConfig));
            Contract.RequiresNonNull(instanceId, nameof(instanceId));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId), $"{nameof(jobId)} must not be empty");

            this.coin = coin;
            BlockTemplate = blockTemplate;
            PrepareBlobTemplate(instanceId);
            PrevHash = prevHash;
        }

        private byte[] blobTemplate;
        private readonly TariCoinTemplate coin;

        private void PrepareBlobTemplate(byte[] instanceId)
        {
            blobTemplate = BlockTemplate.Header.HexToByteArray();
        }

        private string EncodeBlob()
        {
            Span<byte> blob = stackalloc byte[blobTemplate.Length];
            blobTemplate.CopyTo(blob);
            return blob.ToHexString();
        }

        #region API-Surface

        public string PrevHash { get; }
        public GetBlockTemplateResponse BlockTemplate { get; }

        public void PrepareWorkerJob(TariWorkerJob workerJob, out string blob, out string target)
        {
            workerJob.Height = BlockTemplate.Height;

            blob = EncodeBlob();
            target = Math.Ceiling(workerJob.Difficulty).ToString();
        }

        public (Share Share, string BlobHex) ProcessShare(ulong nonce, string workerHash, StratumConnection worker)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(workerHash), $"{nameof(workerHash)} must not be empty");

            var context = worker.ContextAs<TariWorkerContext>();

            var blob = BlockTemplate.Blob;

            var (block, error) = LibTari.InjectNonce(blob.ToString(), nonce);
            if(error != 0)
            {
                throw new StratumException(StratumError.MinusOne, "malformed blob");
            }

            var (shareResult, error2) = LibTari.ShareValidate(block, workerHash, Convert.ToUInt64(Math.Ceiling(context.Difficulty)), BlockTemplate.Difficulty);
            if (error2 != 0) // Should not occur if above succeeds, but always check error
            {
                throw new StratumException(StratumError.MinusOne, "malformed blob");
            }
            var isBlockCandidate = shareResult == 0;

            var stratumDifficulty = Math.Ceiling(context.Difficulty);
            var (shareDiff, error3) = LibTari.ShareDifficulty(block);
            if(error3 != 0) // Should not occur if above succeeds, but always check error
            {
                throw new StratumException(StratumError.MinusOne, "malformed blob");
            }
            var ratio = shareDiff / Convert.ToUInt64(stratumDifficulty);
            // test if share meets at least workers current difficulty

            if(!isBlockCandidate && ratio < 0.99)
            {
                // check if share matched the previous difficulty from before a vardiff retarget
                if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
                {
                    ratio = shareDiff / Convert.ToUInt64(Math.Ceiling(context.PreviousDifficulty.Value));

                    if(ratio < 0.99)
                        throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                    // use previous difficulty
                    stratumDifficulty = Math.Ceiling(context.PreviousDifficulty.Value);
                }

                else
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
            }

            var result = new Share
            {
                BlockHeight = BlockTemplate.Height,
                Difficulty = stratumDifficulty,
            };

            if(isBlockCandidate)
            {

                // Fill in block-relevant fields
                result.IsBlockCandidate = true;
                result.BlockHash = workerHash;
            }

            return (result, block.ToString());
        }

        #endregion // API-Surface
    }
}
