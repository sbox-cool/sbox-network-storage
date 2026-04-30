using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	private static async Task<bool> LoadRuntimeSecurityConfigAsync( string reason, bool forceRefresh )
	{
		var url = $"{ApiRoot}/security-config/{Uri.EscapeDataString( ProjectId )}";
		if ( forceRefresh )
			url += "?refresh=1";

		try
		{
			var raw = await Http.RequestStringAsync( url, "GET", null, null );
			if ( !TryParseRuntimeSecurityConfig( raw, out var loaded, out var error ) )
				return UseSyncedSecurityConfig( reason, error );

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
			EnableAuthSessions = loaded.EnableAuthSessions;
			EnableEncryptedRequests = loaded.EnableEncryptedRequests;
			Log.Info( $"[NetworkStorage] security config loaded reason={reason} {previousMode}->{_runtimeSecurityConfig.ModeText} version={loaded.ConfigVersion} ttl={loaded.TtlSeconds}s" );
			return true;
		}
		catch ( Exception ex )
		{
			return UseSyncedSecurityConfig( reason, ex.Message );
		}
	}

	private static bool TryParseRuntimeSecurityConfig( string raw, out RuntimeSecurityConfigSnapshot snapshot, out string error )
	{
		snapshot = null;
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

			if ( !NetworkStorageSecuritySignature.VerifySecurityConfigSignature( config, out var verificationMode ) )
			{
				error = "invalid signature";
				return false;
			}
			if ( string.Equals( verificationMode, "config-version-fallback", StringComparison.OrdinalIgnoreCase ) )
				Log.Warning( "[NetworkStorage] security config RSA verification failed; accepted HTTPS config with config-version integrity fallback" );

			var settings = config.TryGetProperty( "settings", out var settingsProp ) ? settingsProp : default;
			snapshot = new RuntimeSecurityConfigSnapshot
			{
				ProjectId = projectId,
				ConfigVersion = ReadString( config, "configVersion" ),
				GeneratedAt = ReadDateTimeOffset( config, "generatedAt" ),
				TtlSeconds = ReadInt( config, "ttlSeconds", 60 ),
				EnableAuthSessions = ReadBool( settings, "enableAuthSessions", EnableAuthSessions ),
				EnableEncryptedRequests = ReadBool( settings, "enableEncryptedRequests", EnableEncryptedRequests ),
				SignaturePresent = true
			};
			return true;
		}
		catch ( Exception ex )
		{
			error = ex.Message;
			return false;
		}
	}

	private static bool UseSyncedSecurityConfig( string reason, string error )
	{
		var isEndpointMissing = (error ?? "").Contains( "404", StringComparison.OrdinalIgnoreCase )
			|| (error ?? "").Contains( "Not Found", StringComparison.OrdinalIgnoreCase );

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
