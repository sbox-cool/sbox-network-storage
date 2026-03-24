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
	private static readonly HttpClient _http = new();

	/// <summary>
	/// Make an authenticated request to the management API.
	/// </summary>
	public static async Task<JsonElement?> Request( string method, string path, JsonElement? body = null )
	{
		if ( !SyncToolConfig.IsValid )
		{
			Log.Warning( "[SyncTool] Config not valid — load .env first" );
			return null;
		}

		var url = $"{SyncToolConfig.BaseUrl}/{SyncToolConfig.ApiVersion}/manage/{SyncToolConfig.ProjectId}/{path}";

		var request = new HttpRequestMessage( new HttpMethod( method ), url );
		request.Headers.Add( "x-api-key", SyncToolConfig.SecretKey );
		request.Headers.Add( "User-Agent", "SyncTool-sbox/1.0" );

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
					var msg = errJson.TryGetProperty( "message", out var m ) ? m.GetString() : text;
					Log.Warning( $"[SyncTool] → {msg}" );
				}
				catch
				{
					Log.Warning( $"[SyncTool] → {text[..Math.Min( text.Length, 200 )]}" );
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

	/// <summary>
	/// Validate credentials against the server.
	/// Sends secret key via x-api-key header and optionally public key via x-public-key header.
	/// Returns { ok, project, checks: { projectId, secretKey, publicKey } }
	/// </summary>
	public static async Task<JsonElement?> Validate( string publicKey = null )
	{
		if ( !SyncToolConfig.IsValid ) return null;

		var url = $"{SyncToolConfig.BaseUrl}/{SyncToolConfig.ApiVersion}/manage/{SyncToolConfig.ProjectId}/validate";
		var request = new HttpRequestMessage( HttpMethod.Get, url );
		request.Headers.Add( "x-api-key", SyncToolConfig.SecretKey );
		request.Headers.Add( "User-Agent", "SyncTool-sbox/1.0" );

		if ( !string.IsNullOrEmpty( publicKey ) )
			request.Headers.Add( "x-public-key", publicKey );

		try
		{
			var response = await _http.SendAsync( request );
			var text = await response.Content.ReadAsStringAsync();
			return JsonSerializer.Deserialize<JsonElement>( text );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Validate: {ex.Message}" );
			return null;
		}
	}
}
