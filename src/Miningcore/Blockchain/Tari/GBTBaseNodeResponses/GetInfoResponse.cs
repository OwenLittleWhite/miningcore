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

using Newtonsoft.Json;

namespace Miningcore.Blockchain.Tari.DaemonResponses
{
    public class GetInfoResponse
    {
        /// <summary>
        /// Version of the blockchain
        /// </summary>
        [JsonProperty("blockchain_version")]
        public uint BlockchainVersion { get; set; }

        /// <summary>
        /// Hash of the highest block in the chain.
        /// </summary> 
        [JsonProperty("best_block")]
        public string TopBlockHash { get; set; }

        /// <summary>
        /// Longest time between blocks
        /// </summary>
        [JsonProperty("max_interval")]
        public uint MaxInterval { get; set; }

        /// <summary>
        /// Max transactions weight
        /// </summary>
        [JsonProperty("max_weight")]
        public uint MaxWeight { get; set; }

        /// <summary>
        /// Network difficulty for SHA3 algorithm
        /// </summary>
        [JsonProperty("min_diff")]
        public ulong Difficulty { get; set; }

        /// <summary>
        /// Current length of longest chain known to daemon.
        /// </summary>
        [JsonProperty("height_of_longest_chain")]
        public uint Height { get; set; }

        [JsonProperty("tip_height")]
        public uint TargetHeight { get; set; }

        [JsonProperty("local_height")]
        public uint LocalHeight { get; set; }

        /// <summary>
        /// Number of blocks the coinbase is locked for
        /// </summary> 
        [JsonProperty("lock_height")]
        public string LockHeight { get; set; }

        [JsonProperty("initial_sync_achieved")]
        public bool Synced { get; set; }

        /// <summary>
        /// General RPC error code. "OK" means everything looks good.
        /// </summary>
        public string Status { get; set; }
    }
}
