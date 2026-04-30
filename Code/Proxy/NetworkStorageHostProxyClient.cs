using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	// ── Host "On Behalf Of" Methods ──

	/// <summary>
	/// Call an endpoint on behalf of another player (host only).
	/// Requires the client's auth token as proof of consent.
	/// Sends: host auth (Facepunch-verified) + client token + HMAC proxy signature
	/// scoped to this project + endpoint to prevent cross-server replay.
	/// </summary>
	public static async Task<JsonElement?> CallEndpointAs( string targetSteamId, string clientToken, string slug, object input = null )
	{
		var endpoint = ResolveEndpointReference( slug );
		ApplyEndpointReferenceConfiguration( endpoint );
		slug = endpoint.Slug;

		EnsureConfigured();
		ClearLastEndpointError( slug );

		// Same-machine shortcut: if the target is the host's own Steam ID (e.g. two
		// editor instances on one machine share a Steam account), just call directly
		// using the host's auth — no proxy headers needed.
		if ( targetSteamId == Game.SteamId.ToString() )
		{
			if ( NetworkStorageLogConfig.LogProxy )
				Log.Info( $"[NetworkStorage] {slug} same-account shortcut (targetSteamId == hostSteamId), calling directly" );
			return await CallEndpoint( slug, input );
		}

		string url = null;
		string bodyJson = null;
		try
		{
			var routePath = $"/endpoints/{ProjectId}";
			var usesDedicatedSecret = ShouldUseDedicatedServerSecret( endpoint );
			if ( !usesDedicatedSecret && TryRejectDedicatedServerPlayerAuth( slug ) )
				return null;

			var headers = usesDedicatedSecret ? BuildPublicHeaders() : await BuildAuthHeaders();
			if ( usesDedicatedSecret )
			{
				TryAddDedicatedServerSecretHeaders( headers, endpoint );
				headers["x-on-behalf-of"] = targetSteamId;
			}
			else
			{
				headers["x-on-behalf-of"] = targetSteamId;
				headers["x-on-behalf-of-token"] = clientToken ?? "";
				headers["x-proxy-signature"] = ComputeProxySignature( ApiKey, ProjectId, slug, targetSteamId, clientToken ?? "" );
			}
			url = BuildUrl( routePath, usesDedicatedSecret );

			string result;
			if ( input is not null )
			{
				bodyJson = JsonSerializer.Serialize( input );
				if ( NetworkStorageLogConfig.LogRequests )
				{
					NetLog.Request( slug, $"POST (as {targetSteamId}) {bodyJson}" );
					Log.Info( $"[NetworkStorage] {slug} request: POST (as {targetSteamId}) {ApiRoot}{routePath} body={bodyJson}" );
				}
				var content = Http.CreateJsonContent( input );
				result = await Http.RequestStringAsync( url, "POST", content, headers );
			}
			else
			{
				if ( NetworkStorageLogConfig.LogRequests )
				{
					NetLog.Request( slug, $"GET (as {targetSteamId})" );
					Log.Info( $"[NetworkStorage] {slug} request: GET (as {targetSteamId}) {ApiRoot}/endpoints/{ProjectId}/{slug}" );
				}
				result = await Http.RequestStringAsync( url, "GET", null, headers );
			}

			if ( NetworkStorageLogConfig.LogResponses )
				Log.Info( $"[NetworkStorage] {slug} (as {targetSteamId}) → {result}" );
			var parsed = ParseResponse( slug, result );
			if ( parsed.HasValue && NetworkStorageLogConfig.LogResponses )
				NetLog.Response( slug, TruncateJson( parsed.Value ) );
			return parsed;
		}
		catch ( System.Net.Http.HttpRequestException httpEx )
		{
			var status = httpEx.StatusCode.HasValue ? $"{(int)httpEx.StatusCode.Value} {httpEx.StatusCode.Value}" : "unknown";
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] {slug} (as {targetSteamId}) FAILED — HTTP {status}" );
				Log.Warning( $"[NetworkStorage]   URL: {ApiRoot}/endpoints/{ProjectId}/{slug}" );
				Log.Warning( $"[NetworkStorage]   Method: {( input is not null ? "POST" : "GET" )}" );
				if ( bodyJson != null )
					Log.Warning( $"[NetworkStorage]   Body: {bodyJson}" );
				Log.Warning( $"[NetworkStorage]   Note: s&box Http API does not expose error response bodies — check server logs for details" );
				NetLog.Error( slug, $"HTTP {status}" );
			}
			RecordEndpointError( slug, "HTTP_ERROR", $"HTTP {status}" );
			return null;
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] {slug} (as {targetSteamId}) FAILED — {ex.Message}" );
				Log.Warning( $"[NetworkStorage]   URL: {ApiRoot}/endpoints/{ProjectId}/{slug}" );
				Log.Warning( $"[NetworkStorage]   Method: {( input is not null ? "POST" : "GET" )}" );
				if ( bodyJson != null )
					Log.Warning( $"[NetworkStorage]   Body: {bodyJson}" );
				Log.Warning( $"[NetworkStorage]   Exception: {ex}" );
				NetLog.Error( slug, ex.Message );
			}
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
			if ( NetworkStorageLogConfig.LogProxy )
				Log.Info( $"[NetworkStorage] storage/{collectionId} same-account shortcut, calling directly" );
			return await GetDocument( collectionId, documentId );
		}

		try
		{
			var docId = documentId ?? targetSteamId;
			var path = $"/storage/{EscapeRouteSegment( ProjectId )}/{EscapeRouteSegment( collectionId )}/{EscapeRouteSegment( docId )}";
			var usesDedicatedSecret = TryBuildDedicatedStorageHeaders( out var headers );
			if ( !usesDedicatedSecret && TryRejectDedicatedServerPlayerAuth( "storage" ) )
				return null;

			if ( usesDedicatedSecret )
			{
				headers["x-on-behalf-of"] = targetSteamId;
			}
			else
			{
				headers = await BuildAuthHeaders();
				var slugKey = $"storage-{collectionId}";
				headers["x-on-behalf-of"] = targetSteamId;
				headers["x-on-behalf-of-token"] = clientToken ?? "";
				headers["x-proxy-signature"] = ComputeProxySignature( ApiKey, ProjectId, slugKey, targetSteamId, clientToken ?? "" );
			}
			var url = BuildUrl( path );

			if ( NetworkStorageLogConfig.LogRequests )
				NetLog.Request( "storage", $"GET (as {targetSteamId}) {collectionId}/{docId}" );
			var result = await Http.RequestStringAsync( url, "GET", null, headers );
			if ( NetworkStorageLogConfig.LogResponses )
				Log.Info( $"[NetworkStorage] storage (as {targetSteamId}) → {TruncateJson( result, 300 )}" );
			var parsed = ParseResponse( "storage", result );
			if ( parsed.HasValue && NetworkStorageLogConfig.LogResponses )
				NetLog.Response( "storage", $"OK ({result.Length} bytes)" );
			return parsed;
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] GetDocumentAs({targetSteamId}): {ex.Message}" );
				NetLog.Error( "storage", ex.Message );
			}
			RecordEndpointError( "storage", "REQUEST_FAILED", ex.Message );
			return null;
		}
	}
}
