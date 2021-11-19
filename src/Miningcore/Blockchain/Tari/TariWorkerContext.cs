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


using System.Collections.Generic;
using System.Linq;
using Miningcore.Mining;

namespace Miningcore.Blockchain.Tari
{
    public class TariWorkerContext : WorkerContextBase
    {
        /// <summary>
        /// Usually a wallet address
        /// NOTE: May include paymentid (seperated by a dot .)
        /// </summary>
        public string Miner { get; set; }

        /// <summary>
        /// Arbitrary worker identififer for miners using multiple rigs
        /// </summary>
        public string Worker { get; set; }

        private List<TariWorkerJob> validJobs { get; } = new();

        public void AddJob(TariWorkerJob job)
        {
            validJobs.Insert(0, job);

            while(validJobs.Count > 4)
                validJobs.RemoveAt(validJobs.Count - 1);
        }

        public TariWorkerJob FindJob(string jobId)
        {
            return validJobs.FirstOrDefault(x => x.Id == jobId);
        }
    }
}
