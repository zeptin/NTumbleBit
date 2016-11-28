﻿using NBitcoin;
using NTumbleBit.BouncyCastle.Math;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NTumbleBit.PuzzlePromise
{
    public class SignatureKey
    {
		public SignatureKey(byte[] key)
		{
			if(key == null)
				throw new ArgumentNullException("key");
			if(key.Length != KeySize)
				throw new ArgumentException("Key has invalid length from expected " + KeySize);
			_Value = new BigInteger(1, key);
		}

		internal SignatureKey(BigInteger value)
		{
			if(value == null)
				throw new ArgumentNullException("value");

			_Value = value;
		}

		BigInteger _Value;

		public byte[] XOR(byte[] data)
		{
			byte[] keyBytes = ToBytes();
			var keyHash = PromiseUtils.SHA512(keyBytes, 0, keyBytes.Length);
			var encrypted = new byte[data.Length];
			for(int i = 0; i < encrypted.Length; i++)
			{

				encrypted[i] = (byte)(data[i] ^ keyHash[i % keyHash.Length]);
			}
			return encrypted;
		}


		const int KeySize = 256;
		public byte[] ToBytes()
		{
			byte[] keyBytes = _Value.ToByteArrayUnsigned();
			Utils.Pad(ref keyBytes, KeySize);
			return keyBytes;
		}
    }
}
