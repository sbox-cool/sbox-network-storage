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

}
