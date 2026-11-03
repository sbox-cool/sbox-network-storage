using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	/// <summary>A relayed endpoint call. Body is the server's reply text; Parsed is set only for the same-account shortcut.</summary>
	internal readonly record struct EndpointRead( string Slug, string Body, JsonElement? Parsed, string ErrorCode, string ErrorMessage );

	/// <summary>A relayed document read. DocumentJson is the stored JSON, unchanged.</summary>
	internal readonly record struct DocumentRead( string DocumentJson, string ErrorCode, string ErrorMessage );

	// ── Host "On Behalf Of" Methods ──

	/// <summary>
	/// Call an endpoint on behalf of another player (host only).
	/// Requires the client's auth token as proof of consent.
	/// Sends: host auth (Facepunch-verified) + client token + HMAC proxy signature
	/// scoped to this project + endpoint to prevent cross-server replay.
	/// </summary>
	public static async Task<JsonElement?> CallEndpointAs( string targetSteamId, string clientToken, string slug, object input = null )
	{
		var read = await ReadEndpointAs( targetSteamId, clientToken, slug, input );
		if ( read.Parsed.HasValue ) return read.Parsed;
		if ( read.Body is null ) return null;

		// The host has no key for a sealed response, so it relays the ciphertext for the requesting client to open.
		if ( TryRelaySealedResponse( read.Body, out var relayed ) ) return relayed;

		var parsed = ParseResponse( read.Slug, read.Body );
		if ( parsed.HasValue && NetworkStorageLogConfig.LogResponses )
			NetLog.Response( read.Slug, TruncateJson( parsed.Value ) );
		return parsed;
	}

	/// <summary>
	/// Calls an endpoint on behalf of another player and returns the server's reply text unparsed, so a proxy can relay it.
	/// A failure carries the code and message the host recorded.
	/// </summary>
	internal static async Task<EndpointRead> ReadEndpointAs( string targetSteamId, string clientToken, string slug, object input = null )
	{
		var endpoint = ResolveEndpointReference( slug );
		if ( !ApplyEndpointReferenceConfiguration( endpoint ) )
		{
			RecordInvalidEndpoint( endpoint );
			return new EndpointRead( slug, null, null, "INVALID_ENDPOINT", $"'{slug}' is not a valid endpoint reference." );
		}
		slug = endpoint.Slug;

		EnsureConfigured();
		ClearLastEndpointError( slug );
		if ( !await RequireCapabilityAsync( "endpoints", slug ) ) return LastEndpointFailure( slug );

		// A client-sealed request must be relayed unchanged, even when both editor instances share a Steam account.
		var sealedInput = input is JsonElement element && element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty( "encrypted", out var encrypted ) && encrypted.ValueKind == JsonValueKind.True;
		if ( targetSteamId == Game.SteamId.ToString() && !sealedInput )
		{
			if ( NetworkStorageLogConfig.LogProxy )
				Log.Info( $"[NetworkStorage] {slug} same-account shortcut (targetSteamId == hostSteamId), calling directly" );
			var direct = await CallEndpoint( slug, input );
			return direct.HasValue
				? new EndpointRead( slug, direct.Value.GetRawText(), direct, null, null )
				: LastEndpointFailure( slug );
		}

		string url = null;
		string bodyJson = null;
		try
		{
			var routePath = $"/endpoints/{ProjectId}/{EscapeRouteSegment( slug )}";
			var usesDedicatedSecret = ShouldUseDedicatedServerSecret( endpoint );
			if ( !usesDedicatedSecret && TryRejectDedicatedServerPlayerAuth( slug ) )
				return LastEndpointFailure( slug );

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

			await EnsureRuntimeSecurityConfigAsync( "endpoint-on-behalf" );
			var refusal = EndpointSecurityRefusal();
			if ( refusal is not null )
			{
				RecordEndpointError( slug, refusal.Code, refusal.Message );
				return LastEndpointFailure( slug );
			}

			RawReply reply;
			if ( input is not null )
			{
				bodyJson = JsonSerializer.Serialize( input );
				if ( NetworkStorageLogConfig.LogRequests )
				{
					NetLog.Request( slug, $"POST (as {targetSteamId}) {bodyJson}" );
					Log.Info( $"[NetworkStorage] {slug} request: POST (as {targetSteamId}) {ApiRoot}{routePath} body={bodyJson}" );
				}
				reply = await SendRawContentAsync( url, "POST", () => Http.CreateJsonContent( input ), headers );
			}
			else
			{
				if ( RuntimeEnableEncryptedRequests )
				{
					// A GET has no body, so it cannot carry the encrypted envelope the server requires.
					var message = $"Encrypted requests are required, and an on-behalf call without input would be sent as GET. Call {slug} from the player's own client, which encrypts its request.";
					Log.Warning( $"[NetworkStorage] {slug} (as {targetSteamId}): {message}" );
					RecordEndpointError( slug, "ENCRYPTION_REQUIRES_POST", message );
					return LastEndpointFailure( slug );
				}
				if ( NetworkStorageLogConfig.LogRequests )
				{
					NetLog.Request( slug, $"GET (as {targetSteamId})" );
					Log.Info( $"[NetworkStorage] {slug} request: GET (as {targetSteamId}) {ApiRoot}/endpoints/{ProjectId}/{slug}" );
				}
				reply = await SendRawAsync( url, "GET", null, headers );
			}

			if ( reply.Ok )
			{
				if ( NetworkStorageLogConfig.LogResponses )
					Log.Info( $"[NetworkStorage] {slug} (as {targetSteamId}) → {reply.Body}" );
				return new EndpointRead( slug, reply.Body, null, null, null );
			}

			// A server error body names its own failure, so relay it. Otherwise report the host's failure.
			RecordReplyFailure( slug, reply, "HTTP_ERROR" );
			return !string.IsNullOrEmpty( reply.Body ) && ReadErrorCode( reply.Body ) is not null
				? new EndpointRead( slug, reply.Body, null, null, null )
				: LastEndpointFailure( slug );
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] {slug} (as {targetSteamId}) FAILED — {ex.Message}" );
				Log.Warning( $"[NetworkStorage]   URL: {url}" );
				Log.Warning( $"[NetworkStorage]   Method: {( input is not null ? "POST" : "GET" )}" );
				if ( bodyJson != null )
					Log.Warning( $"[NetworkStorage]   Body: {bodyJson}" );
				Log.Warning( $"[NetworkStorage]   Exception: {ex}" );
				NetLog.Error( slug, ex.Message );
			}
			RecordEndpointError( slug, "REQUEST_FAILED", ex.Message );
			return LastEndpointFailure( slug );
		}
	}

	/// <summary>The error recorded for a slug as a failed read. Falls back to REQUEST_FAILED when nothing was recorded.</summary>
	private static EndpointRead LastEndpointFailure( string slug )
	{
		TryGetLastEndpointError( slug, out var code, out var message );
		return new EndpointRead( slug, null, null, code ?? "REQUEST_FAILED", message ?? "The endpoint call failed." );
	}

	/// <summary>
	/// Read a document on behalf of another player (host only).
	/// Requires the client's auth token as proof of consent.
	/// </summary>
	public static async Task<JsonElement?> GetDocumentAs( string targetSteamId, string clientToken, string collectionId, string documentId = null )
	{
		var read = await ReadDocumentAs( targetSteamId, clientToken, collectionId, documentId );
		return read.DocumentJson is null ? null : JsonSerializer.Deserialize<JsonElement>( read.DocumentJson );
	}

	/// <summary>
	/// Reads a document on behalf of another player and returns the stored JSON unchanged, so a proxy can relay it.
	/// Stored data is not a server reply, so it is never unwrapped or checked for ok/error fields.
	/// </summary>
	internal static async Task<DocumentRead> ReadDocumentAs( string targetSteamId, string clientToken, string collectionId, string documentId = null )
	{
		EnsureConfigured();
		ClearLastEndpointError( "storage" );
		if ( !await RequireCapabilityAsync( "records", "storage" ) ) return LastStorageFailure();

		// Same-machine shortcut: same Steam account as host, call directly
		if ( targetSteamId == Game.SteamId.ToString() )
		{
			if ( NetworkStorageLogConfig.LogProxy )
				Log.Info( $"[NetworkStorage] storage/{collectionId} same-account shortcut, calling directly" );
			var direct = await GetDocument( collectionId, documentId );
			return direct.HasValue ? new DocumentRead( direct.Value.GetRawText(), null, null ) : LastStorageFailure();
		}

		try
		{
			var docId = documentId ?? targetSteamId;
			var path = $"/storage/{EscapeRouteSegment( ProjectId )}/{EscapeRouteSegment( collectionId )}/{EscapeRouteSegment( docId )}";
			var usesDedicatedSecret = TryBuildDedicatedStorageHeaders( out var headers );
			if ( !usesDedicatedSecret && TryRejectDedicatedServerPlayerAuth( "storage" ) )
				return LastStorageFailure();

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
			var reply = await SendRawAsync( url, "GET", null, headers );
			if ( !reply.Ok )
			{
				RecordReplyFailure( "storage", reply, reply.Status == 404 ? "NOT_FOUND" : "REQUEST_FAILED" );
				return LastStorageFailure();
			}

			if ( !TryReadRawDocument( reply, out var document, out var code, out var message ) )
			{
				RecordEndpointError( "storage", code, message );
				return new DocumentRead( null, code, message );
			}

			if ( NetworkStorageLogConfig.LogResponses )
				Log.Info( $"[NetworkStorage] storage (as {targetSteamId}) → {TruncateJson( reply.Body, 300 )}" );
			return new DocumentRead( document.GetRawText(), null, null );
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] ReadDocumentAs({targetSteamId}): {ex.Message}" );
				NetLog.Error( "storage", ex.Message );
			}
			RecordEndpointError( "storage", "REQUEST_FAILED", ex.Message );
			return LastStorageFailure();
		}
	}

	/// <summary>The error recorded for the storage slot as a failed read. Falls back to REQUEST_FAILED when nothing was recorded.</summary>
	private static DocumentRead LastStorageFailure()
	{
		TryGetLastEndpointError( "storage", out var code, out var message );
		return new DocumentRead( null, code ?? "REQUEST_FAILED", message ?? "The document read failed." );
	}
}
