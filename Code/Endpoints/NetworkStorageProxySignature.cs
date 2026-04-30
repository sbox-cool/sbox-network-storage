using System;
using System.Text;

namespace Sandbox;

public static partial class NetworkStorage
{
	/// <summary>
	/// Compute HMAC-SHA256 proxy signature scoped to project + endpoint + client.
	/// Must produce the same hex string as the backend's computeProxySignature().
	/// Format: HMAC-SHA256(apiKey, "projectId:endpointSlug:clientSteamId:clientToken")
	///
	/// Uses a pure managed SHA-256 + HMAC implementation because s&amp;box's whitelist
	/// blocks System.Security.Cryptography.
	/// </summary>
	private static string ComputeProxySignature( string apiKey, string projectId, string endpointSlug, string clientSteamId, string clientToken )
	{
		var data = $"{projectId}:{endpointSlug}:{clientSteamId}:{clientToken}";
		var keyBytes = Encoding.UTF8.GetBytes( apiKey );
		var dataBytes = Encoding.UTF8.GetBytes( data );
		var hash = HmacSha256( keyBytes, dataBytes );

		var sb = new StringBuilder( hash.Length * 2 );
		foreach ( var b in hash )
			sb.Append( b.ToString( "x2" ) );
		return sb.ToString();
	}

	private static byte[] HmacSha256( byte[] key, byte[] message )
	{
		const int blockSize = 64;

		if ( key.Length > blockSize )
			key = Sha256( key );

		var paddedKey = new byte[blockSize];
		Array.Copy( key, paddedKey, key.Length );

		var ipad = new byte[blockSize];
		var opad = new byte[blockSize];
		for ( int i = 0; i < blockSize; i++ )
		{
			ipad[i] = (byte)(paddedKey[i] ^ 0x36);
			opad[i] = (byte)(paddedKey[i] ^ 0x5c);
		}

		var inner = new byte[blockSize + message.Length];
		Array.Copy( ipad, 0, inner, 0, blockSize );
		Array.Copy( message, 0, inner, blockSize, message.Length );
		var innerHash = Sha256( inner );

		var outer = new byte[blockSize + 32];
		Array.Copy( opad, 0, outer, 0, blockSize );
		Array.Copy( innerHash, 0, outer, blockSize, 32 );
		return Sha256( outer );
	}

	private static readonly uint[] _sha256K = {
		0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
		0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
		0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
		0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
		0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
		0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
		0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
		0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
	};

	private static byte[] Sha256( byte[] data )
	{
		long bitLen = (long)data.Length * 8;
		int padLen = (64 - (int)((data.Length + 9) % 64)) % 64;
		var msg = new byte[data.Length + 1 + padLen + 8];
		Array.Copy( data, msg, data.Length );
		msg[data.Length] = 0x80;
		for ( int i = 0; i < 8; i++ )
			msg[msg.Length - 1 - i] = (byte)(bitLen >> (i * 8));

		uint h0 = 0x6a09e667, h1 = 0xbb67ae85, h2 = 0x3c6ef372, h3 = 0xa54ff53a;
		uint h4 = 0x510e527f, h5 = 0x9b05688c, h6 = 0x1f83d9ab, h7 = 0x5be0cd19;

		var w = new uint[64];

		for ( int offset = 0; offset < msg.Length; offset += 64 )
		{
			for ( int i = 0; i < 16; i++ )
				w[i] = (uint)(msg[offset + i * 4] << 24 | msg[offset + i * 4 + 1] << 16 | msg[offset + i * 4 + 2] << 8 | msg[offset + i * 4 + 3]);

			for ( int i = 16; i < 64; i++ )
			{
				var s0 = RotR( w[i - 15], 7 ) ^ RotR( w[i - 15], 18 ) ^ (w[i - 15] >> 3);
				var s1 = RotR( w[i - 2], 17 ) ^ RotR( w[i - 2], 19 ) ^ (w[i - 2] >> 10);
				w[i] = w[i - 16] + s0 + w[i - 7] + s1;
			}

			uint a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;

			for ( int i = 0; i < 64; i++ )
			{
				var S1 = RotR( e, 6 ) ^ RotR( e, 11 ) ^ RotR( e, 25 );
				var ch = (e & f) ^ (~e & g);
				var temp1 = h + S1 + ch + _sha256K[i] + w[i];
				var S0 = RotR( a, 2 ) ^ RotR( a, 13 ) ^ RotR( a, 22 );
				var maj = (a & b) ^ (a & c) ^ (b & c);
				var temp2 = S0 + maj;

				h = g; g = f; f = e; e = d + temp1;
				d = c; c = b; b = a; a = temp1 + temp2;
			}

			h0 += a; h1 += b; h2 += c; h3 += d;
			h4 += e; h5 += f; h6 += g; h7 += h;
		}

		var result = new byte[32];
		WriteBE( result, 0, h0 ); WriteBE( result, 4, h1 );
		WriteBE( result, 8, h2 ); WriteBE( result, 12, h3 );
		WriteBE( result, 16, h4 ); WriteBE( result, 20, h5 );
		WriteBE( result, 24, h6 ); WriteBE( result, 28, h7 );
		return result;
	}

	private static uint RotR( uint x, int n ) => (x >> n) | (x << (32 - n));

	private static void WriteBE( byte[] buf, int offset, uint val )
	{
		buf[offset] = (byte)(val >> 24);
		buf[offset + 1] = (byte)(val >> 16);
		buf[offset + 2] = (byte)(val >> 8);
		buf[offset + 3] = (byte)val;
	}
}
