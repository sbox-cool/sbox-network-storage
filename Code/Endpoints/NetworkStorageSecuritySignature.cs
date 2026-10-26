using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Sandbox;

internal static partial class NetworkStorageSecuritySignature
{
	private static readonly byte[] Sha256DigestInfoPrefix = {
		0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01,
		0x05, 0x00, 0x04, 0x20
	};

	// Reads the RSA key a signed security config claims for itself. The embedded key is never trusted
	// on its own: the caller must match it to a pinned fingerprint or a stored first-use key.
	public static bool TryReadSigningKey( JsonElement config, out byte[] modulus, out byte[] exponent )
	{
		modulus = null;
		exponent = null;
		if ( !config.TryGetProperty( "signing", out var signing ) || signing.ValueKind != JsonValueKind.Object )
			return false;
		if ( !string.Equals( ReadString( signing, "algorithm" ), "rsa-sha256", StringComparison.OrdinalIgnoreCase ) )
			return false;
		if ( !signing.TryGetProperty( "publicKeyJwk", out var jwk ) || jwk.ValueKind != JsonValueKind.Object )
			return false;

		modulus = NetworkStorageRsaKey.NormalizeSigningModulus( Base64UrlDecode( ReadString( jwk, "n" ) ) );
		exponent = Base64UrlDecode( ReadString( jwk, "e" ) );
		return modulus is not null && exponent.Length > 0;
	}

	// Verifies the config's RSA-PKCS1 SHA-256 signature against the supplied key only. There is no
	// unsigned or alternative acceptance path.
	public static bool VerifySecurityConfigSignature( JsonElement config, byte[] modulus, byte[] exponent )
	{
		if ( !config.TryGetProperty( "signature", out var signatureProp ) || signatureProp.ValueKind != JsonValueKind.String )
			return false;

		var signature = Base64UrlDecode( signatureProp.GetString() );
		if ( modulus.Length < NetworkStorageRsaKey.MinSigningModulusBytes || exponent.Length == 0 || signature.Length == 0 )
			return false;

		var digest = Sha256( Encoding.UTF8.GetBytes( StableStringifyWithoutSignature( config ) ) );
		return VerifyRsaSha256Pkcs1( digest, signature, modulus, exponent );
	}

	// Domain label the previous signing key signs over, so a rotation statement cannot be replayed as a config signature.
	private const string SigningKeyRotationDomain = "sboxcool.network-storage.signing-key-rotation.v1";

	// Verifies a rotation statement against the key it replaces. The signed message is the domain label, a newline, and the
	// statement's canonical JSON without its own `signature`. Field and expiry checks belong to the caller.
	public static bool VerifySigningKeyRotation( JsonElement statement, byte[] previousModulus, byte[] previousExponent )
	{
		if ( !statement.TryGetProperty( "signature", out var signatureProp ) || signatureProp.ValueKind != JsonValueKind.String )
			return false;

		var signature = Base64UrlDecode( signatureProp.GetString() );
		if ( previousModulus.Length < NetworkStorageRsaKey.MinSigningModulusBytes || previousExponent.Length == 0 || signature.Length == 0 )
			return false;

		var digest = Sha256( Encoding.UTF8.GetBytes( SigningKeyRotationDomain + "\n" + StableStringifyWithoutSignature( statement ) ) );
		return VerifyRsaSha256Pkcs1( digest, signature, previousModulus, previousExponent );
	}

	private static bool VerifyRsaSha256Pkcs1( byte[] digest, byte[] signature, byte[] modulus, byte[] exponent )
	{
		var keyBytes = modulus.Length;
		if ( signature.Length > keyBytes )
			return false;

		var sigInt = new BigInteger( LeftPad( signature, keyBytes ), isUnsigned: true, isBigEndian: true );
		var modInt = new BigInteger( modulus, isUnsigned: true, isBigEndian: true );
		var expInt = new BigInteger( exponent, isUnsigned: true, isBigEndian: true );
		var decoded = BigInteger.ModPow( sigInt, expInt, modInt ).ToByteArray( isUnsigned: true, isBigEndian: true );
		decoded = LeftPad( decoded, keyBytes );

		if ( decoded.Length < Sha256DigestInfoPrefix.Length + digest.Length + 11 )
			return false;
		if ( decoded[0] != 0x00 || decoded[1] != 0x01 )
			return false;

		var index = 2;
		while ( index < decoded.Length && decoded[index] == 0xff )
			index++;
		if ( index < 10 || index >= decoded.Length || decoded[index++] != 0x00 )
			return false;
		if ( decoded.Length - index != Sha256DigestInfoPrefix.Length + digest.Length )
			return false;

		for ( var i = 0; i < Sha256DigestInfoPrefix.Length; i++ )
		{
			if ( decoded[index + i] != Sha256DigestInfoPrefix[i] )
				return false;
		}
		index += Sha256DigestInfoPrefix.Length;

		for ( var i = 0; i < digest.Length; i++ )
		{
			if ( decoded[index + i] != digest[i] )
				return false;
		}
		return true;
	}

	private static string StableStringifyWithoutSignature( JsonElement element )
	{
		var sb = new StringBuilder();
		WriteStableValue( sb, element, skipSignature: true );
		return sb.ToString();
	}

	private static void WriteStableValue( StringBuilder sb, JsonElement element, bool skipSignature = false )
	{
		switch ( element.ValueKind )
		{
			case JsonValueKind.Object:
				sb.Append( '{' );
				var properties = new List<JsonProperty>();
				foreach ( var property in element.EnumerateObject() )
				{
					if ( skipSignature && property.NameEquals( "signature" ) )
						continue;
					if ( property.Value.ValueKind != JsonValueKind.Undefined )
						properties.Add( property );
				}
				properties.Sort( ( a, b ) => string.CompareOrdinal( a.Name, b.Name ) );
				for ( var i = 0; i < properties.Count; i++ )
				{
					if ( i > 0 ) sb.Append( ',' );
					WriteJsonString( sb, properties[i].Name );
					sb.Append( ':' );
					WriteStableValue( sb, properties[i].Value );
				}
				sb.Append( '}' );
				break;
			case JsonValueKind.Array:
				sb.Append( '[' );
				var first = true;
				foreach ( var item in element.EnumerateArray() )
				{
					if ( !first ) sb.Append( ',' );
					first = false;
					WriteStableValue( sb, item );
				}
				sb.Append( ']' );
				break;
			case JsonValueKind.String:
				WriteJsonString( sb, element.GetString() );
				break;
			case JsonValueKind.Number:
				sb.Append( element.GetRawText() );
				break;
			case JsonValueKind.True:
				sb.Append( "true" );
				break;
			case JsonValueKind.False:
				sb.Append( "false" );
				break;
			default:
				sb.Append( "null" );
				break;
		}
	}

	private static string ReadString( JsonElement element, string name )
		=> element.ValueKind == JsonValueKind.Object && element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

	private static void WriteJsonString( StringBuilder sb, string value )
	{
		sb.Append( '"' );
		foreach ( var ch in value ?? "" )
		{
			switch ( ch )
			{
				case '"': sb.Append( "\\\"" ); break;
				case '\\': sb.Append( "\\\\" ); break;
				case '\b': sb.Append( "\\b" ); break;
				case '\f': sb.Append( "\\f" ); break;
				case '\n': sb.Append( "\\n" ); break;
				case '\r': sb.Append( "\\r" ); break;
				case '\t': sb.Append( "\\t" ); break;
				default:
					if ( ch < ' ' )
						sb.Append( "\\u" ).Append( ((int)ch).ToString( "x4" ) );
					else
						sb.Append( ch );
					break;
			}
		}
		sb.Append( '"' );
	}

}
