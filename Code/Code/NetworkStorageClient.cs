using System;
using System.Collections.Generic;
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
public static partial class NetworkStorage
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

	// ── Proxy Configuration ──

	/// <summary>
	/// Whether proxy mode is enabled (host makes API calls on behalf of non-host clients).
	/// Defaults to true — safe for editor testing where only the host has a valid Steam session.
	/// Set to false in production when every player has their own Steam account.
	/// </summary>
	public static bool ProxyEnabled { get; set; } = true;

	/// <summary>Project flag for fast auth sessions. Runtime implementation is negotiated by the library/backend.</summary>
	public static bool EnableAuthSessions { get; private set; }

	/// <summary>Project flag for encrypted Network Storage requests. Runtime implementation is negotiated by the library/backend.</summary>
	public static bool EnableEncryptedRequests { get; private set; }

	/// <summary>
	/// Delegate for proxying endpoint calls through the game host.
	/// The game registers this to route requests via RPC when proxy mode is active.
	/// Parameters: (targetSteamId, clientToken, endpointSlug, inputJson) → returns response JSON string or null.
	/// </summary>
	public static Func<string, string, string, string, Task<string>> RequestProxy { get; set; }

	/// <summary>
	/// Delegate for proxying document reads through the game host.
	/// Parameters: (targetSteamId, clientToken, collectionId, documentId) → returns response JSON string or null.
	/// </summary>
	public static Func<string, string, string, string, Task<string>> DocumentProxy { get; set; }

	/// <summary>True if this client is the network host (or single-player).</summary>
	public static bool IsHost => !Networking.IsActive || Networking.IsHost;

	private static bool _autoConfigAttempted;
	private static DateTimeOffset _lastAuthTokenLookupFailedAt;
	private static readonly Dictionary<string, EndpointErrorInfo> _lastEndpointErrors = new();
	private static readonly TimeSpan FailedAuthLookupCooldown = TimeSpan.FromSeconds( 5 );
	private static readonly TimeSpan PreparedAuthTokenLifetime = TimeSpan.FromSeconds( 10 );
	private static readonly object _preparedAuthTokensLock = new();
	private static readonly Queue<PreparedAuthTokenEntry> _preparedAuthTokens = new();

	private static void InvalidateCachedAuthToken( string reason )
	{
		Log.Warning( $"[NetworkStorage] Clearing cached auth token: {reason}" );
		lock ( _preparedAuthTokensLock )
			_preparedAuthTokens.Clear();
	}

	public static bool TryGetLastEndpointError( string slug, out string code, out string message )
	{
		if ( _lastEndpointErrors.TryGetValue( slug, out var error ) )
		{
			code = error.Code;
			message = error.Message;
			return true;
		}

		code = null;
		message = null;
		return false;
	}

	public static async Task<bool> EnsureEndpointAuthAsync( string slug )
	{
		EnsureConfigured();
		ClearLastEndpointError( slug );

		var steamId = Game.SteamId.ToString();
		var token = await GetAuthTokenWithRetry( $"steamId={steamId}" );
		if ( !string.IsNullOrWhiteSpace( token ) )
		{
			RememberPreparedAuthToken( token );
			return true;
		}

		RecordEndpointError(
			slug,
			"SBOX_AUTH_FAILED",
			$"Missing token or steamId. token=NO steamId={steamId} This endpoint requires a valid s&box player token." );
		return false;
	}

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
			// Credentials file not found — try NSConfig constants as fallback.
			// The Sync Tool generates a class with hardcoded ProjectId/PublicKey,
			// which works on all clients (no filesystem dependency).
			if ( TryConfigureFromNSConfig() )
				return;

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

			// Read proxy setting from credentials — only override when explicitly present.
			// ProxyEnabled defaults to true; credentials file can explicitly set false for production.
			if ( json.TryGetProperty( "proxyEnabled", out var pe ) )
				ProxyEnabled = pe.GetBoolean();
			if ( json.TryGetProperty( "enableAuthSessions", out var eas ) )
				EnableAuthSessions = eas.ValueKind == JsonValueKind.True;
			if ( json.TryGetProperty( "enableEncryptedRequests", out var eer ) )
				EnableEncryptedRequests = eer.ValueKind == JsonValueKind.True;

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

	/// <summary>
	/// Try to configure from the auto-generated NSConfig class.
	/// This provides a fallback when the credentials file isn't available
	/// (e.g. non-host multiplayer clients, published games).
	/// NSConfig is generated by the Sync Tool with the project's public credentials.
	/// </summary>
	private static bool TryConfigureFromNSConfig()
	{
		try
		{
			var nsConfigType = TypeLibrary.GetType( "NSConfig" );
			if ( nsConfigType is null ) return false;

			var projectId = nsConfigType.GetStaticValue( "ProjectId" ) as string;
			var publicKey = nsConfigType.GetStaticValue( "PublicKey" ) as string;
			var baseUrl = nsConfigType.GetStaticValue( "BaseUrl" ) as string;
			var apiVersion = nsConfigType.GetStaticValue( "ApiVersion" ) as string;

			if ( !string.IsNullOrEmpty( projectId ) && !string.IsNullOrEmpty( publicKey ) )
			{
				// Read ProxyEnabled from the generated class — this is the authoritative
				// runtime source since JSON files are editor-only and not mounted in game.
				var proxyVal = nsConfigType.GetStaticValue( "ProxyEnabled" );
				if ( proxyVal is bool proxyBool )
					ProxyEnabled = proxyBool;
				var authSessionsVal = nsConfigType.GetStaticValue( "EnableAuthSessions" );
				if ( authSessionsVal is bool authSessionsBool )
					EnableAuthSessions = authSessionsBool;
				var encryptedRequestsVal = nsConfigType.GetStaticValue( "EnableEncryptedRequests" );
				if ( encryptedRequestsVal is bool encryptedRequestsBool )
					EnableEncryptedRequests = encryptedRequestsBool;

				Configure( projectId, publicKey, baseUrl, apiVersion );
				NetLog.Info( "config", $"Configured from NSConfig constants (ProxyEnabled={ProxyEnabled})" );
				return true;
			}
		}
		catch
		{
			// NSConfig may not exist — that's fine, it's optional
		}

		return false;
	}

	// ── Endpoints ──

	/// <summary>
	/// Call a server endpoint by slug.
	/// Returns the response body on success, null on any failure.
	/// </summary>
	public static async Task<JsonElement?> CallEndpoint( string slug, object input = null )
	{
		EnsureConfigured();
		ClearLastEndpointError( slug );

		// If proxy mode is active and we're not the host, route through the host
		if ( ProxyEnabled && !IsHost && RequestProxy != null )
		{
			return await CallEndpointViaProxy( slug, input );
		}

		if ( !IsHost )
			Log.Info( $"[NetworkStorage] {slug} direct (proxy bypass: enabled={ProxyEnabled} isHost={IsHost} hasDelegate={RequestProxy != null})" );
		string url = null;
		string bodyJson = null;
		try
		{
			url = BuildUrl( $"/endpoints/{ProjectId}/{slug}" );
			var headers = await BuildAuthHeaders();

			string result;
			if ( input is not null )
			{
				bodyJson = JsonSerializer.Serialize( input );
				NetLog.Request( slug, $"POST {bodyJson}" );
				Log.Info( $"[NetworkStorage] {slug} request: POST {ApiRoot}/endpoints/{ProjectId}/{slug} body={bodyJson}" );
				var content = Http.CreateJsonContent( input );
				result = await Http.RequestStringAsync( url, "POST", content, headers );
			}
			else
			{
				NetLog.Request( slug, "GET" );
				Log.Info( $"[NetworkStorage] {slug} request: GET {ApiRoot}/endpoints/{ProjectId}/{slug}" );
				result = await Http.RequestStringAsync( url, "GET", null, headers );
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
			RecordEndpointError( slug, "HTTP_ERROR", $"HTTP {status}" );
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
			RecordEndpointError( slug, "REQUEST_FAILED", ex.Message );
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
			var url = BuildUrl( $"/values/{ProjectId}" );
			NetLog.Request( "game-values", $"GET {ApiRoot}/values/{ProjectId}" );
			var result = await Http.RequestStringAsync( url, "GET", null, null );
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
		ClearLastEndpointError( "storage" );

		// If proxy mode is active and we're not the host, route through the host
		if ( ProxyEnabled && !IsHost && DocumentProxy != null )
		{
			return await GetDocumentViaProxy( collectionId, documentId );
		}
		try
		{
			var docId = documentId ?? Game.SteamId.ToString();
			var url = BuildUrl( $"/storage/{ProjectId}/{collectionId}/{docId}" );
			var headers = await BuildAuthHeaders();

			NetLog.Request( "storage", $"GET {collectionId}/{docId}" );
			var result = await Http.RequestStringAsync( url, "GET", null, headers );
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
			RecordEndpointError( "storage", "REQUEST_FAILED", ex.Message );
			return null;
		}
	}

	// ── Proxy Methods (non-host clients route through host) ──

	/// <summary>
	/// Route an endpoint call through the game host via the registered RequestProxy delegate.
	/// </summary>
	private static async Task<JsonElement?> CallEndpointViaProxy( string slug, object input )
	{
		var steamId = Game.SteamId.ToString();
		string inputJson = input is not null ? JsonSerializer.Serialize( input ) : null;

		// Get the client's auth token as consent proof (included in HMAC signature)
		string clientToken = null;
		try
		{
			clientToken = await GetAuthTokenWithRetry( $"{slug} proxy consent ({steamId})" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] {slug} PROXY: failed to get client token — {ex.Message}" );
		}

		NetLog.Request( slug, $"PROXY → host (steamId={steamId})" );
		Log.Info( $"[NetworkStorage] {slug} PROXY request via host for steamId={steamId} input={inputJson ?? "null"}" );

		try
		{
			var result = await RequestProxy( steamId, clientToken ?? "", slug, inputJson );
			if ( result == null )
			{
				Log.Warning( $"[NetworkStorage] {slug} PROXY returned null — host may have rejected the request" );
				NetLog.Error( slug, "Proxy returned null" );
				RecordEndpointError( slug, "PROXY_FAILED", "Proxy returned null" );
				return null;
			}

			Log.Info( $"[NetworkStorage] {slug} PROXY → {TruncateJson( result, 200 )}" );
			var parsed = ParseResponse( slug, result );
			if ( parsed.HasValue )
				NetLog.Response( slug, $"PROXY OK — {TruncateJson( parsed.Value )}" );
			return parsed;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] {slug} PROXY FAILED — {ex.Message}" );
			NetLog.Error( slug, $"Proxy error: {ex.Message}" );
			RecordEndpointError( slug, "PROXY_FAILED", ex.Message );
			return null;
		}
	}

	/// <summary>
	/// Route a document read through the game host via the registered DocumentProxy delegate.
	/// </summary>
	private static async Task<JsonElement?> GetDocumentViaProxy( string collectionId, string documentId )
	{
		var steamId = Game.SteamId.ToString();
		var docId = documentId ?? steamId;

		// Get the client's auth token as consent proof (included in HMAC signature)
		string clientToken = null;
		try
		{
			clientToken = await GetAuthTokenWithRetry( $"storage proxy consent ({collectionId}/{docId})" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] storage PROXY: failed to get client token — {ex.Message}" );
		}

		NetLog.Request( "storage", $"PROXY → host GET {collectionId}/{docId}" );
		Log.Info( $"[NetworkStorage] storage PROXY request via host for {collectionId}/{docId}" );

		try
		{
			var result = await DocumentProxy( steamId, clientToken ?? "", collectionId, docId );
			if ( result == null )
			{
				Log.Warning( $"[NetworkStorage] storage PROXY returned null for {collectionId}/{docId}" );
				NetLog.Error( "storage", "Proxy returned null" );
				RecordEndpointError( "storage", "PROXY_FAILED", "Proxy returned null" );
				return null;
			}

			Log.Info( $"[NetworkStorage] storage PROXY → {TruncateJson( result, 200 )}" );
			var parsed = ParseResponse( "storage", result );
			if ( parsed.HasValue )
				NetLog.Response( "storage", $"PROXY OK ({result.Length} bytes)" );
			return parsed;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] storage PROXY FAILED — {ex.Message}" );
			NetLog.Error( "storage", $"Proxy error: {ex.Message}" );
			RecordEndpointError( "storage", "PROXY_FAILED", ex.Message );
			return null;
		}
	}

	// ── Host "On Behalf Of" Methods ──

	/// <summary>
	/// Call an endpoint on behalf of another player (host only).
	/// Requires the client's auth token as proof of consent.
	/// Sends: host auth (Facepunch-verified) + client token + HMAC proxy signature
	/// scoped to this project + endpoint to prevent cross-server replay.
	/// </summary>
	public static async Task<JsonElement?> CallEndpointAs( string targetSteamId, string clientToken, string slug, object input = null )
	{
		EnsureConfigured();
		ClearLastEndpointError( slug );

		// Same-machine shortcut: if the target is the host's own Steam ID (e.g. two
		// editor instances on one machine share a Steam account), just call directly
		// using the host's auth — no proxy headers needed.
		if ( targetSteamId == Game.SteamId.ToString() )
		{
			Log.Info( $"[NetworkStorage] {slug} same-account shortcut (targetSteamId == hostSteamId), calling directly" );
			return await CallEndpoint( slug, input );
		}

		string url = null;
		string bodyJson = null;
		try
		{
			url = BuildUrl( $"/endpoints/{ProjectId}/{slug}" );
			var headers = await BuildAuthHeaders();

			// Proxy headers: client identity + token + scoped signature
			headers["x-on-behalf-of"] = targetSteamId;
			headers["x-on-behalf-of-token"] = clientToken ?? "";
			headers["x-proxy-signature"] = ComputeProxySignature( ApiKey, ProjectId, slug, targetSteamId, clientToken ?? "" );

			string result;
			if ( input is not null )
			{
				bodyJson = JsonSerializer.Serialize( input );
				NetLog.Request( slug, $"POST (as {targetSteamId}) {bodyJson}" );
				Log.Info( $"[NetworkStorage] {slug} request: POST (as {targetSteamId}) {ApiRoot}/endpoints/{ProjectId}/{slug} body={bodyJson}" );
				var content = Http.CreateJsonContent( input );
				result = await Http.RequestStringAsync( url, "POST", content, headers );
			}
			else
			{
				NetLog.Request( slug, $"GET (as {targetSteamId})" );
				Log.Info( $"[NetworkStorage] {slug} request: GET (as {targetSteamId}) {ApiRoot}/endpoints/{ProjectId}/{slug}" );
				result = await Http.RequestStringAsync( url, "GET", null, headers );
			}

			Log.Info( $"[NetworkStorage] {slug} (as {targetSteamId}) → {result}" );
			var parsed = ParseResponse( slug, result );
			if ( parsed.HasValue )
				NetLog.Response( slug, TruncateJson( parsed.Value ) );
			return parsed;
		}
		catch ( System.Net.Http.HttpRequestException httpEx )
		{
			var status = httpEx.StatusCode.HasValue ? $"{(int)httpEx.StatusCode.Value} {httpEx.StatusCode.Value}" : "unknown";
			Log.Warning( $"[NetworkStorage] {slug} (as {targetSteamId}) FAILED — HTTP {status}" );
			Log.Warning( $"[NetworkStorage]   URL: {ApiRoot}/endpoints/{ProjectId}/{slug}" );
			Log.Warning( $"[NetworkStorage]   Method: {( input is not null ? "POST" : "GET" )}" );
			if ( bodyJson != null )
				Log.Warning( $"[NetworkStorage]   Body: {bodyJson}" );
			Log.Warning( $"[NetworkStorage]   Note: s&box Http API does not expose error response bodies — check server logs for details" );
			NetLog.Error( slug, $"HTTP {status}" );
			RecordEndpointError( slug, "HTTP_ERROR", $"HTTP {status}" );
			return null;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] {slug} (as {targetSteamId}) FAILED — {ex.Message}" );
			Log.Warning( $"[NetworkStorage]   URL: {ApiRoot}/endpoints/{ProjectId}/{slug}" );
			Log.Warning( $"[NetworkStorage]   Method: {( input is not null ? "POST" : "GET" )}" );
			if ( bodyJson != null )
				Log.Warning( $"[NetworkStorage]   Body: {bodyJson}" );
			Log.Warning( $"[NetworkStorage]   Exception: {ex}" );
			NetLog.Error( slug, ex.Message );
			RecordEndpointError( slug, "REQUEST_FAILED", ex.Message );
			return null;
		}
	}

	/// <summary>
	/// Read a document on behalf of another player (host only).
	/// Requires the client's auth token as proof of consent.
	/// </summary>
	public static async Task<JsonElement?> GetDocumentAs( string targetSteamId, string clientToken, string collectionId, string documentId = null )
	{
		EnsureConfigured();
		ClearLastEndpointError( "storage" );

		// Same-machine shortcut: same Steam account as host, call directly
		if ( targetSteamId == Game.SteamId.ToString() )
		{
			Log.Info( $"[NetworkStorage] storage/{collectionId} same-account shortcut, calling directly" );
			return await GetDocument( collectionId, documentId );
		}

		try
		{
			var docId = documentId ?? targetSteamId;
			var url = BuildUrl( $"/storage/{ProjectId}/{collectionId}/{docId}" );
			var headers = await BuildAuthHeaders();

			var slugKey = $"storage-{collectionId}";
			headers["x-on-behalf-of"] = targetSteamId;
			headers["x-on-behalf-of-token"] = clientToken ?? "";
			headers["x-proxy-signature"] = ComputeProxySignature( ApiKey, ProjectId, slugKey, targetSteamId, clientToken ?? "" );

			NetLog.Request( "storage", $"GET (as {targetSteamId}) {collectionId}/{docId}" );
			var result = await Http.RequestStringAsync( url, "GET", null, headers );
			Log.Info( $"[NetworkStorage] storage (as {targetSteamId}) → {TruncateJson( result, 300 )}" );
			var parsed = ParseResponse( "storage", result );
			if ( parsed.HasValue )
				NetLog.Response( "storage", $"OK ({result.Length} bytes)" );
			return parsed;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] GetDocumentAs({targetSteamId}): {ex.Message}" );
			NetLog.Error( "storage", ex.Message );
			RecordEndpointError( "storage", "REQUEST_FAILED", ex.Message );
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

	/// <summary>
	/// Build the request URL with API key query param (no auth tokens in URL).
	/// </summary>
	private static string BuildUrl( string path )
	{
		var baseUrl = $"{ApiRoot}{path}?apiKey={Uri.EscapeDataString( ApiKey )}";
		if ( IsCdnRoot( ApiRoot ) )
		{
			var v = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			return $"{baseUrl}&v={v}";
		}
		return baseUrl;
	}

	/// <summary>
	/// Build auth headers containing the Steam ID and auth token.
	/// Sending auth via headers avoids URL-encoding issues that corrupt
	/// base64 tokens in query strings (+ decoded as space, etc.).
	/// </summary>
	private static async Task<Dictionary<string, string>> BuildAuthHeaders()
	{
		var steamId = Game.SteamId.ToString();
		var token = TryTakePreparedAuthToken() ?? await GetAuthTokenWithRetry( $"steamId={steamId}" );

		if ( string.IsNullOrEmpty( token ) )
		{
			Log.Warning( $"[NetworkStorage] Auth token is empty for steamId={steamId} — requests may fail" );
		}
		else
		{
			var preview = token.Length > 8 ? $"{token[..4]}...{token[^4..]}" : "****";
			Log.Info( $"[NetworkStorage] Auth token acquired for steamId={steamId} ({token.Length} chars, preview={preview})" );
		}

		return new Dictionary<string, string>
		{
			{ "x-public-key", ApiKey ?? "" },
			{ "x-steam-id", steamId },
			{ "x-sbox-token", token ?? "" }
		};
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
			RecordEndpointError( slug, "EMPTY_RESPONSE", "Server returned empty response" );
			return null;
		}

		// Catch HTML error pages or non-JSON responses early
		var trimmed = raw.TrimStart();
		if ( trimmed.Length > 0 && trimmed[0] != '{' && trimmed[0] != '[' )
		{
			NetLog.Error( slug, $"Non-JSON response: {raw[..Math.Min( raw.Length, 120 )]}" );
			RecordEndpointError( slug, "INVALID_RESPONSE", "Server returned non-JSON response" );
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
			RecordEndpointError( slug, "INVALID_JSON", "Server returned invalid JSON" );
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
				RecordEndpointError( slug, errProp.GetString() ?? "UNKNOWN", "" );
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
		var authFailureKind = "";

		if ( json.TryGetProperty( "error", out var err ) )
		{
			if ( err.ValueKind == JsonValueKind.Object )
			{
				code = err.TryGetProperty( "code", out var c ) ? c.GetString() ?? "UNKNOWN" : "UNKNOWN";
				message = err.TryGetProperty( "message", out var m ) ? m.GetString() ?? "" : "";
				authFailureKind = err.TryGetProperty( "authFailureKind", out var authKind )
					? authKind.GetString() ?? ""
					: "";
			}
			else if ( err.ValueKind == JsonValueKind.String )
			{
				code = err.GetString() ?? "UNKNOWN";
			}
		}

		// Top-level message (server copies error.message here for convenience)
		if ( string.IsNullOrEmpty( message ) && json.TryGetProperty( "message", out var topMsg ) )
			message = topMsg.GetString() ?? "";

		if ( string.Equals( code, "SBOX_AUTH_FAILED", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( authFailureKind, "token_rejected", StringComparison.OrdinalIgnoreCase ) )
		{
			InvalidateCachedAuthToken( $"{slug} {code} {authFailureKind}".Trim() );
		}

		Log.Warning( $"[NetworkStorage] {slug}: {code} — {message}" );
		NetLog.Error( slug, $"{code}: {message}" );
		RecordEndpointError( slug, code, message );
	}

	private static void ClearLastEndpointError( string slug )
	{
		if ( !string.IsNullOrEmpty( slug ) )
			_lastEndpointErrors.Remove( slug );
	}

	private static void RecordEndpointError( string slug, string code, string message )
	{
		if ( string.IsNullOrEmpty( slug ) )
			return;

		_lastEndpointErrors[slug] = new EndpointErrorInfo
		{
			Code = string.IsNullOrWhiteSpace( code ) ? "UNKNOWN" : code,
			Message = message ?? ""
		};
	}

	private sealed class EndpointErrorInfo
	{
		public string Code { get; init; }
		public string Message { get; init; }
	}

	private static string TruncateJson( JsonElement el ) => TruncateJson( el.ToString() ?? "", 120 );

	private static string TruncateJson( string s, int max = 120 )
		=> s.Length > max ? s[..max] + "..." : s;
}
