using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Network Storage client for sboxcool.com.
/// Call Configure() once at startup, then use the static methods to interact with your project.
///
/// Example:
///   NetworkStorage.Configure( "your-project-id", "sbox_ns_your_key" );
///   var player = await NetworkStorage.CallEndpoint( "load-player" );
///   var values = await NetworkStorage.GetGameValues();
///   var doc = await NetworkStorage.GetDocument( "players", steamId );
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

	/// <summary>True after Configure() has been called with valid credentials.</summary>
	public static bool IsConfigured => !string.IsNullOrEmpty( ProjectId ) && !string.IsNullOrEmpty( ApiKey );

	/// <summary>The full versioned API root, e.g. https://api.sboxcool.com/v3</summary>
	public static string ApiRoot => $"{BaseUrl}/{ApiVersion}";

	/// <summary>
	/// Configure the client. Call once at game startup.
	/// </summary>
	public static void Configure( string projectId, string apiKey, string baseUrl = null, string apiVersion = null )
	{
		ProjectId = projectId;
		ApiKey = apiKey;
		if ( !string.IsNullOrEmpty( baseUrl ) ) BaseUrl = baseUrl.TrimEnd( '/' );
		if ( !string.IsNullOrEmpty( apiVersion ) ) ApiVersion = apiVersion.Trim( '/' );
		NetLog.Info( "config", $"NetworkStorage ready — {ApiRoot}" );
	}

	// ── Endpoints ──

	/// <summary>
	/// Call a server endpoint by slug.
	/// URL: {ApiRoot}/endpoints/{ProjectId}/{slug}
	/// </summary>
	public static async Task<JsonElement?> CallEndpoint( string slug, object input = null )
	{
		EnsureConfigured();
		try
		{
			var url = await BuildUrl( $"/endpoints/{ProjectId}/{slug}" );
			NetLog.Info( slug, $"{ApiRoot}/endpoints/{ProjectId}/{slug}" );

			string result;
			if ( input is not null )
			{
				var body = JsonSerializer.Serialize( input );
				NetLog.Request( slug, $"POST {body}" );
				var content = new StringContent( body, Encoding.UTF8, "application/json" );
				result = await Http.RequestStringAsync( url, "POST", content );
			}
			else
			{
				NetLog.Request( slug, "GET" );
				result = await Http.RequestStringAsync( url );
			}

			var parsed = ParseResponse( slug, result );
			if ( parsed.HasValue )
				NetLog.Response( slug, TruncateJson( parsed.Value ) );
			return parsed;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] {slug}: {ex.Message}" );
			NetLog.Error( slug, ex.Message );
			return null;
		}
	}

	// ── Game Values ──

	/// <summary>
	/// Load all game values (groups + tables).
	/// URL: {ApiRoot}/values/{ProjectId}
	/// </summary>
	public static async Task<JsonElement?> GetGameValues()
	{
		EnsureConfigured();
		try
		{
			var url = await BuildUrl( $"/values/{ProjectId}" );
			NetLog.Request( "game-values", $"GET {ApiRoot}/values/{ProjectId}" );
			var result = await Http.RequestStringAsync( url );

			if ( string.IsNullOrEmpty( result ) )
			{
				NetLog.Error( "game-values", "Empty response" );
				return null;
			}

			var json = JsonSerializer.Deserialize<JsonElement>( result );
			NetLog.Response( "game-values", $"OK ({result.Length} bytes)" );
			return json;
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
	/// URL: {ApiRoot}/storage/{ProjectId}/{collectionId}/{documentId}
	/// If documentId is null, uses the current player's Steam ID.
	/// </summary>
	public static async Task<JsonElement?> GetDocument( string collectionId, string documentId = null )
	{
		EnsureConfigured();
		try
		{
			var docId = documentId ?? Game.SteamId.ToString();
			var v = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			var auth = await BuildAuthQuery();
			var url = $"{ApiRoot}/storage/{ProjectId}/{collectionId}/{docId}{auth}&v={v}";

			NetLog.Request( "storage", $"GET {collectionId}/{docId}" );
			var result = await Http.RequestStringAsync( url );

			if ( string.IsNullOrEmpty( result ) )
			{
				NetLog.Error( "storage", "Empty response" );
				return null;
			}

			NetLog.Response( "storage", $"OK ({result.Length} bytes)" );
			return JsonSerializer.Deserialize<JsonElement>( result );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] GetDocument: {ex.Message}" );
			NetLog.Error( "storage", ex.Message );
			return null;
		}
	}

	// ── Internals ──

	private static void EnsureConfigured()
	{
		if ( !IsConfigured )
			throw new InvalidOperationException( "NetworkStorage.Configure() must be called before use." );
	}

	private static async Task<string> BuildUrl( string path )
	{
		var auth = await BuildAuthQuery();
		return $"{ApiRoot}{path}{auth}";
	}

	private static async Task<string> BuildAuthQuery()
	{
		var steamId = Game.SteamId.ToString();
		var token = await Services.Auth.GetToken( "sbox-network-storage" );
		return $"?apiKey={ApiKey}&steamId={steamId}&token={token}";
	}

	private static JsonElement? ParseResponse( string slug, string raw )
	{
		if ( string.IsNullOrEmpty( raw ) )
		{
			NetLog.Error( slug, "Server returned empty response" );
			return null;
		}

		JsonElement json;
		try { json = JsonSerializer.Deserialize<JsonElement>( raw ); }
		catch ( Exception ex )
		{
			NetLog.Error( slug, $"Invalid JSON: {raw[..Math.Min( raw.Length, 200 )]}" );
			return null;
		}

		if ( json.TryGetProperty( "ok", out var ok ) && ok.ValueKind == JsonValueKind.False )
		{
			var err = json.TryGetProperty( "error", out var e ) ? e.GetString() : "UNKNOWN";
			var msg = json.TryGetProperty( "message", out var m ) ? m.GetString() : "";
			Log.Warning( $"[NetworkStorage] {slug}: {err} — {msg}" );
			NetLog.Error( slug, $"{err}: {msg}" );
			return null;
		}

		if ( json.TryGetProperty( "body", out var body ) )
			return body;

		return json;
	}

	private static string TruncateJson( JsonElement el )
	{
		var s = el.ToString() ?? "";
		return s.Length > 120 ? s[..120] + "..." : s;
	}
}
