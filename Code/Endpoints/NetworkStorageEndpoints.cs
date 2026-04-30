using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	// ── Endpoints ──

	/// <summary>
	/// Call a server endpoint by slug or dashboard endpoint URL.
	/// Returns the response body on success, null on any failure.
	/// </summary>
	public static Task<JsonElement?> CallEndpoint( string slug, object input = null )
		=> CallEndpointInternal( slug, input, allowSecurityRetry: true );

	private static async Task<JsonElement?> CallEndpointInternal( string slug, object input, bool allowSecurityRetry )
	{
		var endpoint = ResolveEndpointReference( slug );
		ApplyEndpointReferenceConfiguration( endpoint );
		slug = endpoint.Slug;

		EnsureConfigured();
		ClearLastEndpointError( slug );

		// If proxy mode is active and we're not the host, route through the host
		if ( ProxyEnabled && !IsHost && RequestProxy != null )
		{
			return await CallEndpointViaProxy( slug, input );
		}

		if ( !IsHost && NetworkStorageLogConfig.LogRequests )
			Log.Info( $"[NetworkStorage] {slug} direct (proxy bypass: enabled={ProxyEnabled} isHost={IsHost} hasDelegate={RequestProxy != null})" );
		string url = null;
		string bodyJson = null;
		try
		{
			var shouldUseDedicatedSecret = ShouldUseDedicatedServerSecret( endpoint );
			if ( !shouldUseDedicatedSecret && TryRejectDedicatedServerPlayerAuth( slug ) )
				return null;

			var securityRequest = await BuildEndpointSecurityRequest( slug, input ?? new { }, useDedicatedServerSecret: shouldUseDedicatedSecret );
			var headers = securityRequest.Headers;
			var hasDedicatedSecret = shouldUseDedicatedSecret && TryAddDedicatedServerSecretHeaders( headers, endpoint );
			url = BuildUrl( securityRequest.RoutePath, hasDedicatedSecret );

			string result;
			if ( input is not null )
			{
				bodyJson = JsonSerializer.Serialize( securityRequest.Body );
				if ( NetworkStorageLogConfig.LogRequests )
				{
					NetLog.Request( slug, $"POST {securityRequest.Mode} {bodyJson}" );
					Log.Info( $"[NetworkStorage] {slug} request: POST {ApiRoot}{securityRequest.RouteLabel} mode={securityRequest.Mode} body={bodyJson}" );
				}
				var content = Http.CreateJsonContent( securityRequest.Body );
				result = await Http.RequestStringAsync( url, "POST", content, headers );
			}
			else
			{
				if ( NetworkStorageLogConfig.LogRequests )
				{
					NetLog.Request( slug, "GET" );
					Log.Info( $"[NetworkStorage] {slug} request: GET {ApiRoot}/endpoints/{ProjectId}/{slug}" );
				}
				result = await Http.RequestStringAsync( url, "GET", null, headers );
			}

			if ( NetworkStorageLogConfig.LogResponses )
				Log.Info( $"[NetworkStorage] {slug} → {result}" );
			var parsed = ParseResponse( slug, result );
			if ( parsed.HasValue )
			{
				if ( NetworkStorageLogConfig.LogResponses )
					NetLog.Response( slug, TruncateJson( parsed.Value ) );
				return parsed;
			}

			// Security mismatch: config was auto-updated by ParseResponse → LogServerError, retry once
			if ( allowSecurityRetry && TryGetLastEndpointError( slug, out var code, out _ ) && IsSecurityConfigMismatchCode( code ) )
			{
				Log.Info( $"[NetworkStorage] {slug} security mismatch detected ({code}), retrying with updated config..." );
				return await CallEndpointInternal( slug, input, allowSecurityRetry: false );
			}

			return null;
		}
		catch ( System.Net.Http.HttpRequestException httpEx )
		{
			var status = httpEx.StatusCode.HasValue ? $"{(int)httpEx.StatusCode.Value} {httpEx.StatusCode.Value}" : "unknown";
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] {slug} FAILED — HTTP {status}" );
				Log.Warning( $"[NetworkStorage]   URL: {url ?? $"{ApiRoot}/endpoints/{ProjectId}/{slug}"}" );
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
				Log.Warning( $"[NetworkStorage] {slug} FAILED — {ex.Message}" );
				Log.Warning( $"[NetworkStorage]   URL: {url ?? $"{ApiRoot}/endpoints/{ProjectId}/{slug}"}" );
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

}
