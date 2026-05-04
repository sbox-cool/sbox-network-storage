using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Result of the one-time revision init handshake with the backend.
/// </summary>
public struct RevisionInitResult
{
	/// <summary>True when the backend acknowledged the init successfully.</summary>
	public bool Ok { get; set; }

	/// <summary>True when the server reports a newer revision is available.</summary>
	public bool IsOutdatedRevision { get; set; }

	/// <summary>The revision this client is running, if known.</summary>
	public long? PlayerRevision { get; set; }

	/// <summary>The latest revision reported by the backend.</summary>
	public long? CurrentRevision { get; set; }

	/// <summary>Human-readable status or warning message from the server.</summary>
	public string Message { get; set; }
}

/// <summary>
/// Sends a one-time revision-init packet at game startup so the backend
/// knows which revision this client is running.
/// Call once after <see cref="NetworkStorage.Configure"/>.
/// </summary>
public static class NetworkStorageRevisionInit
{
	/// <summary>Cached result from the last successful <see cref="SendInitAsync"/> call.</summary>
	public static RevisionInitResult? LastInitResult { get; private set; }

	/// <summary>
	/// Send the revision init packet to the backend.
	/// Safe to call even when package/revision data is unavailable — the request
	/// proceeds without revision headers and the result reflects the gap.
	/// </summary>
	public static async Task<RevisionInitResult> SendInitAsync()
	{
		NetworkStorage.EnsureConfigured();

		// ── Detect package info if not already done ──
		if ( !NetworkStoragePackageInfo.IsDetected )
		{
			try
			{
				await NetworkStoragePackageInfo.DetectAsync();
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[NetworkStorage] revision-init: package detection failed — {ex.Message}" );
			}
		}

		// ── Build request body ──
		var revisionId = NetworkStoragePackageInfo.CurrentRevisionId;
		var body = new Dictionary<string, object>
		{
			{ "projectId", NetworkStorage.ProjectId },
			{ "clientType", "game" },
			{ "networkStorageVersion", "1.0" }
		};

		if ( !string.IsNullOrEmpty( NetworkStoragePackageInfo.PackageIdent ) )
			body["packageIdent"] = NetworkStoragePackageInfo.PackageIdent;

		if ( revisionId.HasValue )
			body["revisionId"] = revisionId.Value;

		// ── Build headers ──
		var headers = new Dictionary<string, string>
		{
			{ "x-public-key", NetworkStorage.ApiKey ?? "" }
		};

		if ( revisionId.HasValue )
			headers["x-ns-revision-id"] = revisionId.Value.ToString();

		headers["x-ns-client-type"] = "game";

		// ── Send POST ──
		var path = $"/{NetworkStorage.ApiVersion}/manage/{Uri.EscapeDataString( NetworkStorage.ProjectId )}/revision-init";
		var url = $"{NetworkStorage.BaseUrl}{path}?apiKey={Uri.EscapeDataString( NetworkStorage.ApiKey ?? "" )}";
		var tag = "revision-init";

		try
		{
			var content = Http.CreateJsonContent( body );

			if ( NetworkStorageLogConfig.LogRequests )
				Log.Info( $"[NetworkStorage] {tag}: POST {NetworkStorage.ApiRoot}/manage/{NetworkStorage.ProjectId}/revision-init" );

			var raw = await Http.RequestStringAsync( url, "POST", content, headers );

			if ( NetworkStorageLogConfig.LogResponses )
				Log.Info( $"[NetworkStorage] {tag} → {TruncateForLog( raw, 300 )}" );

			var result = ParseInitResponse( raw, revisionId );
			LastInitResult = result;

			Log.Info( $"[NetworkStorage] Game revision: {NetworkStoragePackageInfo.CurrentRevisionId?.ToString() ?? "unknown"}, server latest: {result.CurrentRevision?.ToString() ?? "unknown"}" );

			if ( result.IsOutdatedRevision )
				Log.Warning( $"[NetworkStorage] Running outdated revision. {result.Message}" );
			else
				Log.Info( $"[NetworkStorage] {tag}: ok={result.Ok} revision={result.PlayerRevision?.ToString() ?? "unknown"} message={result.Message ?? "none"}" );
			return result;
		}
		catch ( System.Net.Http.HttpRequestException httpEx )
		{
			var status = httpEx.StatusCode.HasValue ? $"{(int)httpEx.StatusCode.Value} {httpEx.StatusCode.Value}" : "unknown";
			Log.Warning( $"[NetworkStorage] {tag} FAILED — HTTP {status}" );

			var fail = new RevisionInitResult
			{
				Ok = false,
				IsOutdatedRevision = false,
				PlayerRevision = revisionId,
				CurrentRevision = null,
				Message = $"HTTP error: {status}"
			};
			LastInitResult = fail;
			return fail;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] {tag} FAILED — {ex.Message}" );

			var fail = new RevisionInitResult
			{
				Ok = false,
				IsOutdatedRevision = false,
				PlayerRevision = revisionId,
				CurrentRevision = null,
				Message = $"Request failed: {ex.Message}"
			};
			LastInitResult = fail;
			return fail;
		}
	}

	/// <summary>
	/// Parse the backend revision-init response into a <see cref="RevisionInitResult"/>.
	/// </summary>
	private static RevisionInitResult ParseInitResponse( string raw, long? clientRevision )
	{
		if ( string.IsNullOrEmpty( raw ) )
		{
			return new RevisionInitResult
			{
				Ok = false,
				PlayerRevision = clientRevision,
				Message = "Server returned empty response"
			};
		}

		JsonElement json;
		try
		{
			json = JsonSerializer.Deserialize<JsonElement>( raw );
		}
		catch
		{
			return new RevisionInitResult
			{
				Ok = false,
				PlayerRevision = clientRevision,
				Message = "Server returned invalid JSON"
			};
		}

		var ok = json.TryGetProperty( "ok", out var okProp ) && okProp.ValueKind == JsonValueKind.True;

		long? serverRevision = null;
		if ( json.TryGetProperty( "currentRevisionId", out var crProp ) && crProp.TryGetInt64( out var crVal ) )
			serverRevision = crVal;

		var isOutdated = false;
		if ( json.TryGetProperty( "revisionOutdated", out var outdatedProp ) && outdatedProp.ValueKind == JsonValueKind.True )
			isOutdated = true;
		else if ( clientRevision.HasValue && serverRevision.HasValue && clientRevision.Value < serverRevision.Value )
			isOutdated = true;

		string message = null;
		if ( json.TryGetProperty( "message", out var msgProp ) && msgProp.ValueKind == JsonValueKind.String )
			message = msgProp.GetString();

		return new RevisionInitResult
		{
			Ok = ok,
			IsOutdatedRevision = isOutdated,
			PlayerRevision = clientRevision,
			CurrentRevision = serverRevision,
			Message = message
		};
	}

	private static string TruncateForLog( string s, int max = 120 )
		=> s != null && s.Length > max ? s[..max] + "..." : s ?? "";
}
