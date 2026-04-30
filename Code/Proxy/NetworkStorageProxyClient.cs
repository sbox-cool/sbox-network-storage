using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	// ── Proxy Methods (non-host clients route through host) ──

	/// <summary>
	/// Route an endpoint call through the game host via the registered RequestProxy delegate.
	/// </summary>
	private static async Task<JsonElement?> CallEndpointViaProxy( string slug, object input )
	{
		var steamId = Game.SteamId.ToString();
		var securityRequest = await BuildEndpointSecurityRequest( slug, input ?? new { }, allowAuthSession: false );
		string proxySlug = "";
		string inputJson = JsonSerializer.Serialize( securityRequest.Body );

		// Get the client's auth token as consent proof (included in HMAC signature)
		string clientToken = null;
		try
		{
			clientToken = await GetAuthTokenWithRetry( $"{slug} proxy consent ({steamId})" );
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
				Log.Warning( $"[NetworkStorage] {slug} PROXY: failed to get client token — {ex.Message}" );
		}

		if ( NetworkStorageLogConfig.LogProxy )
		{
			NetLog.Request( slug, $"PROXY → host (steamId={steamId})" );
			Log.Info( $"[NetworkStorage] {slug} PROXY request via host for steamId={steamId} route={(string.IsNullOrEmpty( proxySlug ) ? "obfuscated" : proxySlug)} input={inputJson ?? "null"}" );
		}

		try
		{
			var result = await RequestProxy( steamId, clientToken ?? "", proxySlug, inputJson );
			if ( result == null )
			{
				if ( NetworkStorageLogConfig.LogErrors )
				{
					Log.Warning( $"[NetworkStorage] {slug} PROXY returned null — host may have rejected the request" );
					NetLog.Error( slug, "Proxy returned null" );
				}
				RecordEndpointError( slug, "PROXY_FAILED", "Proxy returned null" );
				return null;
			}

			if ( NetworkStorageLogConfig.LogProxy )
				Log.Info( $"[NetworkStorage] {slug} PROXY → {TruncateJson( result, 200 )}" );
			var parsed = ParseResponse( slug, result );
			if ( parsed.HasValue && NetworkStorageLogConfig.LogProxy )
				NetLog.Response( slug, $"PROXY OK — {TruncateJson( parsed.Value )}" );
			return parsed;
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] {slug} PROXY FAILED — {ex.Message}" );
				NetLog.Error( slug, $"Proxy error: {ex.Message}" );
			}
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
			if ( NetworkStorageLogConfig.LogErrors )
				Log.Warning( $"[NetworkStorage] storage PROXY: failed to get client token — {ex.Message}" );
		}

		if ( NetworkStorageLogConfig.LogProxy )
		{
			NetLog.Request( "storage", $"PROXY → host GET {collectionId}/{docId}" );
			Log.Info( $"[NetworkStorage] storage PROXY request via host for {collectionId}/{docId}" );
		}

		try
		{
			var result = await DocumentProxy( steamId, clientToken ?? "", collectionId, docId );
			if ( result == null )
			{
				if ( NetworkStorageLogConfig.LogErrors )
				{
					Log.Warning( $"[NetworkStorage] storage PROXY returned null for {collectionId}/{docId}" );
					NetLog.Error( "storage", "Proxy returned null" );
				}
				RecordEndpointError( "storage", "PROXY_FAILED", "Proxy returned null" );
				return null;
			}

			if ( NetworkStorageLogConfig.LogProxy )
				Log.Info( $"[NetworkStorage] storage PROXY → {TruncateJson( result, 200 )}" );
			var parsed = ParseResponse( "storage", result );
			if ( parsed.HasValue && NetworkStorageLogConfig.LogProxy )
				NetLog.Response( "storage", $"PROXY OK ({result.Length} bytes)" );
			return parsed;
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] storage PROXY FAILED — {ex.Message}" );
				NetLog.Error( "storage", $"Proxy error: {ex.Message}" );
			}
			RecordEndpointError( "storage", "PROXY_FAILED", ex.Message );
			return null;
		}
	}

}
