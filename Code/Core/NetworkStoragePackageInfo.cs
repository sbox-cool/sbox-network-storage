using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Detects and caches package/revision information from s&amp;box APIs.
/// Used to track which revision is running and to build sync payloads
/// for the sboxcool.com backend.
/// </summary>
public static class NetworkStoragePackageInfo
{
	/// <summary>The revision ID of the currently running build.</summary>
	public static long? CurrentRevisionId { get; private set; }

	/// <summary>The latest published revision ID for the package.</summary>
	public static long? LatestRevisionId { get; private set; }

	/// <summary>The full package ident, e.g. "org.game".</summary>
	public static string PackageIdent { get; private set; }

	/// <summary>The organization ident, e.g. "hooked_inc".</summary>
	public static string OrgIdent { get; private set; }

	/// <summary>The human-readable package title.</summary>
	public static string PackageTitle { get; private set; }

	/// <summary>The publish status of the current revision (e.g. "live").</summary>
	public static string PublishStatus { get; private set; }

	/// <summary>The package type (e.g. "game", "addon", "map").</summary>
	public static string PackageType { get; private set; }

	/// <summary>True if detection has run and found package info.</summary>
	public static bool IsDetected { get; private set; }

	// ── Revision status from server responses ──

	/// <summary>Latest revision status: true when running an older revision.</summary>
	public static bool IsOutdatedRevision { get; private set; }

	/// <summary>Minutes remaining in the grace period, if applicable.</summary>
	public static int? GraceRemainingMinutes { get; private set; }

	/// <summary>True when the grace period has expired.</summary>
	public static bool GraceExpired { get; private set; }

	/// <summary>Current enforcement action: "warn", "block_writes", or "block_all".</summary>
	public static string RevisionAction { get; private set; }

	/// <summary>Human-readable message from the server about revision status.</summary>
	public static string RevisionMessage { get; private set; }

	/// <summary>The server's current (latest) revision ID.</summary>
	public static long? ServerCurrentRevision { get; private set; }

	/// <summary>Revision policy synced from server.</summary>
	public static int? PolicyGracePeriodMinutes { get; private set; }
	public static string PolicyPostGraceAction { get; private set; }
	public static bool PolicyForceEndpointUpgrade { get; private set; }
	public static string PolicyNotifyMessage { get; private set; }
	public static bool PolicyShowUpdateOptions { get; private set; } = true;
	public static bool PolicyShowPopupOnce { get; private set; } = true;
	public static bool PolicyShowDefaultMessage { get; private set; } = true;

	/// <summary>Enforcement mode from server: ForceUpgrade (default) or AllowContinue.</summary>
	public static RevisionEnforcementMode EnforcementMode { get; private set; } = RevisionEnforcementMode.ForceUpgrade;

	/// <summary>Fired when revision status changes. Game code can subscribe to show UI.</summary>
	public static event Action<RevisionStatusInfo> OnRevisionStatusChanged;

	/// <summary>The raw Package object from the last detection, if available.</summary>
	private static Package _cachedPackage;

	/// <summary>
	/// Detect package and revision info from the current s&amp;box context.
	/// Safe to call multiple times — subsequent calls re-detect from scratch.
	/// </summary>
	public static async Task DetectAsync()
	{
		Reset();

		string ident = null;

		try
		{
			ident = Game.Ident;
		}
		catch ( Exception ex )
		{
			Log.Info( $"[NetworkStorage] PackageInfo: Game.Ident unavailable — {ex.Message}" );
		}

		if ( string.IsNullOrWhiteSpace( ident ) )
		{
			Log.Info( "[NetworkStorage] PackageInfo: No game ident available (editor-only or local project)" );
			return;
		}

		PackageIdent = ident;

		Package package = null;

		try
		{
			package = await Package.FetchAsync( ident, false );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] PackageInfo: Failed to fetch package '{ident}' — {ex.Message}" );
			return;
		}

		if ( package is null )
		{
			Log.Warning( $"[NetworkStorage] PackageInfo: Package.FetchAsync returned null for '{ident}'" );
			return;
		}

		_cachedPackage = package;

		// Read package metadata
		PackageTitle = package.Title;
		PackageType = package.TypeName;
		PackageIdent = package.FullIdent ?? ident;

		// Read organization
		try
		{
			var org = package.Org;
			OrgIdent = org?.Ident ?? org?.ToString();
		}
		catch
		{
			// Org may not be available on all package types
		}

		// Read revision info
		if ( package.Revision is not null )
		{
			CurrentRevisionId = package.Revision.VersionId;
		}

		// IRevision does not expose a Published property.
		// Derive publish status from whether the package is public and has a revision.
		PublishStatus = package.Revision is not null
			? ( package.Public ? "live" : "unlisted" )
			: "local";

		// Latest revision ID — use the same revision if no separate latest is exposed
		LatestRevisionId = CurrentRevisionId;

		IsDetected = true;

		Log.Info( $"[NetworkStorage] PackageInfo: Detected — ident={PackageIdent}, org={OrgIdent ?? "(none)"}, title={PackageTitle ?? "(none)"}, type={PackageType ?? "(unknown)"}, revision={CurrentRevisionId?.ToString() ?? "null"}, status={PublishStatus ?? "unknown"}" );
	}

	/// <summary>
	/// Build the package-sync payload matching the backend contract:
	/// POST /v3/manage/:projectId/package-sync
	/// </summary>
	public static JsonElement BuildSyncPayload()
	{
		using var stream = new MemoryStream();
		using ( var writer = new Utf8JsonWriter( stream ) )
		{
			writer.WriteStartObject();

			writer.WriteString( "packageId", PackageIdent ?? "" );
			writer.WriteString( "packageIdent", PackageIdent ?? "" );
			writer.WriteString( "packageTitle", PackageTitle ?? "" );
			writer.WriteString( "orgIdent", OrgIdent ?? "" );
			writer.WriteString( "packageType", PackageType ?? "" );

			if ( CurrentRevisionId.HasValue )
				writer.WriteNumber( "currentRevisionId", CurrentRevisionId.Value );
			else
				writer.WriteNull( "currentRevisionId" );

			if ( LatestRevisionId.HasValue )
				writer.WriteNumber( "latestRevisionId", LatestRevisionId.Value );
			else
				writer.WriteNull( "latestRevisionId" );

			writer.WriteString( "publishStatus", PublishStatus ?? "" );

			// rawPackage / rawRevision / rawBundle — include empty objects
			// when the raw data isn't available, so the backend always
			// receives the expected shape.
			writer.WriteStartObject( "rawPackage" );
			if ( _cachedPackage is not null )
			{
				writer.WriteString( "fullIdent", _cachedPackage.FullIdent ?? "" );
				writer.WriteString( "title", _cachedPackage.Title ?? "" );
				writer.WriteString( "packageType", _cachedPackage.TypeName ?? "" );
				writer.WriteString( "org", _cachedPackage.Org?.Ident ?? "" );
				writer.WriteString( "orgTitle", _cachedPackage.Org?.Title ?? "" );
				writer.WriteBoolean( "isPublic", _cachedPackage.Public );
			}
			writer.WriteEndObject();

			writer.WriteStartObject( "rawRevision" );
			if ( _cachedPackage?.Revision is not null )
			{
				writer.WriteNumber( "versionId", _cachedPackage.Revision.VersionId );
				writer.WriteNumber( "engineVersion", _cachedPackage.Revision.EngineVersion );
				writer.WriteString( "summary", _cachedPackage.Revision.Summary ?? "" );
				writer.WriteString( "created", _cachedPackage.Revision.Created.ToString( "o" ) );
			}
			writer.WriteEndObject();

			writer.WriteStartObject( "rawBundle" );
			writer.WriteEndObject();

			writer.WriteEndObject();
		}

		return JsonSerializer.Deserialize<JsonElement>( stream.ToArray() );
	}

	/// <summary>
	/// Clear all cached detection data.
	/// </summary>
	public static void Reset()
	{
		CurrentRevisionId = null;
		LatestRevisionId = null;
		PackageIdent = null;
		OrgIdent = null;
		PackageTitle = null;
		PublishStatus = null;
		PackageType = null;
		IsDetected = false;
		_cachedPackage = null;
		IsOutdatedRevision = false;
		GraceRemainingMinutes = null;
		GraceExpired = false;
		RevisionAction = null;
		RevisionMessage = null;
		ServerCurrentRevision = null;
		PolicyGracePeriodMinutes = null;
		PolicyPostGraceAction = null;
		PolicyForceEndpointUpgrade = false;
		PolicyNotifyMessage = null;
		PolicyShowUpdateOptions = true;
		PolicyShowPopupOnce = true;
		PolicyShowDefaultMessage = true;
		EnforcementMode = RevisionEnforcementMode.ForceUpgrade;
	}

	// ── Revision status from server responses ──

	/// <summary>
	/// Extract <c>_revisionStatus</c> from any server response and update static state.
	/// Called automatically by ParseResponse on every endpoint call.
	/// </summary>
	public static void UpdateFromServerResponse( JsonElement response )
	{
		if ( !response.TryGetProperty( "_revisionStatus", out var rs ) || rs.ValueKind != JsonValueKind.Object )
			return;

		var wasOutdated = IsOutdatedRevision;
		var wasGraceExpired = GraceExpired;
		var wasAction = RevisionAction;

		IsOutdatedRevision = rs.TryGetProperty( "isOutdatedRevision", out var o ) && o.ValueKind == JsonValueKind.True;
		GraceRemainingMinutes = rs.TryGetProperty( "graceRemainingMinutes", out var g ) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : null;
		GraceExpired = rs.TryGetProperty( "graceExpired", out var ge ) && ge.ValueKind == JsonValueKind.True;
		RevisionAction = rs.TryGetProperty( "action", out var a ) ? a.GetString() : null;
		RevisionMessage = rs.TryGetProperty( "message", out var m ) ? m.GetString() : null;
		ServerCurrentRevision = rs.TryGetProperty( "currentRevision", out var cr ) && cr.ValueKind == JsonValueKind.Number ? cr.GetInt64() : null;

		// Parse enforcement mode (default to ForceUpgrade for backward compatibility)
		EnforcementMode = RevisionEnforcementMode.ForceUpgrade;
		if ( rs.TryGetProperty( "enforcementMode", out var em ) && em.ValueKind == JsonValueKind.String )
		{
			var modeStr = em.GetString();
			if ( modeStr == "allow_continue" )
				EnforcementMode = RevisionEnforcementMode.AllowContinue;
		}

		// Read revision policy from server
		if ( rs.TryGetProperty( "policy", out var policy ) && policy.ValueKind == JsonValueKind.Object )
		{
			PolicyGracePeriodMinutes = policy.TryGetProperty( "gracePeriodMinutes", out var pg ) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : null;
			PolicyPostGraceAction = policy.TryGetProperty( "postGraceAction", out var pga ) ? pga.GetString() : null;
			PolicyForceEndpointUpgrade = policy.TryGetProperty( "forceEndpointUpgrade", out var feu ) && feu.ValueKind == JsonValueKind.True;
			PolicyNotifyMessage = policy.TryGetProperty( "notifyMessage", out var nm ) ? nm.GetString() : null;
			PolicyShowUpdateOptions = !policy.TryGetProperty( "showUpdateOptions", out var suo ) || suo.ValueKind != JsonValueKind.False;
			PolicyShowPopupOnce = !policy.TryGetProperty( "showPopupOnce", out var spo ) || spo.ValueKind != JsonValueKind.False;
			PolicyShowDefaultMessage = !policy.TryGetProperty( "showDefaultMessage", out var sdm ) || sdm.ValueKind != JsonValueKind.False;

			// Also check enforcement mode in policy (fallback)
			if ( EnforcementMode == RevisionEnforcementMode.ForceUpgrade && 
			     policy.TryGetProperty( "enforcementMode", out var pem ) && pem.ValueKind == JsonValueKind.String )
			{
				if ( pem.GetString() == "allow_continue" )
					EnforcementMode = RevisionEnforcementMode.AllowContinue;
			}
		}
	
		if ( !IsOutdatedRevision )
		{
			RevisionMessage = null;
		}
		else
		{
			if ( GraceExpired )
				Log.Warning( $"[NetworkStorage] REVISION EXPIRED: {RevisionMessage}" );
			else if ( GraceRemainingMinutes.HasValue )
				Log.Warning( $"[NetworkStorage] Outdated revision — {GraceRemainingMinutes}min remaining. {RevisionMessage}" );
			else
				Log.Info( $"[NetworkStorage] Outdated revision: {RevisionMessage}" );
		}

		// Fire event when status materially changes
		if ( IsOutdatedRevision != wasOutdated || GraceExpired != wasGraceExpired || RevisionAction != wasAction )
		{
			OnRevisionStatusChanged?.Invoke( new RevisionStatusInfo
			{
				IsOutdated = IsOutdatedRevision,
				GraceExpired = GraceExpired,
				GraceRemainingMinutes = GraceRemainingMinutes,
				Action = RevisionAction,
				Message = RevisionMessage,
				PlayerRevision = CurrentRevisionId,
				CurrentRevision = ServerCurrentRevision,
				PolicyGracePeriodMinutes = PolicyGracePeriodMinutes,
				PolicyPostGraceAction = PolicyPostGraceAction,
				PolicyForceEndpointUpgrade = PolicyForceEndpointUpgrade,
				PolicyNotifyMessage = PolicyNotifyMessage,
				PolicyShowUpdateOptions = PolicyShowUpdateOptions,
				PolicyShowPopupOnce = PolicyShowPopupOnce,
				PolicyShowDefaultMessage = PolicyShowDefaultMessage,
				EnforcementMode = EnforcementMode
			} );
		}
	}
	
	/// <summary>
	/// Extract <c>revision</c> block from a load-profile response and update static state.
	/// Called automatically by <see cref="NetworkStorage.FireRevisionOutdated"/>.
	/// </summary>
	internal static void UpdateFromRevisionBlock( RevisionOutdatedData data )
	{
		if ( !data.RevisionOutdated )
			return;
	
		var wasOutdated = IsOutdatedRevision;
		var wasGraceExpired = GraceExpired;
	
		IsOutdatedRevision = true;
		ServerCurrentRevision = data.LatestRevisionId;
		GraceRemainingMinutes = data.GraceSeconds > 0 ? data.GraceSeconds / 60 : null;
		GraceExpired = data.IsGraceExpired;
		RevisionAction = data.IsGraceExpired ? "block_writes" : "warn";
		RevisionMessage = data.IsGraceExpired
			? "Your revision has expired. Join a new game session to update."
			: $"A new version is available. Update your game to continue. ({data.TimeRemaining}s remaining)";
	
		// Fire the general revision status change event so existing handlers see the update
		if ( IsOutdatedRevision != wasOutdated || GraceExpired != wasGraceExpired )
		{
			OnRevisionStatusChanged?.Invoke( new RevisionStatusInfo
			{
				IsOutdated = IsOutdatedRevision,
				GraceExpired = GraceExpired,
				GraceRemainingMinutes = GraceRemainingMinutes,
				Action = RevisionAction,
				Message = RevisionMessage,
				PlayerRevision = CurrentRevisionId,
				CurrentRevision = ServerCurrentRevision,
			} );
		}
	}
}

/// <summary>
/// Snapshot of revision status, passed to <see cref="NetworkStoragePackageInfo.OnRevisionStatusChanged"/>.
/// </summary>
public struct RevisionStatusInfo
{
	public bool IsOutdated;
	public bool GraceExpired;
	public int? GraceRemainingMinutes;
	public string Action;
	public string Message;
	public long? PlayerRevision;
	public long? CurrentRevision;
	public int? PolicyGracePeriodMinutes;
	public string PolicyPostGraceAction;
	public bool PolicyForceEndpointUpgrade;
	public string PolicyNotifyMessage;
	public bool PolicyShowUpdateOptions;
	public bool PolicyShowPopupOnce;
	public bool PolicyShowDefaultMessage;
	public RevisionEnforcementMode EnforcementMode;
}
