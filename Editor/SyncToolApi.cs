using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

/// <summary>
/// HTTP client for the Network Storage v3 management API.
/// Used by the SyncTool editor window to push/pull game data.
/// All requests are authenticated with the secret key from .env.
/// </summary>
public static class SyncToolApi
{
	private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds( 30 ) };

	/// <summary>
	/// Last error code from the server (e.g., "KEY_UPGRADE_REQUIRED", "FORBIDDEN").
	/// Reset on each request. Null if the last request succeeded or had no structured error.
	/// </summary>
	public static string LastErrorCode { get; private set; }

	/// <summary>
	/// Last error message from the server.
	/// </summary>
	public static string LastErrorMessage { get; private set; }

	/// <summary>
	/// Make an authenticated request to the management API.
	/// </summary>
	public static async Task<JsonElement?> Request( string method, string path, JsonElement? body = null )
	{
		LastErrorCode = null;
		LastErrorMessage = null;

		if ( !SyncToolConfig.IsValid )
		{
			Log.Warning( "[SyncTool] Config not valid -- load .env first" );
			return null;
		}

		var url = $"{SyncToolConfig.BaseUrl}/{SyncToolConfig.ApiVersion}/manage/{SyncToolConfig.ProjectId}/{path}";

		var sk = SyncToolConfig.SecretKey ?? "";
		var pk = SyncToolConfig.PublicApiKey ?? "";
		Log.Info( $"[SyncTool] {method} {url}" );
		Log.Info( $"[SyncTool]   x-api-key: {( sk.Length > 20 ? sk[..16] + "..." + sk[^4..] : sk.Length > 0 ? "(too short: " + sk.Length + " chars)" : "(empty)" )} ({sk.Length} chars)" );
		Log.Info( $"[SyncTool]   x-public-key: {( pk.Length > 20 ? pk[..16] + "..." + pk[^4..] : pk.Length > 0 ? pk : "(empty)" )} ({pk.Length} chars)" );

		var request = new HttpRequestMessage( new HttpMethod( method ), url );
		request.Headers.Add( "x-api-key", sk );
		request.Headers.Add( "x-public-key", pk );
		request.Headers.Add( "User-Agent", "SyncTool-sbox/2.0" );

		if ( body.HasValue )
		{
			var json = JsonSerializer.Serialize( body.Value );
			request.Content = new StringContent( json, Encoding.UTF8, "application/json" );
		}

		try
		{
			var response = await _http.SendAsync( request );
			var text = await response.Content.ReadAsStringAsync();

			if ( !response.IsSuccessStatusCode )
			{
				Log.Warning( $"[SyncTool] {method} {path}: HTTP {(int)response.StatusCode}" );
				try
				{
					var errJson = JsonSerializer.Deserialize<JsonElement>( text );
					if ( errJson.TryGetProperty( "error", out var errCode ) )
						LastErrorCode = errCode.GetString();
					if ( errJson.TryGetProperty( "message", out var errMsg ) )
						LastErrorMessage = errMsg.GetString();

					var msg = LastErrorMessage ?? text;
					Log.Warning( $"[SyncTool] -> {msg}" );

					// Show what the server received vs what we sent
					if ( errJson.TryGetProperty( "received", out var received ) )
						Log.Warning( $"[SyncTool] Server received: {received}" );

					// Surface specific error types
					if ( LastErrorCode == "KEY_UPGRADE_REQUIRED" )
					{
						Log.Warning( "[SyncTool] Your secret key uses an old format. Generate a new one at sbox.cool." );
					}
					else if ( LastErrorCode == "FORBIDDEN" )
					{
						Log.Warning( $"[SyncTool] Permission denied: {LastErrorMessage}" );
					}
				}
				catch
				{
					Log.Warning( $"[SyncTool] -> {text[..Math.Min( text.Length, 200 )]}" );
				}
				return null;
			}

			return JsonSerializer.Deserialize<JsonElement>( text );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] {method} {path}: {ex.Message}" );
			return null;
		}
	}

	/// <summary>Fetch current server endpoints.</summary>
	public static Task<JsonElement?> GetEndpoints() => Request( "GET", "endpoints" );

	/// <summary>Push endpoints to server.</summary>
	public static Task<JsonElement?> PushEndpoints( JsonElement data ) => Request( "PUT", "endpoints", data );

	/// <summary>Fetch current server collections.</summary>
	public static Task<JsonElement?> GetCollections() => Request( "GET", "collections" );

	/// <summary>Push collection schemas to server.</summary>
	public static Task<JsonElement?> PushCollections( JsonElement data ) => Request( "PUT", "collections", data );

	/// <summary>Fetch current server workflows.</summary>
	public static Task<JsonElement?> GetWorkflows() => Request( "GET", "workflows" );

	/// <summary>Push workflows to server.</summary>
	public static Task<JsonElement?> PushWorkflows( JsonElement data ) => Request( "PUT", "workflows", data );

	/// <summary>Fetch current server tests.</summary>
	public static Task<JsonElement?> GetTests() => Request( "GET", "tests" );

	/// <summary>Push tests to server.</summary>
	public static Task<JsonElement?> PushTests( JsonElement data ) => Request( "PUT", "tests", data );

	/// <summary>Run a single test via dry-run.</summary>
	public static Task<JsonElement?> RunTest( JsonElement data ) => Request( "POST", "test-endpoint", data );

	/// <summary>Run all tests via dry-run.</summary>
	public static Task<JsonElement?> RunAllTests( JsonElement data ) => Request( "POST", "run-tests", data );

	/// <summary>Suggest tests for an endpoint.</summary>
	public static Task<JsonElement?> SuggestTests( JsonElement data ) => Request( "POST", "suggest-tests", data );

	/// <summary>
	/// Validate credentials against the server.
	/// Sends secret key via x-api-key header and optionally public key via x-public-key header.
	/// Returns { ok, project, checks, permissions? }
	/// </summary>
	public static async Task<JsonElement?> Validate( string publicKey = null )
	{
		LastErrorCode = null;
		LastErrorMessage = null;

		if ( !SyncToolConfig.IsValid )
		{
			Log.Warning( "[SyncTool] Validate: config not valid" );
			return null;
		}

		var url = $"{SyncToolConfig.BaseUrl}/{SyncToolConfig.ApiVersion}/manage/{SyncToolConfig.ProjectId}/validate";
		var sk = SyncToolConfig.SecretKey ?? "";
		var pk = publicKey ?? "";

		Log.Info( $"[SyncTool] Validate: GET {url}" );
		Log.Info( $"[SyncTool]   x-api-key: {( sk.Length > 20 ? sk[..16] + "..." + sk[^4..] : sk.Length > 0 ? "(short: " + sk.Length + " chars)" : "(empty)" )}" );
		Log.Info( $"[SyncTool]   x-public-key: {( pk.Length > 0 ? pk : "(not sent)" )}" );

		var request = new HttpRequestMessage( HttpMethod.Get, url );
		request.Headers.Add( "x-api-key", sk );
		request.Headers.Add( "User-Agent", "SyncTool-sbox/2.0" );

		if ( !string.IsNullOrEmpty( publicKey ) )
			request.Headers.Add( "x-public-key", publicKey );

		try
		{
			var response = await _http.SendAsync( request );
			var text = await response.Content.ReadAsStringAsync();

			Log.Info( $"[SyncTool] Validate: HTTP {(int)response.StatusCode} — {text[..Math.Min( text.Length, 500 )]}" );

			if ( !response.IsSuccessStatusCode )
			{
				LastErrorCode = $"HTTP_{(int)response.StatusCode}";
				LastErrorMessage = $"Server returned HTTP {(int)response.StatusCode}. Response: {text[..Math.Min( text.Length, 300 )]}";
				Log.Warning( $"[SyncTool] Validate failed: HTTP {(int)response.StatusCode}" );
				Log.Warning( $"[SyncTool]   Response: {text[..Math.Min( text.Length, 500 )]}" );
				return null;
			}

			var result = JsonSerializer.Deserialize<JsonElement>( text );

			// Check for error codes in the response
			if ( result.TryGetProperty( "error", out var errCode ) )
			{
				LastErrorCode = errCode.GetString();
				if ( result.TryGetProperty( "message", out var errMsg ) )
					LastErrorMessage = errMsg.GetString();
				Log.Warning( $"[SyncTool] Validate error: {LastErrorCode} — {LastErrorMessage}" );
			}

			return result;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Validate: {ex.Message}" );
			return null;
		}
	}
}
