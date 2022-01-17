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

namespace Miningcore.Blockchain.Tari
{
    public enum TariNetworkType
    {
        Main = 1,
        Test
    }

    public class TariConstants
    {
        public const string WalletDaemonCategory = "wallet";

        public const string DaemonRpcLocation = "json_rpc";
        public const int RpcMethodNotFound = -32601;
        public const int PaymentIdHexLength = 64;

#if DEBUG
        public const int PayoutMinBlockConfirmations = 3;
#else
        public const int PayoutMinBlockConfirmations = 60;
#endif

        public const int InstanceIdSize = 3;

        public const decimal StaticTransactionFeeReserve = 0.05m; // in Tari
    }

    public static class TariCommands
    {
        public const string GetInfo = "get_info";
        public const string GetBlockTemplate = "getblocktemplate";
        public const string SubmitBlock = "submitblock";
        public const string GetBlockHeaderByHeight = "getblockheaderbyheight";
    }

    public static class TariWalletCommands
    {
        public const string GetBalance = "get_balance";
        public const string Transfer = "transfer";
        public const string GetFee = "get_fee";
    }
}
