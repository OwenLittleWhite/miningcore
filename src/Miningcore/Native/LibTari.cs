using Miningcore.Contracts;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Miningcore.Native
{
    public static unsafe class LibTari
    {
        [DllImport("tari_stratum_ffi", EntryPoint = "public_key_hex_validate", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool public_key_hex_validate(char* ptsPt, int* error_ptr);

        public static bool ValidateAddress(string address)
        {
            bool validated = false;
            unsafe
            {
                var error = -1;
                int* error_ptr = &error;
                IntPtr ptS = Marshal.StringToHGlobalAnsi(address.ToLower());
                char* ptsPt = (char*) ptS.ToPointer();
                validated = LibTari.public_key_hex_validate(ptsPt, error_ptr);
            }
            return validated;
        }

        [DllImport("tari_stratum_ffi", EntryPoint = "inject_nonce", CallingConvention = CallingConvention.Cdecl)]
        private static extern char* inject_nonce(char* ptsTemplate, ulong nonce, int* error_ptr);

        public static (String, int) InjectNonce(string template, ulong nonce)
        {
            unsafe
            {
                var error = -1;
                int* error_ptr = &error;
                IntPtr ptS = Marshal.StringToHGlobalAnsi(template.ToLower());
                char* ptsPt = (char*) ptS.ToPointer();
                char* result = LibTari.inject_nonce(ptsPt, nonce, error_ptr);
                return (Marshal.PtrToStringAnsi((IntPtr) result),error);
            }
        }

        [DllImport("tari_stratum_ffi", EntryPoint = "share_validate", CallingConvention = CallingConvention.Cdecl)]
        private static extern int share_validate(char* ptsBlock, char* ptsHash, ulong stratum_diff, ulong template_diff, int* error_ptr);

        public static (int,int) ShareValidate(string block, string hash, ulong stratumDifficulty, ulong templateDifficulty)
        {
            unsafe
            {
                var error = -1;
                int* error_ptr = &error;
                IntPtr ptS = Marshal.StringToHGlobalAnsi(block.ToLower());
                char* ptsPt = (char*) ptS.ToPointer();
                IntPtr ptS2 = Marshal.StringToHGlobalAnsi(hash.ToLower());
                char* ptsPt2 = (char*) ptS2.ToPointer();
                int result = LibTari.share_validate(ptsPt, ptsPt2, stratumDifficulty, templateDifficulty, error_ptr);
                return (result,error);
            }
        }

        [DllImport("tari_stratum_ffi", EntryPoint = "share_difficulty", CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong share_difficulty(char* ptsBlock, int* error_ptr);
        public static (ulong, int) ShareDifficulty(string block)
        {
            unsafe
            {
                var error = -1;
                int* error_ptr = &error;
                IntPtr ptS = Marshal.StringToHGlobalAnsi(block.ToLower());
                char* ptsPt = (char*) ptS.ToPointer();
                ulong result = LibTari.share_difficulty(ptsPt, error_ptr);
                return (result, error);
            }
        }
    }
}
