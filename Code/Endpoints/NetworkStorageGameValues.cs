using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	// ── Game Values ──

	/// <summary>
	/// Load all game values (groups + tables).
	/// </summary>
	public static async Task<JsonElement?> GetGameValues()
	{
		EnsureConfigured();
		ClearLastEndpointError( "game-values" );
		if ( !await RequireCapabilityAsync( "game-values", "game-values" ) ) return null;
		try
		{
			var url = BuildUrl( $"/values/{ProjectId}" );
			var headers = BuildPublicHeaders();
			if ( NetworkStorageLogConfig.LogRequests )
				NetLog.Request( "game-values", $"GET {ApiRoot}/values/{ProjectId}" );
			var result = await RequestStringAsync(url, "GET", null, headers);
			if ( NetworkStorageLogConfig.LogResponses )
				Log.Info( $"[NetworkStorage] game-values → {TruncateJson( result, 300 )}" );
			var parsed = ParseResponse( "game-values", result );
			if ( parsed.HasValue && NetworkStorageLogConfig.LogResponses )
				NetLog.Response( "game-values", $"OK ({result.Length} bytes)" );
			return parsed;
		}
		catch ( Exception ex )
		{
			RecordEndpointError( "game-values", ex is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
				? "RATE_LIMITED" : "REQUEST_FAILED", ex.Message );
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] GameValues: {ex.Message}" );
				NetLog.Error( "game-values", ex.Message );
			}
			return null;
		}
	}

}
