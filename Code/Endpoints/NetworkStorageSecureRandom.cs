using System;
using System.Collections.Generic;
using System.Text;

namespace Sandbox;

// The only source of key, IV, nonce and OAEP seed bytes for encryption v2.
// System.Security.Cryptography.RandomNumberGenerator is not on the s&box whitelist.
// Each 32-byte block is SHA-256(domain label || 32-bit counter || guid || guid || guid)
// over fresh Guid.NewGuid() values, which are OS CSPRNG-backed (about 366 random bits per block).
internal static class NetworkStorageSecureRandom
{
	private static readonly byte[] DomainLabel = Encoding.UTF8.GetBytes( "sboxcool.network-storage.secure-random.v2" );

	internal static byte[] Bytes( int count )
	{
		var output = new byte[count];
		var written = 0;
		for ( uint counter = 0; written < count; counter++ )
		{
			var digest = NetworkStorageSecuritySignature.Sha256( BlockInput( counter ) );
			var take = Math.Min( digest.Length, count - written );
			Array.Copy( digest, 0, output, written, take );
			written += take;
		}
		return output;
	}

	private static byte[] BlockInput( uint counter )
	{
		var input = new List<byte>( DomainLabel );
		input.Add( (byte)(counter >> 24) );
		input.Add( (byte)(counter >> 16) );
		input.Add( (byte)(counter >> 8) );
		input.Add( (byte)counter );
		for ( var i = 0; i < 3; i++ )
			input.AddRange( Guid.NewGuid().ToByteArray() );
		return input.ToArray();
	}
}
