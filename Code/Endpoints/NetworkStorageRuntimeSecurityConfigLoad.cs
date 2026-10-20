using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	private const int SecurityConfigRetryDelaySeconds = 60;

	// A config that was received but cannot be trusted. Code is the stable error code; Message is for logs.
	private sealed record SecurityConfigRefusal( string Code, string Message );

	private static async Task<bool> LoadRuntimeSecurityConfigAsync( string reason, bool forceRefresh )
	{
		var url = $"{ApiRoot}/security-config/{Uri.EscapeDataString( ProjectId )}";
		var generation = _securityConfigGeneration;
		if ( forceRefresh )
			url += "?refresh=1";

		try
		{
			var headers = new System.Collections.Generic.Dictionary<string, string>();
			AddRuntimeTargetHeaders( headers );
			var raw = await RequestStringAsync(url, "GET", null, headers);
			// The pin, API root or project changed while this fetch was in flight; its result must not be committed.
			if ( generation != _securityConfigGeneration ) return false;
			if ( !TryParseRuntimeSecurityConfig( raw, out var loaded, out var refusal, out var error ) )
				return refusal is not null ? RefuseSecurityConfig( reason, refusal ) : UseSyncedSecurityConfig( reason, error );

			var previousMode = _runtimeSecurityConfig?.ModeText ?? "none";
			_runtimeSecurityConfig = loaded with
			{
				Loaded = true,
				Source = forceRefresh ? "backend-refresh" : "backend",
				LoadedAt = DateTimeOffset.UtcNow,
				LastRefreshReason = reason ?? "",
				SyncedEnableAuthSessions = EnableAuthSessions,
				SyncedEnableEncryptedRequests = EnableEncryptedRequests
			};
			_securityConfigRetryAfter = default;
			EnableAuthSessions = loaded.EnableAuthSessions;
			EnableEncryptedRequests = loaded.EnableEncryptedRequests;
			Log.Info( $"[NetworkStorage] security config loaded reason={reason} {previousMode}->{_runtimeSecurityConfig.ModeText} version={loaded.ConfigVersion} ttl={loaded.TtlSeconds}s" );
			return true;
		}
		catch ( Exception ex )
		{
			if ( generation != _securityConfigGeneration ) return false;
			return UseSyncedSecurityConfig( reason, ex.Message );
		}
	}

	// Parses and authenticates a fetched config. Returns false with `refusal` when the config was received but cannot
	// be trusted, or with `error` when the response is unusable.
	private static bool TryParseRuntimeSecurityConfig( string raw, out RuntimeSecurityConfigSnapshot snapshot, out SecurityConfigRefusal refusal, out string error )
	{
		snapshot = null;
		refusal = null;
		error = "";

		if ( string.IsNullOrWhiteSpace( raw ) )
		{
			error = "empty response";
			return false;
		}

		try
		{
			using var doc = JsonDocument.Parse( raw );
			var root = doc.RootElement;
			if ( root.TryGetProperty( "ok", out var ok ) && ok.ValueKind == JsonValueKind.False )
			{
				error = "backend returned ok=false";
				return false;
			}

			var config = root.TryGetProperty( "config", out var configProp ) ? configProp : root;
			var projectId = ReadString( config, "projectId" );
			if ( !string.Equals( projectId, ProjectId, StringComparison.Ordinal ) )
			{
				error = $"project mismatch {projectId}";
				return false;
			}

			refusal = CheckSecurityConfigTrust( config, out var firstUseKey );
			if ( refusal is not null )
				return false;

			RuntimeEncryptionKey encryptionKey = null;
			if ( config.TryGetProperty( "encryption", out var encryptionProp ) )
			{
				if ( !TryReadEncryptionKey( encryptionProp, out encryptionKey, out var keyError ) )
				{
					refusal = new SecurityConfigRefusal( "ENCRYPTION_KEY_INVALID", keyError );
					return false;
				}
			}

			var settings = config.TryGetProperty( "settings", out var settingsProp ) ? settingsProp : default;
			ApplyAnalyticsRuntimeConfig( settings );
			snapshot = new RuntimeSecurityConfigSnapshot
			{
				ProjectId = projectId,
				ConfigVersion = ReadString( config, "configVersion" ),
				GeneratedAt = ReadDateTimeOffset( config, "generatedAt" ),
				TtlSeconds = ReadInt( config, "ttlSeconds", 60 ),
				EnableAuthSessions = ReadBool( settings, "enableAuthSessions", EnableAuthSessions ),
				EnableEncryptedRequests = ReadBool( settings, "enableEncryptedRequests", EnableEncryptedRequests ),
				EnablePlayerAnalytics = EnablePlayerAnalytics,
				SignaturePresent = true,
				Trusted = true,
				EncryptionKey = encryptionKey
			};
			if ( firstUseKey is not null )
				RememberTrustedSigningKey( ApiRoot, firstUseKey );
			return true;
		}
		catch ( Exception ex )
		{
			error = ex.Message;
			return false;
		}
	}

	// Decides whether a signed config may be used. Returns null when trusted. A key learned on first use is returned in
	// `firstUse`; the caller records it only after the whole config has been accepted.
	private static SecurityConfigRefusal CheckSecurityConfigTrust( JsonElement config, out TrustedSigningKey firstUse )
	{
		firstUse = null;
		if ( !NetworkStorageSecuritySignature.TryReadSigningKey( config, out var modulus, out var exponent ) )
			return new SecurityConfigRefusal( "ENCRYPTION_CONFIG_UNVERIFIED", "security config has no usable signing key" );

		// The config is verified with the key it carries. A pin, or the key trusted for this API root, must name that key.
		var embedded = new TrustedSigningKey( NetworkStorageRsaKey.Fingerprint( modulus, exponent ), modulus, exponent );
		if ( SecuritySigningKeyPin.Length > 0 )
		{
			if ( embedded.Fingerprint != SecuritySigningKeyPin )
				return new SecurityConfigRefusal( "ENCRYPTION_KEY_PIN_MISMATCH", "security config signing key does not match the pinned fingerprint" );
		}
		else if ( !TrustedSigningKeys.TryGetValue( ApiRoot, out var stored ) )
		{
			firstUse = embedded;
		}
		else if ( stored.Fingerprint != embedded.Fingerprint )
		{
			Log.Warning( $"[NetworkStorage] server signing key for {ApiRoot} changed from {stored.Fingerprint} to {embedded.Fingerprint}. Refusing the replacement; verify the server identity before changing its pin or local trust store." );
			return new SecurityConfigRefusal( "ENCRYPTION_SIGNING_KEY_CHANGED", "security config signing key differs from the key trusted on first use" );
		}

		return NetworkStorageSecuritySignature.VerifySecurityConfigSignature( config, embedded.Modulus, embedded.Exponent )
			? null
			: new SecurityConfigRefusal( "ENCRYPTION_CONFIG_UNVERIFIED", "security config signature did not verify with its signing key" );
	}

	// A received config was refused. A trusted config is kept; otherwise the refusal is recorded so encrypted calls stop
	// with its code. Synced settings are the mode baseline only and never grant trust.
	private static bool RefuseSecurityConfig( string reason, SecurityConfigRefusal refusal )
	{
		var kept = _runtimeSecurityConfig?.Trusted == true;
		if ( kept )
		{
			KeepCurrentSecurityConfig( reason, refusal.Code );
		}
		else
		{
			_runtimeSecurityConfig = RuntimeSecurityConfigSnapshot.FromSyncedSettings() with
			{
				Loaded = true,
				Source = "refused",
				LoadedAt = DateTimeOffset.UtcNow,
				TtlSeconds = SecurityConfigRetryDelaySeconds,
				LastRefreshReason = reason ?? "",
				LastMismatchCode = refusal.Code,
				RefusalCode = refusal.Code,
				RefusalMessage = refusal.Message
			};
		}

		Log.Warning( $"[NetworkStorage] security config refused reason={reason} code={refusal.Code} keptTrusted={kept}: {refusal.Message}" );
		if ( NetworkStorageLogConfig.LogErrors )
			NetLog.Error( "security-config", $"{refusal.Code}: {refusal.Message}" );
		return false;
	}

	// Keeps the current trusted or refused config through a failed refresh. Its freshness is not extended; the next
	// non-forced refresh is throttled instead.
	private static void KeepCurrentSecurityConfig( string reason, string code )
	{
		_runtimeSecurityConfig = _runtimeSecurityConfig with
		{
			LastRefreshReason = reason ?? "",
			LastMismatchCode = code ?? ""
		};
		_securityConfigRetryAfter = DateTimeOffset.UtcNow.AddSeconds( SecurityConfigRetryDelaySeconds );
	}

	private static bool UseSyncedSecurityConfig( string reason, string error )
	{
		var isEndpointMissing = (error ?? "").Contains( "404", StringComparison.OrdinalIgnoreCase )
			|| (error ?? "").Contains( "Not Found", StringComparison.OrdinalIgnoreCase );

		if ( _runtimeSecurityConfig?.Trusted == true || !string.IsNullOrEmpty( _runtimeSecurityConfig?.RefusalCode ) )
		{
			KeepCurrentSecurityConfig( reason, error );
			Log.Warning( $"[NetworkStorage] security config unavailable reason={reason} error={error}; keeping {_runtimeSecurityConfig.Source}" );
			_securityConfigUnavailableLogged = true;
			return false;
		}

		_runtimeSecurityConfig = RuntimeSecurityConfigSnapshot.FromSyncedSettings() with
		{
			Loaded = true,
			Source = isEndpointMissing ? "backend-missing-fallback" : "synced-fallback",
			LoadedAt = DateTimeOffset.UtcNow,
			TtlSeconds = isEndpointMissing ? 10 : 60,
			LastRefreshReason = reason ?? "",
			LastMismatchCode = error ?? ""
		};

		if ( isEndpointMissing )
		{
			if ( !_securityConfigUnavailableLogged )
				Log.Info( $"[NetworkStorage] security config endpoint unavailable; using synced config authSessions={EnableAuthSessions} encryptedRequests={EnableEncryptedRequests}" );
		}
		else
		{
			Log.Warning( $"[NetworkStorage] security config unavailable reason={reason} error={error}; using synced config authSessions={EnableAuthSessions} encryptedRequests={EnableEncryptedRequests}" );
		}

		_securityConfigUnavailableLogged = true;
		return false;
	}
}
