using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Network Storage client for sboxcool.com.
///
/// Auto-configures from network-storage.credentials.json on first use.
/// You can also call Configure() manually to override.
///
/// Example:
///   var player = await NetworkStorage.CallEndpoint( "load-player" );
///   var values = await NetworkStorage.GetGameValues();
/// </summary>
public static class NetworkStorage
{
	// ── Configuration ──

	/// <summary>Base API URL (no trailing slash).</summary>
	public static string BaseUrl { get; private set; } = "https://api.sboxcool.com";

	/// <summary>API version prefix.</summary>
	public static string ApiVersion { get; private set; } = "v3";

	/// <summary>Your project ID from the sboxcool.com dashboard.</summary>
	public static string ProjectId { get; private set; }

	/// <summary>Your public API key (sbox_ns_ prefix).</summary>
	public static string ApiKey { get; private set; }

	/// <summary>True after Configure() or auto-config has loaded valid credentials.</summary>
	public static bool IsConfigured => !string.IsNullOrEmpty( ProjectId ) && !string.IsNullOrEmpty( ApiKey );

	/// <summary>Optional CDN URL for storage reads (no trailing slash). When set and is a CDN domain, cache-busting is applied.</summary>
	public static string CdnUrl { get; private set; }

	/// <summary>The full versioned API root, e.g. https://api.sboxcool.com/v3</summary>
	public static string ApiRoot => $"{BaseUrl}/{ApiVersion}";

	private static bool _autoConfigAttempted;

	/// <summary>
	/// Configure the client manually. Call once at game startup.
	/// If not called, the client auto-configures from credentials file on first use.
	/// </summary>
	public static void Configure( string projectId, string apiKey, string baseUrl = null, string apiVersion = null, string cdnUrl = null )
	{
		ProjectId = projectId;
		ApiKey = apiKey;
		if ( !string.IsNullOrEmpty( baseUrl ) ) BaseUrl = baseUrl.TrimEnd( '/' );
		if ( !string.IsNullOrEmpty( apiVersion ) ) ApiVersion = apiVersion.Trim( '/' );
		CdnUrl = string.IsNullOrEmpty( cdnUrl ) ? null : cdnUrl.TrimEnd( '/' );
		_autoConfigAttempted = true;
		NetLog.Info( "config", $"NetworkStorage ready — {ApiRoot}" );
	}

	/// <summary>
	/// Reset the auto-configure guard so AutoConfigure() can retry.
	/// Call this when the initial attempt failed and you want to retry
	/// (e.g. non-host client whose filesystem wasn't mounted yet).
	/// </summary>
	public static void ResetAutoConfigureFlag()
	{
		_autoConfigAttempted = false;
	}

	/// <summary>
	/// Auto-configure from network-storage.credentials.json.
	/// Called automatically on first API use.
	/// </summary>
	public static void AutoConfigure()
	{
		if ( _autoConfigAttempted ) return;
		_autoConfigAttempted = true;

		NetworkStorageBootstrap.CheckEditorOnce();

		const string fileName = "network-storage.credentials.json";

		string contents = null;
		string[] candidates = {
			fileName,
			$"/{fileName}",
			$"Assets/{fileName}",
			$"/Assets/{fileName}",
		};

		foreach ( var path in candidates )
		{
			if ( FileSystem.Mounted.FileExists( path ) )
			{
				contents = FileSystem.Mounted.ReadAllText( path );
				break;
			}
		}

		if ( string.IsNullOrEmpty( contents ) )
		{
			Log.Warning( "[NetworkStorage] No network-storage.credentials.json found — run Editor → Network Storage → Setup" );
			return;
		}

		try
		{
			var json = JsonSerializer.Deserialize<JsonElement>( contents );

			var projectId = json.TryGetProperty( "projectId", out var pid ) ? pid.GetString() : null;
			var publicKey = json.TryGetProperty( "publicKey", out var pk ) ? pk.GetString() : null;
			var baseUrl = json.TryGetProperty( "baseUrl", out var bu ) ? bu.GetString() : null;
			var apiVersion = json.TryGetProperty( "apiVersion", out var av ) ? av.GetString() : null;
			var cdnUrl = json.TryGetProperty( "cdnUrl", out var cu ) ? cu.GetString() : null;

			if ( !string.IsNullOrEmpty( projectId ) && !string.IsNullOrEmpty( publicKey ) )
			{
				Configure( projectId, publicKey, baseUrl, apiVersion, cdnUrl );
			}
			else
			{
				Log.Warning( "[NetworkStorage] credentials.json missing projectId or publicKey" );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] Failed to parse credentials.json: {ex.Message}" );
		}
	}

	// ── Endpoints ──

	/// <summary>
	/// Call a server endpoint by slug.
	/// Returns the response body on success, null on any failure.
	/// </summary>
	public static async Task<JsonElement?> CallEndpoint( string slug, object input = null )
	{
		EnsureConfigured();
		string url = null;
		string bodyJson = null;
		try
		{
			url = await BuildUrl( $"/endpoints/{ProjectId}/{slug}" );

			string result;
			if ( input is not null )
			{
				bodyJson = JsonSerializer.Serialize( input );
				NetLog.Request( slug, $"POST {bodyJson}" );
				Log.Info( $"[NetworkStorage] {slug} request: POST {ApiRoot}/endpoints/{ProjectId}/{slug} body={bodyJson}" );
				var content = Http.CreateJsonContent( input );
				result = await Http.RequestStringAsync( url, "POST", content );
			}
			else
			{
				NetLog.Request( slug, "GET" );
				Log.Info( $"[NetworkStorage] {slug} request: GET {ApiRoot}/endpoints/{ProjectId}/{slug}" );
				result = await Http.RequestStringAsync( url );
			}

			Log.Info( $"[NetworkStorage] {slug} → {result}" );
			var parsed = ParseResponse( slug, result );
			if ( parsed.HasValue )
				NetLog.Response( slug, TruncateJson( parsed.Value ) );
			return parsed;
		}
		catch ( System.Net.Http.HttpRequestException httpEx )
		{
			var status = httpEx.StatusCode.HasValue ? $"{(int)httpEx.StatusCode.Value} {httpEx.StatusCode.Value}" : "unknown";
			Log.Warning( $"[NetworkStorage] {slug} FAILED — HTTP {status}" );
			Log.Warning( $"[NetworkStorage]   URL: {ApiRoot}/endpoints/{ProjectId}/{slug}" );
			Log.Warning( $"[NetworkStorage]   Method: {( input is not null ? "POST" : "GET" )}" );
			if ( bodyJson != null )
				Log.Warning( $"[NetworkStorage]   Body: {bodyJson}" );
			Log.Warning( $"[NetworkStorage]   Note: s&box Http API does not expose error response bodies — check server logs for details" );
			NetLog.Error( slug, $"HTTP {status}" );
			return null;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] {slug} FAILED — {ex.Message}" );
			Log.Warning( $"[NetworkStorage]   URL: {ApiRoot}/endpoints/{ProjectId}/{slug}" );
			Log.Warning( $"[NetworkStorage]   Method: {( input is not null ? "POST" : "GET" )}" );
			if ( bodyJson != null )
				Log.Warning( $"[NetworkStorage]   Body: {bodyJson}" );
			Log.Warning( $"[NetworkStorage]   Exception: {ex}" );
			NetLog.Error( slug, ex.Message );
			return null;
		}
	}

	// ── Game Values ──

	/// <summary>
	/// Load all game values (groups + tables).
	/// </summary>
	public static async Task<JsonElement?> GetGameValues()
	{
		EnsureConfigured();
		try
		{
			var url = await BuildUrl( $"/values/{ProjectId}" );
			NetLog.Request( "game-values", $"GET {ApiRoot}/values/{ProjectId}" );
			var result = await Http.RequestStringAsync( url );
			Log.Info( $"[NetworkStorage] game-values → {TruncateJson( result, 300 )}" );
			var parsed = ParseResponse( "game-values", result );
			if ( parsed.HasValue )
				NetLog.Response( "game-values", $"OK ({result.Length} bytes)" );
			return parsed;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] GameValues: {ex.Message}" );
			NetLog.Error( "game-values", ex.Message );
			return null;
		}
	}

	// ── Collections ──

	/// <summary>
	/// Read a document from a collection.
	/// If documentId is null, uses the current player's Steam ID.
	/// </summary>
	public static async Task<JsonElement?> GetDocument( string collectionId, string documentId = null )
	{
		EnsureConfigured();
		try
		{
			var docId = documentId ?? Game.SteamId.ToString();
			var url = await BuildUrl( $"/storage/{ProjectId}/{collectionId}/{docId}" );

			NetLog.Request( "storage", $"GET {collectionId}/{docId}" );
			var result = await Http.RequestStringAsync( url );
			Log.Info( $"[NetworkStorage] storage → {TruncateJson( result, 300 )}" );
			var parsed = ParseResponse( "storage", result );
			if ( parsed.HasValue )
				NetLog.Response( "storage", $"OK ({result.Length} bytes)" );
			return parsed;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] GetDocument: {ex.Message}" );
			NetLog.Error( "storage", ex.Message );
			return null;
		}
	}

	// ── Internals ──

	public static void EnsureConfigured()
	{
		if ( !IsConfigured )
			AutoConfigure();

		if ( !IsConfigured )
			throw new InvalidOperationException( "NetworkStorage not configured. Add credentials via Editor → Network Storage → Setup, or call NetworkStorage.Configure() manually." );
	}

	private static bool IsCdnRoot( string root )
		=> root.Contains( "storage.sbox.cool" ) || root.Contains( "storage.sboxcool.com" );

	private static async Task<string> BuildUrl( string path )
	{
		var auth = await BuildAuthQuery();
		if ( IsCdnRoot( ApiRoot ) )
		{
			var v = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			return $"{ApiRoot}{path}{auth}&v={v}";
		}
		return $"{ApiRoot}{path}{auth}";
	}

	private static async Task<string> BuildAuthQuery()
	{
		var steamId = Game.SteamId.ToString();
		string token = null;
		try
		{
			token = await Services.Auth.GetToken( "sbox-network-storage" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] Failed to get auth token: {ex.Message}" );
		}

		if ( string.IsNullOrEmpty( token ) )
			Log.Warning( "[NetworkStorage] Auth token is empty — requests may fail" );

		return $"?apiKey={ApiKey}&steamId={steamId}&token={token}";
	}

	/// <summary>
	/// Parse a server response. Returns the response data on success, null on any error.
	/// Detects errors via: ok=false, error object, or non-JSON responses.
	/// </summary>
	private static JsonElement? ParseResponse( string slug, string raw )
	{
		if ( string.IsNullOrEmpty( raw ) )
		{
			NetLog.Error( slug, "Server returned empty response" );
			return null;
		}

		// Catch HTML error pages or non-JSON responses early
		var trimmed = raw.TrimStart();
		if ( trimmed.Length > 0 && trimmed[0] != '{' && trimmed[0] != '[' )
		{
			NetLog.Error( slug, $"Non-JSON response: {raw[..Math.Min( raw.Length, 120 )]}" );
			return null;
		}

		JsonElement json;
		try
		{
			json = JsonSerializer.Deserialize<JsonElement>( raw );
		}
		catch
		{
			NetLog.Error( slug, $"Invalid JSON: {raw[..Math.Min( raw.Length, 200 )]}" );
			return null;
		}

		// ── Error detection (multiple patterns for robustness) ──

		// 1) Explicit ok: false
		if ( json.TryGetProperty( "ok", out var ok ) && ok.ValueKind == JsonValueKind.False )
		{
			LogServerError( slug, json );
			return null;
		}

		// 2) Error object without ok field (legacy / edge cases)
		if ( json.TryGetProperty( "error", out var errProp ) )
		{
			// { error: { code, message } } — structured error
			if ( errProp.ValueKind == JsonValueKind.Object )
			{
				// Only treat as error if there's no ok:true (some responses include error metadata alongside success)
				if ( !json.TryGetProperty( "ok", out var okCheck ) || okCheck.ValueKind != JsonValueKind.True )
				{
					LogServerError( slug, json );
					return null;
				}
			}
			// { error: "string message" } — simple error
			else if ( errProp.ValueKind == JsonValueKind.String )
			{
				Log.Warning( $"[NetworkStorage] {slug}: {errProp.GetString()}" );
				NetLog.Error( slug, errProp.GetString() );
				return null;
			}
		}

		// 3) HTTP error status forwarded as { status: 4xx/5xx }
		if ( json.TryGetProperty( "status", out var status ) && status.ValueKind == JsonValueKind.Number )
		{
			var statusCode = status.GetInt32();
			if ( statusCode >= 400 )
			{
				LogServerError( slug, json );
				return null;
			}
		}

		// ── Success — extract response body ──

		// Server wraps endpoint responses in { ok, body, timing } — unwrap body if present
		if ( json.TryGetProperty( "body", out var body ) && body.ValueKind == JsonValueKind.Object )
			return body;

		return json;
	}

	/// <summary>
	/// Extract and log error details from a server error response.
	/// Handles: { error: { code, message } }, { error: "msg" }, { message: "msg" }
	/// </summary>
	private static void LogServerError( string slug, JsonElement json )
	{
		var code = "UNKNOWN";
		var message = "";

		if ( json.TryGetProperty( "error", out var err ) )
		{
			if ( err.ValueKind == JsonValueKind.Object )
			{
				code = err.TryGetProperty( "code", out var c ) ? c.GetString() ?? "UNKNOWN" : "UNKNOWN";
				message = err.TryGetProperty( "message", out var m ) ? m.GetString() ?? "" : "";
			}
			else if ( err.ValueKind == JsonValueKind.String )
			{
				code = err.GetString() ?? "UNKNOWN";
			}
		}

		// Top-level message (server copies error.message here for convenience)
		if ( string.IsNullOrEmpty( message ) && json.TryGetProperty( "message", out var topMsg ) )
			message = topMsg.GetString() ?? "";

		Log.Warning( $"[NetworkStorage] {slug}: {code} — {message}" );
		NetLog.Error( slug, $"{code}: {message}" );
	}

	private static string TruncateJson( JsonElement el ) => TruncateJson( el.ToString() ?? "", 120 );

	private static string TruncateJson( string s, int max = 120 )
		=> s.Length > max ? s[..max] + "..." : s;
}
