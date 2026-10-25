using System;
using System.Net.Http;
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

	private static async Task<JsonElement?> CallEndpointInternal( string slug, object input, bool allowSecurityRetry, bool allowClockRetry = true )
	{
		var endpoint = ResolveEndpointReference( slug );
		if ( !ApplyEndpointReferenceConfiguration( endpoint ) )
		{
			RecordInvalidEndpoint( endpoint );
			return null;
		}
		slug = endpoint.Slug;

		EnsureConfigured();
		ClearLastEndpointError( slug );
		if ( !await RequireCapabilityAsync( "endpoints", slug ) ) return null;

		// If proxy mode is active and we're not the host, route through the host
		if ( ProxyEnabled && !IsHost && RequestProxy != null )
		{
			return await CallEndpointViaProxy( slug, input );
		}

		if ( !IsHost && NetworkStorageLogConfig.LogRequests )
			Log.Info( $"[NetworkStorage] {slug} direct (proxy bypass: enabled={ProxyEnabled} isHost={IsHost} hasDelegate={RequestProxy != null})" );
		string url = null;
		string bodyJson = null;
		double requestStartedAt = 0;
		try
		{
			var shouldUseDedicatedSecret = ShouldUseDedicatedServerSecret( endpoint );
			if ( !shouldUseDedicatedSecret && TryRejectDedicatedServerPlayerAuth( slug ) )
				return null;

			var securityRequest = await BuildEndpointSecurityRequest( slug, input ?? new { }, useDedicatedServerSecret: shouldUseDedicatedSecret );
			if ( securityRequest is null )
				return null;
			var headers = securityRequest.Headers;
			var hasDedicatedSecret = shouldUseDedicatedSecret && TryAddDedicatedServerSecretHeaders( headers, endpoint );
			url = BuildUrl( securityRequest.RoutePath, hasDedicatedSecret );

			// Encrypted calls always POST: the envelope is the body, so a call without input still needs one.
			var usePost = input is not null || securityRequest.Encrypted;
			// Each send attempt uses the request it was built for. Attempts after the first call Reseal(), so a retried
			// encrypted request never reuses an encryptedRequestId the server may already have consumed.
			var attempt = securityRequest;
			var attempts = 0;
			Func<HttpContent> createContent = null;
			if ( usePost )
			{
				createContent = () =>
				{
					if ( attempts++ > 0 && attempt.Reseal is not null )
						attempt = attempt.Reseal();
					bodyJson = JsonSerializer.Serialize( attempt.Body );
					if ( NetworkStorageLogConfig.LogRequests )
					{
						NetLog.Request( slug, $"POST {attempt.Mode} {bodyJson}" );
						Log.Info( $"[NetworkStorage] {slug} request: POST {ApiRoot}{attempt.RouteLabel} mode={attempt.Mode} revision={NetworkStoragePackageInfo.RuntimeRevisionId?.ToString() ?? "unknown"} target={PublishTarget} body={bodyJson}" );
					}
					return Http.CreateJsonContent( attempt.Body );
				};
			}

			requestStartedAt = RealTime.Now;
			RawReply reply;
			if ( usePost )
			{
				reply = await SendRawContentAsync( url, "POST", createContent, headers );
			}
			else
			{
				if ( NetworkStorageLogConfig.LogRequests )
				{
					NetLog.Request( slug, "GET" );
					Log.Info( $"[NetworkStorage] {slug} request: GET {ApiRoot}/endpoints/{ProjectId}/{slug}" );
				}
				reply = await SendRawAsync( url, "GET", null, headers );
			}

			var responseBody = reply.Body;
			if ( reply.Ok )
			{
				responseBody = UnsealEndpointResponse( attempt.ResponseContext, slug, reply.Body );
				if ( responseBody is null )
					return null;
				if ( NetworkStorageLogConfig.LogResponses )
					Log.Info( $"[NetworkStorage] {slug} → {responseBody}" );
				var parsed = ParseResponse( slug, responseBody );
				if ( parsed.HasValue )
				{
					NetworkStorageAnalyticsRuntime.RecordEndpointDiagnostic( slug, ok: true, elapsedMs: requestStartedAt > 0 ? (RealTime.Now - requestStartedAt) * 1000.0 : 0 );
					if ( NetworkStorageLogConfig.LogResponses )
						NetLog.Response( slug, TruncateJson( parsed.Value ) );
					return parsed;
				}
			}
			else
			{
				RecordReplyFailure( slug, reply, "HTTP_ERROR" );
			}

			// A security refusal the server marks retrySafe was rejected before the handler ran. Rebuild once with the
			// updated config. Any other refusal is final.
			if ( allowSecurityRetry && responseBody is not null && IsRetrySafeSecurityRefusal( responseBody ) )
			{
				Log.Info( $"[NetworkStorage] {slug} security config refused ({ReadErrorCode( responseBody )}), retrying once with updated config..." );
				await EnsureRuntimeSecurityConfigAsync( "security-retry", forceRefresh: true );
				return await CallEndpointInternal( slug, input, allowSecurityRetry: false, allowClockRetry: allowClockRetry );
			}
			// The refusal came before the request was used and carries the server clock, so one retry with a
			// freshly built envelope is safe.
			if ( allowClockRetry && LastEndpointErrorWasStaleRequest( slug ) )
			{
				Log.Info( $"[NetworkStorage] {slug} request id was outside the server replay window, retrying with server time." );
				return await CallEndpointInternal( slug, input, allowSecurityRetry, allowClockRetry: false );
			}

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
