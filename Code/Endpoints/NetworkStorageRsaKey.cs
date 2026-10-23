using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Sandbox;

// RSA-OAEP (SHA-256, MGF1-SHA-256, empty label) encryption and SubjectPublicKeyInfo fingerprints
// for encryption v2. Encrypt-only: the game never holds a private key. BigInteger is whitelisted;
// System.Security.Cryptography RSA is not, so the padding and DER encoding are implemented here.
internal static class NetworkStorageRsaKey
{
	// RSA-2048 is the only accepted size for the server's encryption key.
	internal const int ModulusBytes = 256;

	private const int HashLength = 32;

	private static readonly byte[] RsaEncryptionAlgorithmIdentifier =
	{
		0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00,
	};

	// Returns the modulus without leading zero bytes, or null when it is not RSA-2048.
	internal static byte[] NormalizeModulus( byte[] modulus )
	{
		var trimmed = StripLeadingZeros( modulus );
		return trimmed.Length == ModulusBytes ? trimmed : null;
	}

	// Smallest signing key the security-config verifier accepts (RSA-1024). Signing keys of any size from here up are
	// trusted, and the trust store reads them back under this same rule, so a stored key can always be reloaded.
	internal const int MinSigningModulusBytes = 128;

	// Returns the modulus without leading zero bytes when it is a usable signing key, or null.
	internal static byte[] NormalizeSigningModulus( byte[] modulus )
	{
		var trimmed = StripLeadingZeros( modulus );
		return trimmed.Length >= MinSigningModulusBytes ? trimmed : null;
	}

	// Lowercase hex SHA-256 of the SubjectPublicKeyInfo DER. This is the pinnable key fingerprint.
	internal static string Fingerprint( byte[] modulus, byte[] exponent )
		=> ToHex( NetworkStorageSecuritySignature.Sha256( SubjectPublicKeyInfo( modulus, exponent ) ) );

	internal static byte[] SubjectPublicKeyInfo( byte[] modulus, byte[] exponent )
	{
		var rsaPublicKey = DerTlv( 0x30, DerInteger( modulus ), DerInteger( exponent ) );
		var subjectPublicKey = DerTlv( 0x03, new byte[] { 0x00 }, rsaPublicKey );
		return DerTlv( 0x30, RsaEncryptionAlgorithmIdentifier, subjectPublicKey );
	}

	// RFC 8017 section 7.1.1 with SHA-256, MGF1-SHA-256 and an empty label.
	internal static byte[] OaepSha256Encrypt( byte[] message, byte[] modulus, byte[] exponent, byte[] seed )
	{
		var k = ModulusBytes;
		if ( modulus.Length != k )
			throw new ArgumentException( "RSA-OAEP key must be RSA-2048." );
		if ( message.Length > k - 2 * HashLength - 2 )
			throw new ArgumentException( "Message too long for RSA-OAEP." );
		if ( seed.Length != HashLength )
			throw new ArgumentException( "OAEP seed must be 32 bytes." );

		var db = new byte[k - HashLength - 1];
		Array.Copy( NetworkStorageSecuritySignature.Sha256( Array.Empty<byte>() ), db, HashLength );
		db[db.Length - message.Length - 1] = 0x01;
		Array.Copy( message, 0, db, db.Length - message.Length, message.Length );

		var dbMask = Mgf1Sha256( seed, db.Length );
		for ( var i = 0; i < db.Length; i++ )
			db[i] ^= dbMask[i];

		var seedMask = Mgf1Sha256( db, HashLength );
		var em = new byte[k];
		for ( var i = 0; i < HashLength; i++ )
			em[1 + i] = (byte)(seed[i] ^ seedMask[i]);
		Array.Copy( db, 0, em, 1 + HashLength, db.Length );

		var m = new BigInteger( em, isUnsigned: true, isBigEndian: true );
		var e = new BigInteger( exponent, isUnsigned: true, isBigEndian: true );
		var n = new BigInteger( modulus, isUnsigned: true, isBigEndian: true );
		var c = BigInteger.ModPow( m, e, n );
		return NetworkStorageSecuritySignature.LeftPad( c.ToByteArray( isUnsigned: true, isBigEndian: true ), k );
	}

	private static byte[] Mgf1Sha256( byte[] seed, int length )
	{
		var mask = new byte[length];
		var written = 0;
		for ( uint counter = 0; written < length; counter++ )
		{
			var input = new byte[seed.Length + 4];
			Array.Copy( seed, input, seed.Length );
			input[seed.Length] = (byte)(counter >> 24);
			input[seed.Length + 1] = (byte)(counter >> 16);
			input[seed.Length + 2] = (byte)(counter >> 8);
			input[seed.Length + 3] = (byte)counter;
			var digest = NetworkStorageSecuritySignature.Sha256( input );
			var take = Math.Min( digest.Length, length - written );
			Array.Copy( digest, 0, mask, written, take );
			written += take;
		}
		return mask;
	}

	private static byte[] DerInteger( byte[] value )
	{
		var magnitude = StripLeadingZeros( value );
		if ( magnitude.Length == 0 )
			magnitude = new byte[] { 0x00 };
		if ( (magnitude[0] & 0x80) != 0 )
			magnitude = Concat( new byte[] { 0x00 }, magnitude );
		return DerTlv( 0x02, magnitude );
	}

	private static byte[] DerTlv( byte tag, params byte[][] parts )
	{
		var contentLength = 0;
		foreach ( var part in parts )
			contentLength += part.Length;

		var header = DerLength( contentLength );
		var output = new byte[1 + header.Length + contentLength];
		output[0] = tag;
		Array.Copy( header, 0, output, 1, header.Length );

		var offset = 1 + header.Length;
		foreach ( var part in parts )
		{
			Array.Copy( part, 0, output, offset, part.Length );
			offset += part.Length;
		}
		return output;
	}

	private static byte[] DerLength( int length )
	{
		if ( length < 0x80 )
			return new[] { (byte)length };

		var bytes = new List<byte>();
		for ( var value = length; value > 0; value >>= 8 )
			bytes.Insert( 0, (byte)value );
		bytes.Insert( 0, (byte)(0x80 | bytes.Count) );
		return bytes.ToArray();
	}

	private static byte[] StripLeadingZeros( byte[] value )
	{
		var start = 0;
		while ( start < value.Length && value[start] == 0 )
			start++;

		var stripped = new byte[value.Length - start];
		Array.Copy( value, start, stripped, 0, stripped.Length );
		return stripped;
	}

	private static byte[] Concat( byte[] first, byte[] second )
	{
		var output = new byte[first.Length + second.Length];
		Array.Copy( first, 0, output, 0, first.Length );
		Array.Copy( second, 0, output, first.Length, second.Length );
		return output;
	}

	private static string ToHex( byte[] value )
	{
		var sb = new StringBuilder( value.Length * 2 );
		foreach ( var b in value )
			sb.Append( b.ToString( "x2" ) );
		return sb.ToString();
	}
}
