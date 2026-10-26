using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// One server reply as received. A non-2xx status is data, not an exception: s&amp;box exposes the status and body of
/// every response, so the library reports the server's own error code instead of a generic HTTP failure.
/// Status 0 means no response arrived; <see cref="TransportError"/> then holds the reason.
/// </summary>
internal sealed record RawReply( int Status, string Body, string TransportError, string RetryAfter )
{
	public bool Ok => Status is >= 200 and < 300;
}

public static partial class NetworkStorage
{
	/// <summary>Sends a request with an optional JSON body. Never throws for an HTTP status.</summary>
	internal static Task<RawReply> SendRawAsync( string url, string method, object body, Dictionary<string, string> headers )
	{
		Func<HttpContent> createContent = null;
		if ( body is not null ) createContent = () => Http.CreateJsonContent( body );
		return SendRawContentAsync( url, method, createContent, headers );
	}

	/// <summary>
	/// Sends a request whose content is built per attempt. Adds correlation and write-idempotency tokens unless the
	/// caller set them; transport retries reuse the same headers and tokens.
	/// </summary>
	internal static Task<RawReply> SendRawContentAsync( string url, string method, Func<HttpContent> createContent, Dictionary<string, string> headers )
	{
		headers ??= new Dictionary<string, string>();
		if ( !headers.ContainsKey( "X-Correlation-ID" ) ) headers["X-Correlation-ID"] = NewRequestToken();
		if ( method is "POST" or "PUT" or "PATCH" or "DELETE" && !headers.ContainsKey( "Idempotency-Key" ) )
			headers["Idempotency-Key"] = NewRequestToken();
		return SendWithRetriesAsync( method, createContent, content => Http.RequestAsync( url, method, content, headers ) );
	}

	/// <summary>A fresh opaque token for correlation ids and idempotency keys.</summary>
	internal static string NewRequestToken() => Guid.NewGuid().ToString( "N" );

	/// <summary>Status text for a reply without a server error body, e.g. "HTTP 413 RequestEntityTooLarge".</summary>
	internal static string HttpStatusMessage( int status ) => $"HTTP {status} {(HttpStatusCode)status}";

	/// <summary>True for an UPPER_SNAKE_CASE code such as <c>RATE_LIMITED</c>. Sentences are messages, not codes.</summary>
	internal static bool IsErrorCodeToken( string value )
	{
		if ( string.IsNullOrEmpty( value ) || value.Length > 64 || value[0] < 'A' || value[0] > 'Z' ) return false;
		foreach ( var c in value )
		{
			if ( !((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_') ) return false;
		}
		return true;
	}

	/// <summary>
	/// Code and message for a failed reply. A JSON error keeps the server's own code. Otherwise a 429 is
	/// <c>RATE_LIMITED</c> and anything else gets <paramref name="fallbackCode"/>. Transport failures are <c>REQUEST_FAILED</c>.
	/// </summary>
	internal static void ClassifyFailure( RawReply reply, string fallbackCode, out string code, out string message )
	{
		if ( reply.TransportError is not null )
		{
			code = "REQUEST_FAILED";
			message = reply.TransportError;
			return;
		}

		code = ReadErrorCode( reply.Body ) ?? (reply.Status == 429 ? "RATE_LIMITED" : fallbackCode);
		var serverMessage = ReadErrorMessage( reply.Body );
		message = string.IsNullOrWhiteSpace( serverMessage ) ? HttpStatusMessage( reply.Status ) : serverMessage;
	}

	/// <summary>
	/// Records a failed reply under <paramref name="slug"/>. A reply carrying a server JSON error goes through
	/// <see cref="LogServerError"/>, so its code and side effects (auth invalidation, security config) match the envelope path.
	/// </summary>
	internal static void RecordReplyFailure( string slug, RawReply reply, string fallbackCode )
	{
		if ( reply.TransportError is null && ReadErrorCode( reply.Body ) is not null )
		{
			using var document = JsonDocument.Parse( reply.Body );
			LogServerError( slug, document.RootElement );
			return;
		}

		ClassifyFailure( reply, fallbackCode, out var code, out var message );
		RecordFailure( slug, code, message );
	}

	/// <summary>Logs and records a failure that has no server JSON error body.</summary>
	internal static void RecordFailure( string slug, string code, string message )
	{
		if ( NetworkStorageLogConfig.LogErrors )
		{
			Log.Warning( $"[NetworkStorage] {slug} FAILED - {code}: {message}" );
			NetLog.Error( slug, message );
		}
		RecordEndpointError( slug, code, message );
	}

	/// <summary>
	/// Reads a document reply verbatim. Any valid JSON value on a 2xx reply is the document. Nothing is unwrapped, and keys
	/// such as <c>ok</c>, <c>error</c>, <c>body</c> or <c>_revisionStatus</c> carry no meaning in stored data.
	/// </summary>
	internal static bool TryReadRawDocument( RawReply reply, out JsonElement document, out string code, out string message )
	{
		document = default;
		if ( !reply.Ok )
		{
			ClassifyFailure( reply, reply.Status == 404 ? "NOT_FOUND" : "REQUEST_FAILED", out code, out message );
			return false;
		}

		if ( string.IsNullOrWhiteSpace( reply.Body ) )
		{
			code = "EMPTY_RESPONSE";
			message = "Server returned an empty response.";
			return false;
		}

		try
		{
			document = JsonSerializer.Deserialize<JsonElement>( reply.Body );
		}
		catch ( JsonException )
		{
			var trimmed = reply.Body.TrimStart();
			var looksJson = trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
			code = looksJson ? "INVALID_JSON" : "INVALID_RESPONSE";
			message = "Server returned a response that is not valid JSON.";
			return false;
		}

		code = null;
		message = null;
		return true;
	}

	/// <summary>
	/// True for a <c>SECURITY_*</c> refusal the server marks <c>retrySafe</c>: the request was refused before it ran, so the
	/// caller may refresh its security config and send again. A prefix match alone is not enough.
	/// </summary>
	internal static bool IsRetrySafeSecurityRefusal( string body )
	{
		if ( string.IsNullOrEmpty( body ) ) return false;
		var code = ReadErrorCode( body );
		return code is not null && IsSecurityConfigMismatchCode( code )
			&& ReadRoot( body, root => root.TryGetProperty( "retrySafe", out var r ) && r.ValueKind == JsonValueKind.True, false );
	}

	/// <summary>
	/// Runs one event listener. A listener that throws is logged and the throw never reaches the request that raised the event.
	/// </summary>
	internal static void InvokeSafely<T>( Action<T> handler, T value, string name )
	{
		if ( handler is null ) return;
		try
		{
			handler( value );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] {name} listener failed: {ex.Message}" );
		}
	}

	/// <summary>Parameterless form of <see cref="InvokeSafely{T}(Action{T}, T, string)"/>.</summary>
	internal static void InvokeSafely( Action handler, string name )
	{
		if ( handler is null ) return;
		try
		{
			handler();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] {name} listener failed: {ex.Message}" );
		}
	}
}
