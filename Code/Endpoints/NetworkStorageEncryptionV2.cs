using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Sandbox;

// Encryption v2: RSA-OAEP wraps a fresh AES-256 key per request and the response is sealed under that key. The server
// request key comes only from a trusted signed config (see NetworkStorageRuntimeSecurityConfigLoad.cs).
public static partial class NetworkStorage
{
	private const string V2Algorithm = "rsa-oaep-256+aes-256-gcm";
	private const string V2ResponseAadLabel = "sboxcool.network-storage.encrypted-response.v2";

	// The server request key from a trusted config's `encryption` block.
	internal sealed record RuntimeEncryptionKey( string KeyId, string Fingerprint, byte[] Modulus, byte[] Exponent );

	// What a response needs to be opened: the request's AES key, nonce, and keyId.
	internal sealed record EncryptedResponseContext( byte[] AesKey, string Nonce, string KeyId );

	// Returns why an endpoint call must not be sent, or null when it may proceed. Once a trusted config is held, it
	// decides the mode. Until then a refusal blocks every call, and a pin blocks every call with no trusted config.
	private static SecurityConfigRefusal EndpointSecurityRefusal()
	{
		if ( _runtimeSecurityConfig?.Trusted == true )
			return null;

		// A plain call could leak data the owner meant to encrypt, so a refused config blocks all calls.
		if ( !string.IsNullOrEmpty( _runtimeSecurityConfig?.RefusalCode ) )
			return new SecurityConfigRefusal( _runtimeSecurityConfig.RefusalCode, _runtimeSecurityConfig.RefusalMessage );

		return SecuritySigningKeyPin.Length > 0
			? new SecurityConfigRefusal( "ENCRYPTION_KEY_UNVERIFIED", "no trusted security config has been loaded" )
			: null;
	}

	// Reads the signed `encryption` block. The fingerprint must be the SPKI hash of the key it accompanies, and the
	// keyId its prefix, so a config cannot pair a key with a fingerprint that does not match it.
	internal static bool TryReadEncryptionKey( JsonElement block, out RuntimeEncryptionKey key, out string error )
	{
		key = null;
		error = "";
		if ( block.ValueKind != JsonValueKind.Object )
		{
			error = "encryption block is not an object";
			return false;
		}

		if ( ReadString( block, "algorithm" ) != V2Algorithm )
		{
			error = "encryption algorithm is not supported";
			return false;
		}

		if ( !block.TryGetProperty( "publicKeyJwk", out var jwk ) || jwk.ValueKind != JsonValueKind.Object )
		{
			error = "encryption publicKeyJwk is missing";
			return false;
		}

		var modulus = NetworkStorageRsaKey.NormalizeModulus( NetworkStorageSecuritySignature.Base64UrlDecode( ReadString( jwk, "n" ) ) );
		var exponent = NetworkStorageSecuritySignature.Base64UrlDecode( ReadString( jwk, "e" ) );
		if ( modulus is null || exponent.Length == 0 )
		{
			error = "encryption publicKeyJwk is not an RSA-2048 key";
			return false;
		}

		var fingerprint = NormalizeSecurityKeyFingerprint( ReadString( block, "fingerprint" ) );
		if ( fingerprint != NetworkStorageRsaKey.Fingerprint( modulus, exponent ) )
		{
			error = "encryption fingerprint does not match publicKeyJwk";
			return false;
		}

		var keyId = NormalizeSecurityKeyFingerprint( ReadString( block, "keyId" ) );
		if ( keyId.Length != 32 || !fingerprint.StartsWith( keyId, StringComparison.Ordinal ) )
		{
			error = "encryption keyId is not the first 32 characters of its fingerprint";
			return false;
		}

		key = new RuntimeEncryptionKey( keyId, fingerprint, modulus, exponent );
		return true;
	}

	// Seals a payload under a fresh AES key wrapped to the trusted server key. There is no signature: GCM under the
	// wrapped key authenticates the envelope, and the AAD binds project, endpoint, identity, nonce, and keyId.
	private static Dictionary<string, object> SealEndpointRequestV2( RuntimeEncryptionKey key, string slug, Dictionary<string, object> payload,
		RuntimeAuthSession session, out EncryptedResponseContext responseContext )
	{
		var identityType = session is not null ? "session" : "steam";
		var identity = session is not null ? session.Id : Game.SteamId.ToString();
		return SealRequestV2( key, ProjectId, slug, identityType, identity, payload, out responseContext );
	}

	// Pure sealing core. Project and identity are inputs, so the envelope can be checked without a live game.
	internal static Dictionary<string, object> SealRequestV2( RuntimeEncryptionKey key, string projectId, string slug, string identityType,
		string identity, Dictionary<string, object> payload, out EncryptedResponseContext responseContext )
	{
		var aesKey = NetworkStorageSecureRandom.Bytes( 32 );
		var iv = NetworkStorageSecureRandom.Bytes( 12 );
		var seed = NetworkStorageSecureRandom.Bytes( 32 );
		var nonce = Base64UrlEncode( NetworkStorageSecureRandom.Bytes( 12 ) );

		var aad = EncryptedRequestAadV2( projectId, slug, identityType, identity, nonce, key.KeyId );
		var plaintext = Encoding.UTF8.GetBytes( JsonSerializer.Serialize( payload ) );
		var (ciphertext, tag) = Aes256GcmEncrypt( aesKey, iv, plaintext, aad );

		var envelope = new Dictionary<string, object>
		{
			["version"] = "2",
			["algorithm"] = V2Algorithm,
			["projectId"] = projectId,
			["keyId"] = key.KeyId,
			["nonce"] = nonce,
			["wrappedKey"] = Base64UrlEncode( NetworkStorageRsaKey.OaepSha256Encrypt( aesKey, key.Modulus, key.Exponent, seed ) ),
			["iv"] = Base64UrlEncode( iv ),
			["tag"] = Base64UrlEncode( tag ),
			["encryptedPayload"] = Base64UrlEncode( ciphertext )
		};
		if ( !string.IsNullOrEmpty( slug ) )
			envelope["endpointSlug"] = slug;
		envelope[identityType == "session" ? "sessionId" : "steamId"] = identity;

		responseContext = new EncryptedResponseContext( aesKey, nonce, key.KeyId );
		return envelope;
	}

	internal static byte[] EncryptedRequestAadV2( string projectId, string slug, string identityType, string identity, string nonce, string keyId )
	{
		var binding = new Dictionary<string, object>
		{
			["version"] = "2",
			["algorithm"] = V2Algorithm,
			["projectId"] = projectId,
			["identityType"] = identityType,
			["identity"] = identity,
			["nonce"] = nonce,
			["keyId"] = keyId
		};
		if ( !string.IsNullOrEmpty( slug ) )
			binding["endpointSlug"] = slug;
		return Encoding.UTF8.GetBytes( StableStringify( binding ) );
	}

	// Returns the body to parse. A plain body passes through; a body sealed as v2 is opened with the request's key.
	// Returns null and records an error when a sealed body cannot be opened.
	internal static string UnsealEndpointResponse( EncryptedResponseContext context, string slug, string raw )
	{
		var trimmed = (raw ?? "").TrimStart();
		if ( trimmed.Length == 0 || trimmed[0] != '{' )
			return raw;

		try
		{
			using var doc = JsonDocument.Parse( trimmed );
			var root = doc.RootElement;
			if ( !IsSealedV2Response( root ) )
				return raw;

			var body = context is null ? null : OpenSealedV2Response( context, root );
			if ( body is not null )
				return body;
		}
		catch ( JsonException )
		{
			return raw;
		}

		RecordEndpointError( slug, "ENCRYPTED_RESPONSE_INVALID", "Encrypted response could not be opened." );
		return null;
	}

	// Returns a v2 seal as-is. The host proxy uses this: it has no key, so it relays the ciphertext unparsed.
	internal static bool TryRelaySealedResponse( string raw, out JsonElement? relayed )
	{
		relayed = null;
		var trimmed = (raw ?? "").TrimStart();
		if ( trimmed.Length == 0 || trimmed[0] != '{' )
			return false;

		try
		{
			using var doc = JsonDocument.Parse( trimmed );
			if ( !IsSealedV2Response( doc.RootElement ) )
				return false;
			relayed = doc.RootElement.Clone();
			return true;
		}
		catch ( JsonException )
		{
			return false;
		}
	}

	private static bool IsSealedV2Response( JsonElement root )
		=> root.ValueKind == JsonValueKind.Object
			&& root.TryGetProperty( "encrypted", out var encrypted ) && encrypted.ValueKind == JsonValueKind.True
			&& root.TryGetProperty( "v", out var version ) && version.ValueKind == JsonValueKind.Number
			&& version.TryGetInt32( out var v ) && v == 2;

	private static string OpenSealedV2Response( EncryptedResponseContext context, JsonElement root )
	{
		var iv = NetworkStorageSecuritySignature.Base64UrlDecode( ReadString( root, "iv" ) );
		var tag = NetworkStorageSecuritySignature.Base64UrlDecode( ReadString( root, "tag" ) );
		var ciphertext = NetworkStorageSecuritySignature.Base64UrlDecode( ReadString( root, "payload" ) );
		if ( iv.Length != 12 || tag.Length != 16 || ciphertext.Length == 0 )
			return null;

		var aad = Encoding.UTF8.GetBytes( $"{V2ResponseAadLabel}\0{context.Nonce}\0{context.KeyId}" );
		var plaintext = Aes256GcmDecrypt( context.AesKey, iv, ciphertext, tag, aad );
		return plaintext is null ? null : Encoding.UTF8.GetString( plaintext );
	}

	// Pins and fingerprints compare as lowercase hex. Whitespace and ':' separators are ignored, so pasted values work.
	internal static string NormalizeSecurityKeyFingerprint( string value )
	{
		var sb = new StringBuilder();
		foreach ( var ch in value ?? "" )
		{
			if ( char.IsWhiteSpace( ch ) || ch == ':' )
				continue;
			sb.Append( char.ToLowerInvariant( ch ) );
		}
		return sb.ToString();
	}

	private static bool IsSha256Hex( string value )
	{
		if ( value.Length != 64 )
			return false;
		foreach ( var ch in value )
		{
			var isHex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
			if ( !isHex )
				return false;
		}
		return true;
	}

	// An empty value clears the pin. A value that is not a SHA-256 fingerprint is kept, so every config fails the pin
	// check and encrypted calls are refused until the owner fixes it.
	private static void SetSecuritySigningKeyPin( string fingerprint )
	{
		var normalized = NormalizeSecurityKeyFingerprint( fingerprint );
		if ( normalized.Length > 0 && !IsSha256Hex( normalized ) )
			Log.Warning( "[NetworkStorage] securityKeyFingerprint is not a 64-character SHA-256 hex fingerprint; encrypted calls will be refused" );
		SecuritySigningKeyPin = normalized;
	}
}
