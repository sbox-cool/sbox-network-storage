using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	// ── Collections ──

	/// <summary>
	/// Read a document from a collection and return the stored JSON value exactly as the server sent it. Nothing is
	/// unwrapped: a top-level array, or a key named <c>ok</c>, <c>error</c> or <c>body</c>, is document data.
	/// If documentId is null, uses the current player's Steam ID. Returns null when the document is missing or the request
	/// failed; the reason is reported by <see cref="TryGetLastEndpointError"/> under "storage".
	/// </summary>
	public static async Task<JsonElement?> GetDocument( string collectionId, string documentId = null )
	{
		EnsureConfigured();
		ClearLastEndpointError( "storage" );
		if ( !await RequireCapabilityAsync( "records", "storage" ) ) return null;

		// If proxy mode is active and we're not the host, route through the host
		if ( ProxyEnabled && !IsHost && DocumentProxy != null )
		{
			return await GetDocumentViaProxy( collectionId, documentId );
		}

		var docId = documentId ?? Game.SteamId.ToString();
		try
		{
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
			var reply = await SendRawAsync( url, "GET", null, headers );
			if ( reply.Status == 404 )
			{
				RecordStorageNotFound( $"{collectionId}/{docId}" );
				return null;
			}
			if ( !reply.Ok )
			{
				RecordReplyFailure( "storage", reply, "REQUEST_FAILED" );
				return null;
			}
			if ( !TryReadRawDocument( reply, out var document, out var code, out var message ) )
			{
				RecordFailure( "storage", code, message );
				return null;
			}
			if ( NetworkStorageLogConfig.LogResponses )
			{
				Log.Info( $"[NetworkStorage] storage → {TruncateJson( reply.Body ?? "", 300 )}" );
				NetLog.Response( "storage", $"OK ({reply.Body?.Length ?? 0} bytes)" );
			}
			return document;
		}
		catch ( Exception ex )
		{
			RecordFailure( "storage", "REQUEST_FAILED", ex.Message );
			return null;
		}
	}

}
