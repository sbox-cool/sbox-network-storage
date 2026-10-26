using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>Which credentials a <see cref="NetworkStorageRequest"/> carries.</summary>
public enum NetworkStorageAuth
{
	/// <summary>The player's Steam ID and s&amp;box token, as the rest of the library sends them.</summary>
	Player,

	/// <summary>The project's public key only.</summary>
	Public,

	/// <summary>The dedicated server secret. Valid only on a dedicated server with a secure transport.</summary>
	DedicatedSecret
}

/// <summary>
/// A request for <see cref="NetworkStorage.SendAsync"/>: one route under the project's API root.
/// Routes that have a typed helper (<c>/endpoints/...</c>) are refused, so callers cannot skip their checks.
/// </summary>
public sealed class NetworkStorageRequest
{
	/// <summary>GET, POST, PUT, PATCH or DELETE. Defaults to GET.</summary>
	public string Method { get; init; } = "GET";

	/// <summary>Route path starting with "/", for example "/storage/{project}/{collection}". No scheme, host, query, or dot segments.</summary>
	public string Path { get; init; }

	/// <summary>Extra query values. Keys and values are escaped. The keys <c>apiKey</c>, <c>revisionTarget</c>, <c>includeStaged</c> and <c>secret-key</c> are reserved.</summary>
	public Dictionary<string, string> Query { get; init; }

	/// <summary>JSON body for POST, PUT, PATCH or DELETE. Not allowed on GET.</summary>
	public object Body { get; init; }

	/// <summary>Credentials to send. Defaults to the player.</summary>
	public NetworkStorageAuth Auth { get; init; } = NetworkStorageAuth.Player;

	/// <summary>
	/// <c>Idempotency-Key</c> for a write (1 to 128 visible characters). When omitted, one is generated for this call and
	/// reused by every retry of it. Ignored for GET.
	/// </summary>
	public string IdempotencyKey { get; init; }

	/// <summary>
	/// Use the API root the server advertises in server-info (<c>api.root</c>) instead of the configured version.
	/// Fails with <c>API_ROOT_UNAVAILABLE</c> when the server advertises none.
	/// </summary>
	public bool UseServerRoot { get; init; }
}

/// <summary>
/// Outcome of <see cref="NetworkStorage.SendAsync"/>. Check <see cref="Ok"/>; nothing is thrown for a server or transport failure.
/// </summary>
public sealed class NetworkStorageResult
{
	/// <summary>True for a 2xx reply whose body is empty or valid JSON.</summary>
	public bool Ok { get; }

	/// <summary>HTTP status of the reply. 0 when no reply arrived or the request was refused before it was sent.</summary>
	public int Status { get; }

	/// <summary>
	/// Null when <see cref="Ok"/>. Otherwise the server's error code when it sent one, or a library code such as
	/// <c>REQUEST_FAILED</c>, <c>RATE_LIMITED</c>, <c>NOT_FOUND</c> or <c>INVALID_PATH</c>.
	/// </summary>
	public string Code { get; }

	/// <summary>Human-readable detail. Null when <see cref="Ok"/>.</summary>
	public string Message { get; }

	/// <summary>The reply body parsed as JSON, verbatim: nothing is unwrapped. Null when the body is empty or not JSON.</summary>
	public JsonElement? Body { get; }

	/// <summary>Seconds the server asked the caller to wait (Retry-After, else retryAfterSeconds). Null when not given.</summary>
	public double? RetryAfterSeconds { get; }

	/// <summary>The server's request id (<c>_requestId</c> or <c>error.requestId</c>), for support. Null when absent.</summary>
	public string RequestId { get; }

	internal NetworkStorageResult( bool ok, int status, string code, string message, JsonElement? body, double? retryAfterSeconds, string requestId )
	{
		Ok = ok;
		Status = status;
		Code = code;
		Message = message;
		Body = body;
		RetryAfterSeconds = retryAfterSeconds;
		RequestId = requestId;
	}
}

public static partial class NetworkStorage
{
	/// <summary>
	/// Sends one request and returns the outcome. Never throws. Unlike the typed helpers, a failure here does not write the
	/// endpoint error ledger; the caller reads <see cref="NetworkStorageResult.Code"/> instead.
	/// Endpoints are refused with <c>USE_CALL_ENDPOINT</c>: they need the encrypted and signed path in <see cref="CallEndpoint"/>.
	/// </summary>
	public static async Task<NetworkStorageResult> SendAsync( NetworkStorageRequest request )
	{
		if ( request is null ) return Refused( "INVALID_REQUEST", "A request is required." );

		try
		{
			if ( !TryValidateSendRequest( request, out var refusedCode, out var refusedMessage ) )
				return Refused( refusedCode, refusedMessage );

			if ( !IsConfigured ) AutoConfigure();
			if ( !IsConfigured ) return Refused( "NOT_CONFIGURED", "Network Storage is not configured for this project." );
			EnsureConfigured();
			await _serverInfoTask;

			var root = request.UseServerRoot ? ServerRootUrl() : ApiRoot;
			if ( root is null )
				return Refused( "API_ROOT_UNAVAILABLE", "The server did not advertise an API root (api.root in server-info)." );

			var method = request.Method.ToUpperInvariant();
			Dictionary<string, string> headers;
			switch ( request.Auth )
			{
				case NetworkStorageAuth.Public:
					headers = BuildPublicHeaders();
					break;
				case NetworkStorageAuth.DedicatedSecret:
					if ( !TryBuildDedicatedStorageHeaders( out headers ) )
						return Refused( "DEDICATED_SECRET_UNAVAILABLE", "The dedicated server secret is not available on this process or transport." );
					break;
				default:
					if ( IsDedicatedServerProcess )
						return Refused( "DEDICATED_SECRET_REQUIRED", DedicatedServerAuthUnavailableMessage() );
					headers = await BuildAuthHeaders();
					break;
			}

			var isWrite = method != "GET";
			if ( isWrite ) headers["Idempotency-Key"] = request.IdempotencyKey ?? NewRequestToken();

			Func<HttpContent> createContent = null;
			if ( request.Body is not null )
				createContent = () => Http.CreateJsonContent( request.Body );

			var url = BuildSendUrl( root, request.Path, request.Query );
			if ( NetworkStorageLogConfig.LogRequests )
				NetLog.Request( "send", $"{method} {request.Path}" );

			var reply = await SendRawContentAsync( url, method, createContent, headers );
			if ( NetworkStorageLogConfig.LogResponses )
				Log.Info( $"[NetworkStorage] send {method} {request.Path} → HTTP {reply.Status}" );
			return ReplyResult( request, reply );
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
				Log.Warning( $"[NetworkStorage] send {request.Method} {request.Path} FAILED — {ex.Message}" );
			return Refused( "REQUEST_FAILED", ex.Message );
		}
	}

	private static NetworkStorageResult Refused( string code, string message )
	{
		if ( NetworkStorageLogConfig.LogErrors )
			Log.Warning( $"[NetworkStorage] send refused: {code} — {message}" );
		return new NetworkStorageResult( false, 0, code, message, null, null, null );
	}

	private static string ServerRootUrl() => ServerApiRoot is null ? null : $"{BaseUrl}{ServerApiRoot}";

	private static string BuildSendUrl( string root, string path, Dictionary<string, string> query )
	{
		var url = $"{root}{path}?apiKey={Uri.EscapeDataString( ApiKey )}";
		if ( query is not null )
		{
			foreach ( var pair in query )
				url += $"&{Uri.EscapeDataString( pair.Key )}={Uri.EscapeDataString( pair.Value ?? "" )}";
		}
		if ( string.Equals( PublishTarget, "next", StringComparison.OrdinalIgnoreCase ) )
			url += "&revisionTarget=next&includeStaged=true";
		if ( IsCdnRoot( root ) )
			url += $"&v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
		return url;
	}

	private static bool TryValidateSendRequest( NetworkStorageRequest request, out string code, out string message )
	{
		code = null;
		message = null;

		var method = request.Method?.ToUpperInvariant();
		if ( method is not ( "GET" or "POST" or "PUT" or "PATCH" or "DELETE" ) )
		{
			code = "INVALID_METHOD";
			message = "Method must be GET, POST, PUT, PATCH or DELETE.";
			return false;
		}

		if ( !TryValidateSendPath( request.Path, out code, out message ) ) return false;

		if ( method == "GET" && request.Body is not null )
		{
			code = "INVALID_REQUEST";
			message = "A GET request cannot carry a body.";
			return false;
		}

		if ( request.IdempotencyKey is not null && !IsValidIdempotencyKey( request.IdempotencyKey ) )
		{
			code = "INVALID_REQUEST";
			message = "IdempotencyKey must be 1 to 128 visible characters.";
			return false;
		}

		if ( request.Query is not null )
		{
			foreach ( var key in request.Query.Keys )
			{
				if ( string.IsNullOrEmpty( key ) || IsReservedQueryKey( key ) )
				{
					code = "INVALID_REQUEST";
					message = $"Query key '{key}' is empty or reserved.";
					return false;
				}
			}
		}

		return true;
	}

	private static bool TryValidateSendPath( string path, out string code, out string message )
	{
		code = null;
		message = null;

		if ( string.IsNullOrEmpty( path ) || path.Length < 2 || path[0] != '/' )
		{
			code = "INVALID_PATH";
			message = "Path must start with '/'.";
			return false;
		}

		if ( path.Contains( "//", StringComparison.Ordinal ) || path.Contains( "://", StringComparison.Ordinal ) )
		{
			code = "INVALID_PATH";
			message = "Path must not contain a scheme, a host, or an empty segment.";
			return false;
		}

		foreach ( var c in path )
		{
			if ( c == '\\' || c == '?' || c == '#' || c < ' ' || c == '\u007f' )
			{
				code = "INVALID_PATH";
				message = "Path must not contain a backslash, query, fragment, or control character.";
				return false;
			}
		}

		// Checks run on decoded segments: "%2e%2e" is a dot segment and "%65ndpoints" is "endpoints" to the server's router.
		var segments = path.Split( '/' );
		for ( var i = 0; i < segments.Length; i++ )
		{
			var decoded = Uri.UnescapeDataString( segments[i] );
			if ( decoded == "." || decoded == ".." )
			{
				code = "INVALID_PATH";
				message = "Path must not contain dot segments.";
				return false;
			}

			foreach ( var c in decoded )
			{
				if ( c == '/' || c == '\\' || c == '?' || c == '#' || c < ' ' || c == '\u007f' )
				{
					code = "INVALID_PATH";
					message = "Path must not contain an encoded separator, query, fragment, or control character.";
					return false;
				}
			}

			segments[i] = decoded;
		}

		var first = segments[1];
		if ( string.Equals( first, "endpoints", StringComparison.OrdinalIgnoreCase ) )
		{
			code = "USE_CALL_ENDPOINT";
			message = "Endpoints are called with NetworkStorage.CallEndpoint, which applies the encrypted and signed request path.";
			return false;
		}
		if ( string.Equals( first, "manage", StringComparison.OrdinalIgnoreCase ) )
		{
			code = "UNSUPPORTED_PATH";
			message = "Routes under /manage are not available to game code.";
			return false;
		}

		return true;
	}

	private static bool IsValidIdempotencyKey( string key )
	{
		if ( key.Length == 0 || key.Length > 128 ) return false;
		foreach ( var c in key )
		{
			if ( c <= ' ' || c == '\u007f' ) return false;
		}
		return true;
	}

	private static bool IsReservedQueryKey( string key )
		=> string.Equals( key, "apiKey", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( key, "revisionTarget", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( key, "includeStaged", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( key, "secret-key", StringComparison.OrdinalIgnoreCase );

	private static NetworkStorageResult ReplyResult( NetworkStorageRequest request, RawReply reply )
	{
		var hasBody = !string.IsNullOrWhiteSpace( reply.Body );
		JsonElement? body = null;
		if ( hasBody && TryParseJsonBody( reply.Body, out var element ) ) body = element;
		var requestId = ReadRequestId( body );

		if ( reply.Ok )
		{
			if ( !hasBody || body.HasValue )
				return new NetworkStorageResult( true, reply.Status, null, null, body, null, requestId );

			// A 2xx body that is not JSON is a failure for a caller who asked for a response.
			TryReadRawDocument( reply, out _, out var invalidCode, out var invalidMessage );
			return Failed( request, reply.Status, invalidCode, invalidMessage, body, null, requestId );
		}

		ClassifyFailure( reply, reply.Status == 404 ? "NOT_FOUND" : "REQUEST_FAILED", out var code, out var message );
		if ( request.Auth == NetworkStorageAuth.Player && string.Equals( code, "SBOX_AUTH_FAILED", StringComparison.OrdinalIgnoreCase ) )
			InvalidateCachedAuthToken( $"send {request.Method} {request.Path} {code}" );
		return Failed( request, reply.Status, code, message, body, RetryAfterSecondsOf( reply ), requestId );
	}

	private static NetworkStorageResult Failed( NetworkStorageRequest request, int status, string code, string message, JsonElement? body, double? retryAfterSeconds, string requestId )
	{
		if ( NetworkStorageLogConfig.LogErrors )
			Log.Warning( $"[NetworkStorage] send {request.Method} {request.Path} HTTP {status} {code} — {message}" );
		return new NetworkStorageResult( false, status, code, message, body, retryAfterSeconds, requestId );
	}

	private static double? RetryAfterSecondsOf( RawReply reply )
	{
		if ( double.TryParse( reply.RetryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var header ) && header >= 0 )
			return header;
		return ReadBodySeconds( reply.Body, "retryAfterSeconds" ) ?? ReadBodySeconds( reply.Body, "retryAfterSec" );
	}

	private static string ReadRequestId( JsonElement? body )
	{
		if ( !body.HasValue || body.Value.ValueKind != JsonValueKind.Object ) return null;
		var root = body.Value;
		return ReadStringProperty( root, "_requestId" )
			?? ( root.TryGetProperty( "error", out var error ) ? ReadStringProperty( error, "requestId" ) : null );
	}

	private static bool TryParseJsonBody( string text, out JsonElement element )
	{
		element = default;
		try
		{
			element = JsonSerializer.Deserialize<JsonElement>( text );
			return true;
		}
		catch ( JsonException )
		{
			return false;
		}
	}
}
