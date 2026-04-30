using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
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
			var path = $"/storage/{EscapeRouteSegment( ProjectId )}/{EscapeRouteSegment( collectionId )}/{EscapeRouteSegment( docId )}";
			var usesDedicatedSecret = TryBuildDedicatedStorageHeaders( out var headers );
			if ( !usesDedicatedSecret )
			{
				if ( TryRejectDedicatedServerPlayerAuth( "storage" ) )
					return null;
				headers = await BuildAuthHeaders();
			}
			var url = BuildUrl( path );

			if ( NetworkStorageLogConfig.LogRequests )
				NetLog.Request( "storage", $"GET {collectionId}/{docId}" );
			var result = await Http.RequestStringAsync( url, "GET", null, headers );
			if ( NetworkStorageLogConfig.LogResponses )
				Log.Info( $"[NetworkStorage] storage → {TruncateJson( result, 300 )}" );
			var parsed = ParseResponse( "storage", result );
			if ( parsed.HasValue && NetworkStorageLogConfig.LogResponses )
				NetLog.Response( "storage", $"OK ({result.Length} bytes)" );
			return parsed;
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] GetDocument: {ex.Message}" );
				NetLog.Error( "storage", ex.Message );
			}
			RecordEndpointError( "storage", "REQUEST_FAILED", ex.Message );
			return null;
		}
	}

}
