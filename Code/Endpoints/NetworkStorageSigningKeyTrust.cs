using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Sandbox;

public static partial class NetworkStorage
{
	// Unpinned trust: the server signing key accepted for each API root, kept in the game's data folder so a restart
	// keeps it. It is local to this install; it is never synced or shipped.
	private const string SigningKeyTrustFile = "network-storage-signing-key-trust.json";

	// Signing key accepted for an API root when no pin is configured.
	internal sealed record TrustedSigningKey( string Fingerprint, byte[] Modulus, byte[] Exponent );

	private static Dictionary<string, TrustedSigningKey> _trustedSigningKeys;

	private static Dictionary<string, TrustedSigningKey> TrustedSigningKeys => _trustedSigningKeys ??= LoadSigningKeyTrust();

	private static Dictionary<string, TrustedSigningKey> LoadSigningKeyTrust()
	{
		var keys = new Dictionary<string, TrustedSigningKey>();
		try
		{
			if ( !FileSystem.Data.FileExists( SigningKeyTrustFile ) )
				return keys;

			using var doc = JsonDocument.Parse( FileSystem.Data.ReadAllText( SigningKeyTrustFile ) );
			foreach ( var entry in doc.RootElement.EnumerateObject() )
			{
				// A bad entry is skipped alone; the roots that are still valid keep their trust.
				try
				{
					keys[entry.Name] = ReadStoredSigningKey( entry.Value );
				}
				catch ( Exception ex )
				{
					Log.Warning( $"[NetworkStorage] ignoring unusable signing key trust entry for {entry.Name}: {ex.Message}" );
				}
			}
		}
		catch ( Exception ex )
		{
			// An unreadable store is discarded, so the next config is trusted on first use again. The warning makes that visible.
			keys.Clear();
			Log.Warning( $"[NetworkStorage] ignoring unreadable signing key trust store {SigningKeyTrustFile}: {ex.Message}" );
		}

		return keys;
	}

	// Reads one stored entry with the same size rule as TryReadSigningKey, so every key RememberTrustedSigningKey writes reads back.
	private static TrustedSigningKey ReadStoredSigningKey( JsonElement entry )
	{
		var jwk = entry.GetProperty( "publicKeyJwk" );
		var modulus = NetworkStorageRsaKey.NormalizeSigningModulus( NetworkStorageSecuritySignature.Base64UrlDecode( jwk.GetProperty( "n" ).GetString() ?? "" ) );
		var exponent = NetworkStorageSecuritySignature.Base64UrlDecode( jwk.GetProperty( "e" ).GetString() ?? "" );
		if ( modulus is null || exponent.Length == 0 )
			throw new FormatException( "not an RSA public key of at least 1024 bits" );

		return new TrustedSigningKey( NetworkStorageRsaKey.Fingerprint( modulus, exponent ), modulus, exponent );
	}

	// Records the signing key trusted for an API root and writes the whole store. A failed write keeps the key for this session only.
	private static void RememberTrustedSigningKey( string apiRoot, TrustedSigningKey key )
	{
		TrustedSigningKeys[apiRoot] = key;
		SaveSigningKeyTrust();
	}

	// Forgets the signing key trusted for the current API root, so the next signed config is trusted on first use again.
	// Call it only after verifying the server identity: the next config is then accepted from whichever key it carries.
	// A game that must refuse a key change should ship a securityKeyFingerprint pin instead.
	public static void ResetSigningKeyTrust()
	{
		var apiRoot = ApiRoot;
		if ( TrustedSigningKeys.Remove( apiRoot ) )
			SaveSigningKeyTrust();

		ResetRuntimeSecurityConfig();
		Log.Warning( $"[NetworkStorage] signing key trust for {apiRoot} was reset; the next security config is trusted on first use." );
	}

	private static void SaveSigningKeyTrust()
	{
		var store = new Dictionary<string, object>();
		foreach ( var (root, trusted) in TrustedSigningKeys )
		{
			store[root] = new Dictionary<string, object>
			{
				["publicKeyJwk"] = new Dictionary<string, string>
				{
					["kty"] = "RSA",
					["n"] = Base64UrlEncode( trusted.Modulus ),
					["e"] = Base64UrlEncode( trusted.Exponent )
				}
			};
		}

		try
		{
			FileSystem.Data.WriteAllText( SigningKeyTrustFile, JsonSerializer.Serialize( store ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] could not save signing key trust to {SigningKeyTrustFile}: {ex.Message}. Trust changes last for this session only." );
		}
	}
}
