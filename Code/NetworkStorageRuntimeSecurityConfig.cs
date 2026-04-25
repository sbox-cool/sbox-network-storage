using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	private static readonly TimeSpan RuntimeSecurityConfigMaxAge = TimeSpan.FromMinutes( 5 );
	private static RuntimeSecurityConfigSnapshot _runtimeSecurityConfig;
	private static Task<bool> _runtimeSecurityConfigLoadTask;
	private static bool _securityConfigUnavailableLogged;

	public static bool RuntimeSecurityConfigLoaded => _runtimeSecurityConfig?.Loaded == true;
	public static bool RuntimeEnableAuthSessions => _runtimeSecurityConfig?.EnableAuthSessions ?? EnableAuthSessions;
	public static bool RuntimeEnableEncryptedRequests => _runtimeSecurityConfig?.EnableEncryptedRequests ?? EnableEncryptedRequests;
	public static string RuntimeSecurityConfigVersion => _runtimeSecurityConfig?.ConfigVersion ?? "";
	public static string RuntimeSecurityClientMode => RuntimeEnableAuthSessions
		? RuntimeEnableEncryptedRequests ? "session+encrypted" : "session"
		: RuntimeEnableEncryptedRequests ? "encrypted" : "legacy";
	public static string RuntimeSecurityConfigSource => _runtimeSecurityConfig?.Source ?? "not-loaded";
	public static string LastSecurityConfigDiagnostics => BuildSecurityConfigDiagnostics();

	public static bool IsSecurityConfigMismatchCode( string code )
		=> !string.IsNullOrWhiteSpace( code )
			&& code.StartsWith( "SECURITY_", StringComparison.OrdinalIgnoreCase );

	public static async Task<bool> EnsureRuntimeSecurityConfigAsync( string reason = "first-use", bool forceRefresh = false )
	{
		EnsureConfigured();

		if ( !forceRefresh && _runtimeSecurityConfig?.IsFresh == true )
			return true;

		if ( !forceRefresh && _runtimeSecurityConfigLoadTask is not null && !_runtimeSecurityConfigLoadTask.IsCompleted )
			return await _runtimeSecurityConfigLoadTask;

		_runtimeSecurityConfigLoadTask = LoadRuntimeSecurityConfigAsync( reason, forceRefresh );
		return await _runtimeSecurityConfigLoadTask;
	}

	public static void MarkRuntimeSecurityMismatch( string code, string requestMode = "" )
	{
		if ( _runtimeSecurityConfig is null )
			_runtimeSecurityConfig = RuntimeSecurityConfigSnapshot.FromSyncedSettings();

		_runtimeSecurityConfig.LastMismatchCode = code ?? "";
		_runtimeSecurityConfig.LastRequestMode = requestMode ?? "";
		Log.Warning( $"[NetworkStorage] security mismatch code={code} mode={requestMode} diagnostics={LastSecurityConfigDiagnostics}" );
	}

	public static void ApplyServerExpectedSecurityConfig( string code, JsonElement expected, string expectedConfigVersion = null )
	{
		if ( expected.ValueKind != JsonValueKind.Object )
		{
			Log.Warning( $"[NetworkStorage] security mismatch {code} — no expected config provided, forcing refresh" );
			_ = EnsureRuntimeSecurityConfigAsync( $"mismatch-{code}", forceRefresh: true );
			return;
		}

		var authSessions = expected.TryGetProperty( "authSessions", out var authProp )
			? string.Equals( authProp.GetString(), "enabled", StringComparison.OrdinalIgnoreCase )
			: RuntimeEnableAuthSessions;
		var encryptedRequests = expected.TryGetProperty( "encryptedRequests", out var encProp )
			? string.Equals( encProp.GetString(), "required", StringComparison.OrdinalIgnoreCase )
			: RuntimeEnableEncryptedRequests;

		var previousMode = _runtimeSecurityConfig?.ModeText ?? "none";
		var previousAuthSessions = RuntimeEnableAuthSessions;
		var previousEncryptedRequests = RuntimeEnableEncryptedRequests;
		var previousVersion = _runtimeSecurityConfig?.ConfigVersion ?? "";
		var newVersion = !string.IsNullOrEmpty( expectedConfigVersion ) ? expectedConfigVersion : previousVersion;

		_runtimeSecurityConfig = new RuntimeSecurityConfigSnapshot
		{
			Loaded = true,
			Source = "server-expected",
			ProjectId = ProjectId,
			ConfigVersion = newVersion,
			LoadedAt = DateTimeOffset.UtcNow,
			TtlSeconds = 60,
			EnableAuthSessions = authSessions,
			EnableEncryptedRequests = encryptedRequests,
			SyncedEnableAuthSessions = EnableAuthSessions,
			SyncedEnableEncryptedRequests = EnableEncryptedRequests,
			LastRefreshReason = code,
			LastMismatchCode = code
		};
		EnableAuthSessions = authSessions;
		EnableEncryptedRequests = encryptedRequests;

		Log.Info( $"[NetworkStorage] security config auto-adapted from server expected values: "
			+ $"authSessions={previousAuthSessions}->{authSessions} encryptedRequests={previousEncryptedRequests}->{encryptedRequests} "
			+ $"version={previousVersion}->{newVersion} mode={previousMode}->{_runtimeSecurityConfig.ModeText} trigger={code}" );
	}

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

	private static string BuildSecurityConfigDiagnostics()
	{
		var runtime = _runtimeSecurityConfig;
		if ( runtime is null )
			return $"synced(authSessions={EnableAuthSessions}, encryptedRequests={EnableEncryptedRequests}) runtime=not-loaded";

		return $"synced(authSessions={runtime.SyncedEnableAuthSessions}, encryptedRequests={runtime.SyncedEnableEncryptedRequests}) "
			+ $"runtime(authSessions={runtime.EnableAuthSessions}, encryptedRequests={runtime.EnableEncryptedRequests}, source={runtime.Source}, "
			+ $"version={runtime.ConfigVersion}, ageSeconds={runtime.AgeSeconds}, mismatch={runtime.LastMismatchCode}, mode={runtime.ModeText})";
	}

	private static string ReadString( JsonElement element, string name )
		=> element.ValueKind == JsonValueKind.Object && element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

	private static int ReadInt( JsonElement element, string name, int fallback )
		=> element.ValueKind == JsonValueKind.Object && element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32( out var parsed ) ? parsed : fallback;

	private static bool ReadBool( JsonElement element, string name, bool fallback )
		=> element.ValueKind == JsonValueKind.Object && element.TryGetProperty( name, out var value )
			? value.ValueKind == JsonValueKind.True || (value.ValueKind != JsonValueKind.False && fallback)
			: fallback;

	private static DateTimeOffset ReadDateTimeOffset( JsonElement element, string name )
		=> DateTimeOffset.TryParse( ReadString( element, name ), out var parsed ) ? parsed : DateTimeOffset.UtcNow;

	private sealed record RuntimeSecurityConfigSnapshot
	{
		public bool Loaded { get; init; }
		public string Source { get; init; } = "synced";
		public string ProjectId { get; init; }
		public string ConfigVersion { get; init; } = "";
		public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
		public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;
		public int TtlSeconds { get; init; } = 60;
		public bool EnableAuthSessions { get; init; }
		public bool EnableEncryptedRequests { get; init; }
		public bool SyncedEnableAuthSessions { get; init; }
		public bool SyncedEnableEncryptedRequests { get; init; }
		public bool SignaturePresent { get; init; }
		public string LastRefreshReason { get; set; } = "";
		public string LastMismatchCode { get; set; } = "";
		public string LastRequestMode { get; set; } = "";
		public int AgeSeconds => Math.Max( 0, (int)(DateTimeOffset.UtcNow - LoadedAt).TotalSeconds );
		public bool IsFresh => Loaded && DateTimeOffset.UtcNow - LoadedAt < TimeSpan.FromSeconds( Math.Max( 1, TtlSeconds ) ) && DateTimeOffset.UtcNow - LoadedAt < RuntimeSecurityConfigMaxAge;
		public string ModeText => $"{(EnableAuthSessions ? "session" : "legacy")}+{(EnableEncryptedRequests ? "encrypted" : "plaintext")}";

		public static RuntimeSecurityConfigSnapshot FromSyncedSettings() => new()
		{
			Loaded = false,
			Source = "synced",
			ProjectId = NetworkStorage.ProjectId,
			EnableAuthSessions = NetworkStorage.EnableAuthSessions,
			EnableEncryptedRequests = NetworkStorage.EnableEncryptedRequests,
			SyncedEnableAuthSessions = NetworkStorage.EnableAuthSessions,
			SyncedEnableEncryptedRequests = NetworkStorage.EnableEncryptedRequests,
		};
	}
}
