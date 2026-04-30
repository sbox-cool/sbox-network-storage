using System;
using System.Text.Json;

namespace Sandbox;

public static partial class NetworkStorage
{
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
	}}
