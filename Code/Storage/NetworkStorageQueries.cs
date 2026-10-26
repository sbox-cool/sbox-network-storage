using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	/// <summary>
	/// Run a dashboard query by ID. Public queries use the public runtime key.
	/// On dedicated servers, a configured secret key is sent automatically so
	/// secret-gated queries can run without s&amp;box auth tokens.
	/// </summary>
	public static Task<JsonElement?> RunQuery( string queryId )
		=> GetQuery( queryId );

	/// <summary>Alias for RunQuery.</summary>
	public static async Task<JsonElement?> GetQuery( string queryId )
	{
		EnsureConfigured();
		ClearLastEndpointError( "query" );

		if ( string.IsNullOrWhiteSpace( queryId ) )
		{
			RecordEndpointErrorUnderCode( "query", "INVALID_QUERY", "queryId is required." );
			return null;
		}

		var tag = $"query:{queryId}";
		if ( !await RequireCapabilityAsync( "queries", tag ) ) return null;
		var path = $"/queries/{EscapeRouteSegment( ProjectId )}/{EscapeRouteSegment( queryId )}";
		string url = null;

		try
		{
			var usesDedicatedSecret = TryGetDedicatedServerSecretKey( null, out var secretKey ) && CanTransportDedicatedServerSecretSecurely();
			var headers = BuildPublicHeaders();
			if ( usesDedicatedSecret )
			{
				headers["x-api-key"] = secretKey;
				headers["x-public-key"] = ApiKey ?? "";
				LogDedicatedSecretTransportOnce( "x-api-key" );
			}
			else if ( HasDedicatedServerSecretKey )
			{
				LogInsecureSecretTransportOnce();
			}

			url = BuildUrl( path, usesDedicatedSecret );
			if ( NetworkStorageLogConfig.LogRequests )
			{
				var suffix = usesDedicatedSecret ? " secret-key=1" : "";
				NetLog.Request( tag, $"GET{suffix} {path}" );
				Log.Info( $"[NetworkStorage] {tag} request: GET {ApiRoot}{path}{suffix}" );
			}

			var reply = await SendRawAsync( url, "GET", null, headers );
			if ( NetworkStorageLogConfig.LogResponses )
				Log.Info( $"[NetworkStorage] {tag} → HTTP {reply.Status} {TruncateJson( reply.Body ?? reply.TransportError ?? "", 300 )}" );
			if ( !reply.Ok )
			{
				RecordReplyFailure( tag, reply, "REQUEST_FAILED" );
				return null;
			}

			var parsed = ParseResponse( tag, reply.Body );
			if ( parsed.HasValue && NetworkStorageLogConfig.LogResponses )
				NetLog.Response( tag, TruncateJson( parsed.Value ) );
			return parsed;
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] {tag} FAILED — {ex.Message}" );
				Log.Warning( $"[NetworkStorage]   URL: {url ?? $"{ApiRoot}{path}"}" );
				NetLog.Error( tag, ex.Message );
			}
			RecordEndpointError( tag, "REQUEST_FAILED", ex.Message );
			return null;
		}
	}
}
