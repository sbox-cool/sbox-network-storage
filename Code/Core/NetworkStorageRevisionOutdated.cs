using System;
using System.Text.Json;

namespace Sandbox;

/// <summary>
/// Why the revision was detected as outdated.
/// </summary>
public enum RevisionOutdatedReason
{
	/// <summary>Detected via the load-profile endpoint response.</summary>
	LoadProfileDetected,

	/// <summary>Triggered manually for testing (no backend required).</summary>
	ManualTest,

	/// <summary>Lobby metadata indicates a different revision.</summary>
	LobbyMismatch,

	/// <summary>Grace period expired, revision is now blocked.</summary>
	GraceExpired
}

/// <summary>
/// Data from the load-profile endpoint's revision block.
/// Fires <see cref="NetworkStorage.OnRevisionOutdated"/> when
/// the server reports the client's revision is outdated.
/// </summary>
public struct RevisionOutdatedData
{
	/// <summary>Creates a default instance.</summary>
	public RevisionOutdatedData() { }

	/// <summary>The revision the client is running.</summary>
	public long CurrentRevisionId { get; init; }

	/// <summary>The latest revision available on the server.</summary>
	public long LatestRevisionId { get; init; }

	/// <summary>True when the client revision is older than latest.</summary>
	public bool RevisionOutdated { get; init; }

	/// <summary>Seconds remaining in the grace period, or 0.</summary>
	public int GraceSeconds { get; init; }

	/// <summary>Unix timestamp when grace ends, or null.</summary>
	public long? GraceEndsAtUnixSeconds { get; init; }

	/// <summary>Server's current unix timestamp.</summary>
	public long ServerUnixSeconds { get; init; }

	/// <summary>
	/// Source identifier describing where the detection came from.
	/// Typical values: "load-profile", "manual-test", "lobby"
/// </summary>
	public string Source { get; init; }

	/// <summary>
	/// Why this revision-outdated event was raised.
	/// </summary>
	public RevisionOutdatedReason Reason { get; init; }

	// ── Enforcement mode fields (from _revisionStatus API) ──

	/// <summary>
	/// The enforcement mode from the server. Defaults to ForceUpgrade for backward compatibility.
	/// </summary>
	public RevisionEnforcementMode EnforcementMode { get; init; } = RevisionEnforcementMode.ForceUpgrade;

	/// <summary>
	/// Custom message from the server to display to players.
	/// </summary>
	public string Message { get; init; }

	/// <summary>
	/// What action is currently required: "warn", "block_saves", "block_all".
	/// </summary>
	public string Action { get; init; }

	/// <summary>
	/// True when the grace period has expired (from server).
	/// </summary>
	public bool GraceExpired { get; init; }

	/// <summary>
	/// Minutes remaining in the grace period (from server), or null if no grace.
	/// </summary>
	public int? GraceRemainingMinutes { get; init; }

	/// <summary>
	/// True if the server wants to show update options (Create/Join buttons).
	/// </summary>
	public bool ShowUpdateOptions { get; init; } = true;

	/// <summary>
	/// True if popup should only show once per session.
	/// </summary>
	public bool ShowPopupOnce { get; init; } = true;

	/// <summary>Seconds until grace expires (negative if already expired).</summary>
	public int TimeRemaining
	{
		get
		{
			if ( !GraceEndsAtUnixSeconds.HasValue )
				return 0;
			var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			return (int)Math.Max( 0, GraceEndsAtUnixSeconds.Value - now );
		}
	}

	/// <summary>True when the grace period has fully expired.</summary>
	public bool IsGraceExpired
	{
		get
		{
			if ( !GraceEndsAtUnixSeconds.HasValue )
				return false;
			var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			return now >= GraceEndsAtUnixSeconds.Value;
		}
	}

	internal static RevisionOutdatedData? FromJson( JsonElement json )
	{
		if ( json.ValueKind != JsonValueKind.Object )
			return null;

		if ( !json.TryGetProperty( "revisionOutdated", out var outdated ) )
			return null;

		return new RevisionOutdatedData
		{
			CurrentRevisionId = json.TryGetProperty( "currentRevisionId", out var cr ) && cr.ValueKind == JsonValueKind.Number ? cr.GetInt64() : 0,
			LatestRevisionId = json.TryGetProperty( "latestRevisionId", out var lr ) && lr.ValueKind == JsonValueKind.Number ? lr.GetInt64() : 0,
			RevisionOutdated = outdated.ValueKind == JsonValueKind.True,
			GraceSeconds = json.TryGetProperty( "graceSeconds", out var gs ) && gs.ValueKind == JsonValueKind.Number ? gs.GetInt32() : 0,
			GraceEndsAtUnixSeconds = json.TryGetProperty( "graceEndsAtUnixSeconds", out var ge ) && ge.ValueKind == JsonValueKind.Number ? ge.GetInt64() : (long?)null,
			ServerUnixSeconds = json.TryGetProperty( "serverUnixSeconds", out var su ) && su.ValueKind == JsonValueKind.Number ? su.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
			Source = "load-profile",
			Reason = RevisionOutdatedReason.LoadProfileDetected,
		};
	}
}

public static partial class NetworkStorage
{
	/// <summary>
	/// Fired when the load-profile endpoint reports an outdated revision,
	/// or when <see cref="TestFireRevisionOutdated"/> is called.
	/// Subscribe to show custom in-game UI for revision warnings.
	/// </summary>
	public static event Action<RevisionOutdatedData> OnRevisionOutdated;

	/// <summary>
	/// Called internally by <see cref="NetworkStorageResponse"/> when a
	/// load-profile response contains a <c>revision</c> block.
	/// </summary>
	internal static void FireRevisionOutdated( RevisionOutdatedData data )
	{
		if ( data.RevisionOutdated )
		{
			NetworkStoragePackageInfo.UpdateFromRevisionBlock( data );
			OnRevisionOutdated?.Invoke( data );
		}
	}
}
