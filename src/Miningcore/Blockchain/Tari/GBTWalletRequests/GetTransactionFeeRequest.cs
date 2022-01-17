using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace Miningcore.Blockchain.Tari.GBTWalletRequests
{
    public class Transaction
    {
        [JsonProperty("transaction_id")]
        public string TxId { get; set; }
    }

    public class GetTransactionFeeRequest
    {
        public Transaction[] Transactions { get; set; }

    }
}
