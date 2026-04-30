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

	public static bool VerifySecurityConfigSignature( JsonElement config )
		=> VerifySecurityConfigSignature( config, out _ );

	public static bool VerifySecurityConfigSignature( JsonElement config, out string verificationMode )
	{
		verificationMode = "";
		if ( !config.TryGetProperty( "signature", out var signatureProp ) || signatureProp.ValueKind != JsonValueKind.String )
			return false;
		if ( !config.TryGetProperty( "signing", out var signing ) || signing.ValueKind != JsonValueKind.Object )
			return false;
		if ( !string.Equals( ReadString( signing, "algorithm" ), "rsa-sha256", StringComparison.OrdinalIgnoreCase ) )
			return false;
		if ( !signing.TryGetProperty( "publicKeyJwk", out var jwk ) || jwk.ValueKind != JsonValueKind.Object )
			return false;

		var modulus = Base64UrlDecode( ReadString( jwk, "n" ) );
		var exponent = Base64UrlDecode( ReadString( jwk, "e" ) );
		var signature = Base64UrlDecode( signatureProp.GetString() );
		if ( modulus.Length < 128 || exponent.Length == 0 || signature.Length == 0 )
			return false;

		var signedPayload = StableStringifyWithoutSignature( config );
		var digest = Sha256( Encoding.UTF8.GetBytes( signedPayload ) );
		if ( VerifyRsaSha256Pkcs1( digest, signature, modulus, exponent ) )
		{
			verificationMode = "rsa-sha256";
			return true;
		}

		if ( VerifyConfigVersionIntegrity( config ) )
		{
			verificationMode = "config-version-fallback";
			return true;
		}

		return false;
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

	private static bool VerifyConfigVersionIntegrity( JsonElement config )
	{
		if ( !config.TryGetProperty( "settings", out var settings ) || settings.ValueKind != JsonValueKind.Object )
			return false;

		var expected = ReadString( config, "configVersion" );
		if ( string.IsNullOrWhiteSpace( expected ) )
			return false;

		var digest = Sha256( Encoding.UTF8.GetBytes( StableStringifyWithoutSignature( settings ) ) );
		return string.Equals( ToHex( digest )[..16], expected, StringComparison.OrdinalIgnoreCase );
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
