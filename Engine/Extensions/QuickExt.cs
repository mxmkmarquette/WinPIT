﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Engine.ProcessCore;

namespace Engine.Extensions
{
    public enum HashType
    {
        MD5,
        RIPEMD160,
        SHA1,
        SHA256,
        SHA384,
        SHA512
    }

    public enum CRCType
    {
        CRC8,
        CRC16,
        CRC16_ModBus,
        CRC16_Sick,
        CRC_CCITT_XModem,
        CRC_CCITT_0xFFFF,
        CRC_CCITT_0x1D0F,
        CRC_CCITT_Kermit,
        CRC_DNP,
        CRC32
    }

    public static class QuickExt
    {
        public static string GetHash(byte[] toGetHashFrom, HashType hType)
        {
            byte[] hashed = null;
            string hashedHex = String.Empty;
            switch (hType)
            {
                case HashType.MD5:
                    hashed = new MD5CryptoServiceProvider().ComputeHash(toGetHashFrom);
                    hashedHex = hashed.GetHex();
                    break;
                case HashType.RIPEMD160:
                    hashed = new RIPEMD160Managed().ComputeHash(toGetHashFrom);
                    hashedHex = hashed.GetHex();
                    break;
                case HashType.SHA1:
                    hashed = new SHA1CryptoServiceProvider().ComputeHash(toGetHashFrom);
                    hashedHex = hashed.GetHex();
                    break;
                case HashType.SHA256:
                    hashed = new SHA256CryptoServiceProvider().ComputeHash(toGetHashFrom);
                    hashedHex = hashed.GetHex();
                    break;
                case HashType.SHA384:
                    hashed = new SHA384CryptoServiceProvider().ComputeHash(toGetHashFrom);
                    hashedHex = hashed.GetHex();
                    break;
                case HashType.SHA512:
                    hashed = new SHA512CryptoServiceProvider().ComputeHash(toGetHashFrom);
                    hashedHex = hashed.GetHex();
                    break;
            }

            return hashedHex;
        }
    }
}