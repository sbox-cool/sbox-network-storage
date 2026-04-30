using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Sandbox;

public static partial class NetworkStorage
{
	private static Dictionary<string, object> CreateEncryptedEndpointEnvelope( string slug, Dictionary<string, object> payload, RuntimeAuthSession session )
	{
		var envelope = new Dictionary<string, object>
		{
			["version"] = "1",
			["algorithm"] = "aes-256-gcm+hmac-sha256",
			["projectId"] = ProjectId,
			["nonce"] = Base64UrlEncode( Encoding.UTF8.GetBytes( Guid.NewGuid().ToString( "N" ) ) ),
			["publicKeyFingerprint"] = PublicKeyFingerprint( ApiKey )
		};
		if ( session is not null )
			envelope["sessionId"] = session.Id;
		else
			envelope["steamId"] = Game.SteamId.ToString();

		var iv = Guid.NewGuid().ToByteArray()[..12];
		envelope["iv"] = Base64UrlEncode( iv );
		var key = Sha256( Encoding.UTF8.GetBytes( $"sboxcool.network-storage.encrypted-request.v1\0{ApiKey ?? ""}\0{StableStringify( EnvelopeContext( envelope ) )}" ) );
		var plaintext = Encoding.UTF8.GetBytes( JsonSerializer.Serialize( payload ) );
		var aad = Encoding.UTF8.GetBytes( StableStringify( EnvelopeContext( envelope ) ) );
		var (ciphertext, tag) = Aes256GcmEncrypt( key, iv, plaintext, aad );

		envelope["tag"] = Base64UrlEncode( tag );
		envelope["encryptedPayload"] = Base64UrlEncode( ciphertext );
		envelope["signature"] = ToHex( HmacSha256( Encoding.UTF8.GetBytes( ApiKey ?? "" ), Encoding.UTF8.GetBytes( SignatureBinding( envelope ) ) ) );
		return envelope;
	}

	private static Dictionary<string, object> EnvelopeContext( Dictionary<string, object> envelope )
	{
		var identity = envelope.ContainsKey( "sessionId" )
			? ("session", Convert.ToString( envelope["sessionId"] ) ?? "")
			: ("steam", Convert.ToString( envelope["steamId"] ) ?? "");
		return new Dictionary<string, object>
		{
			["version"] = Convert.ToString( envelope["version"] ) ?? "1",
			["algorithm"] = Convert.ToString( envelope["algorithm"] ) ?? "aes-256-gcm+hmac-sha256",
			["projectId"] = Convert.ToString( envelope["projectId"] ) ?? "",
			["identityType"] = identity.Item1,
			["identity"] = identity.Item2,
			["nonce"] = Convert.ToString( envelope["nonce"] ) ?? "",
			["publicKeyFingerprint"] = Convert.ToString( envelope["publicKeyFingerprint"] ) ?? ""
		};
	}

	private static string SignatureBinding( Dictionary<string, object> envelope )
	{
		var binding = EnvelopeContext( envelope );
		binding["iv"] = Convert.ToString( envelope["iv"] ) ?? "";
		binding["encryptedPayload"] = Convert.ToString( envelope["encryptedPayload"] ) ?? "";
		binding["tag"] = Convert.ToString( envelope["tag"] ) ?? "";
		return StableStringify( binding );
	}

	private static Dictionary<string, object> ObjectToDictionary( object input )
	{
		if ( input is Dictionary<string, object> existing )
			return new Dictionary<string, object>( existing );

		var json = JsonSerializer.Serialize( input ?? new { } );
		return JsonSerializer.Deserialize<Dictionary<string, object>>( json ) ?? new Dictionary<string, object>();
	}

	private static string PublicKeyFingerprint( string apiKey )
		=> ToHex( Sha256( Encoding.UTF8.GetBytes( apiKey ?? "" ) ) )[..32];

	private static string ReadServerMessage( JsonElement root, string fallback )
	{
		if ( root.TryGetProperty( "message", out var message ) && message.ValueKind == JsonValueKind.String )
			return message.GetString() ?? fallback;
		if ( root.TryGetProperty( "error", out var error ) && error.ValueKind == JsonValueKind.Object &&
			error.TryGetProperty( "message", out var nested ) && nested.ValueKind == JsonValueKind.String )
			return nested.GetString() ?? fallback;
		return fallback;
	}

	private static string StableStringify( object value )
	{
		var sb = new StringBuilder();
		WriteStableJson( sb, value );
		return sb.ToString();
	}

	private static void WriteStableJson( StringBuilder sb, object value )
	{
		if ( value is Dictionary<string, object> dict )
		{
			sb.Append( '{' );
			var keys = new List<string>( dict.Keys );
			keys.Sort( StringComparer.Ordinal );
			for ( var i = 0; i < keys.Count; i++ )
			{
				if ( i > 0 ) sb.Append( ',' );
				WriteJsonString( sb, keys[i] );
				sb.Append( ':' );
				WriteStableJson( sb, dict[keys[i]] );
			}
			sb.Append( '}' );
			return;
		}
		if ( value is string stringValue )
		{
			WriteJsonString( sb, stringValue );
			return;
		}
		sb.Append( JsonSerializer.Serialize( value ) );
	}

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
