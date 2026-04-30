using System;
using System.Text;

namespace Sandbox;

internal static partial class NetworkStorageSecuritySignature
{
	private static byte[] Base64UrlDecode( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return Array.Empty<byte>();
		var padded = value.Replace( '-', '+' ).Replace( '_', '/' );
		padded = padded.PadRight( padded.Length + (4 - padded.Length % 4) % 4, '=' );
		try
		{
			return Convert.FromBase64String( padded );
		}
		catch
		{
			return Array.Empty<byte>();
		}
	}

	private static byte[] LeftPad( byte[] value, int length )
	{
		if ( value.Length == length )
			return value;
		var padded = new byte[length];
		Array.Copy( value, 0, padded, Math.Max( 0, length - value.Length ), Math.Min( value.Length, length ) );
		return padded;
	}

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
				w[i] = w[i - 16] + (RotR( w[i - 15], 7 ) ^ RotR( w[i - 15], 18 ) ^ (w[i - 15] >> 3)) + w[i - 7] + (RotR( w[i - 2], 17 ) ^ RotR( w[i - 2], 19 ) ^ (w[i - 2] >> 10));

			uint a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;
			for ( int i = 0; i < 64; i++ )
			{
				var t1 = h + (RotR( e, 6 ) ^ RotR( e, 11 ) ^ RotR( e, 25 )) + ((e & f) ^ (~e & g)) + K[i] + w[i];
				var t2 = (RotR( a, 2 ) ^ RotR( a, 13 ) ^ RotR( a, 22 )) + ((a & b) ^ (a & c) ^ (b & c));
				h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
			}
			h0 += a; h1 += b; h2 += c; h3 += d; h4 += e; h5 += f; h6 += g; h7 += h;
		}

		var result = new byte[32];
		WriteBE( result, 0, h0 ); WriteBE( result, 4, h1 ); WriteBE( result, 8, h2 ); WriteBE( result, 12, h3 );
		WriteBE( result, 16, h4 ); WriteBE( result, 20, h5 ); WriteBE( result, 24, h6 ); WriteBE( result, 28, h7 );
		return result;
	}

	private static uint RotR( uint value, int bits ) => (value >> bits) | (value << (32 - bits));
	private static void WriteBE( byte[] buffer, int offset, uint value )
	{
		buffer[offset] = (byte)(value >> 24);
		buffer[offset + 1] = (byte)(value >> 16);
		buffer[offset + 2] = (byte)(value >> 8);
		buffer[offset + 3] = (byte)value;
	}

	private static string ToHex( byte[] value )
	{
		var sb = new StringBuilder( value.Length * 2 );
		foreach ( var b in value )
			sb.Append( b.ToString( "x2" ) );
		return sb.ToString();
	}

	private static readonly uint[] K = {
		0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
		0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
		0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
		0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
		0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
		0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
		0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
		0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
	};}
