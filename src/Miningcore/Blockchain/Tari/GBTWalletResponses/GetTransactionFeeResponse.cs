using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace Miningcore.Blockchain.Tari.GBTWalletResponses
{
    public class FeeResult
    {
        [JsonProperty("transaction_id", DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue(0)]
        public string TxId { get; set; }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        [DefaultValue(0)]
        public ulong Fee { get; set; }
    }
    public class GetTransactionFeeResponse
    {
        [JsonProperty("fee_results", DefaultValueHandling = DefaultValueHandling.Populate)]
        public List<FeeResult> FeeResults { get; set; }

    }
}
