using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace Sandbox;

public static partial class NetworkStorage
{
	/// <summary>Retries after the first attempt when a call is rate limited.</summary>
	internal const int MaxRateLimitRetries = 2;

	/// <summary>Total time one call may spend waiting between rate-limit retries.</summary>
	internal const double MaxRateLimitWaitSeconds = 5.0;

	/// <summary>Wait used when the server gives no usable Retry-After.</summary>
	internal const double DefaultRateLimitWaitSeconds = 1.0;

	/// <summary>
	/// True when a response is a rate-limit answer the library may retry. Covers a real HTTP 429 (s&amp;box hides its
	/// body, so only the method decides) and the library-identified endpoint wrapper (HTTP 200 with <c>status: 429</c>).
	/// Other 429s, such as plan limits, are not transient and are never retried. Reads always retry; writes retry only
	/// when the server marks the answer <c>retrySafe</c>.
	/// </summary>
	internal static bool IsRetryableRateLimit( string method, int statusCode, string body )
	{
		var status = statusCode == 200 ? ReadRoot( body, root => root.TryGetProperty( "status", out var s ) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32( out var n ) ? n : 0, 0 ) : statusCode;
		if ( status != 429 ) return false;

		var code = ReadErrorCode( body );
		if ( code != null && code != "RATE_LIMITED" ) return false;

		if ( string.Equals( method, "GET", StringComparison.OrdinalIgnoreCase ) ) return true;
		return ReadRoot( body, root => root.TryGetProperty( "retrySafe", out var r ) && r.ValueKind == JsonValueKind.True, false );
	}

	/// <summary>
	/// True for a 503 that carries Retry-After on a read: the server's own "try again" signal. Writes never retry here,
	/// because the server cannot tell from a 503 whether the earlier attempt was applied.
	/// </summary>
	internal static bool IsRetryableUnavailable( string method, int statusCode, string retryAfterHeader )
		=> statusCode == 503 && !string.IsNullOrWhiteSpace( retryAfterHeader ) && string.Equals( method, "GET", StringComparison.OrdinalIgnoreCase );

	/// <summary>
	/// Seconds to wait before retrying: the Retry-After header, else the body's <c>retryAfterSeconds</c> (the field the
	/// server sends), else the legacy <c>retryAfterSec</c>, else the default. A value that is missing, negative or not a
	/// number is ignored.
	/// </summary>
	internal static double RateLimitDelaySeconds( string retryAfterHeader, string body )
	{
		if ( double.TryParse( retryAfterHeader, NumberStyles.Float, CultureInfo.InvariantCulture, out var headerSeconds ) && headerSeconds >= 0 )
			return headerSeconds;

		var bodySeconds = ReadBodySeconds( body, "retryAfterSeconds" ) ?? ReadBodySeconds( body, "retryAfterSec" );
		return bodySeconds is >= 0 ? bodySeconds.Value : DefaultRateLimitWaitSeconds;
	}

	private static double? ReadBodySeconds( string body, string property )
		=> ReadRoot<double?>( body, root => root.TryGetProperty( property, out var r ) && r.ValueKind == JsonValueKind.Number && r.TryGetDouble( out var d ) && d >= 0 ? d : null, null );

	private static string RetryAfterHeader( HttpResponseMessage response )
		=> response.Headers.TryGetValues( "Retry-After", out var values ) ? values.FirstOrDefault() : null;

	/// <summary>
	/// The error code of a JSON error body: <c>error.code</c>, or a plain <c>error</c> string when that string is a code
	/// token (UPPER_SNAKE). A sentence in <c>error</c> is a message, never a code.
	/// </summary>
	private static string ReadErrorCode( string body )
		=> ReadRoot<string>( body, root =>
		{
			if ( !root.TryGetProperty( "error", out var error ) ) return null;
			if ( error.ValueKind == JsonValueKind.String ) return IsErrorCodeToken( error.GetString() ) ? error.GetString() : null;
			return error.ValueKind == JsonValueKind.Object && error.TryGetProperty( "code", out var code ) && code.ValueKind == JsonValueKind.String
				? code.GetString()
				: null;
		}, null );

	/// <summary>
	/// The human message of a JSON error body: <c>error.message</c>, a sentence in <c>error</c> that is not a code, the
	/// top-level <c>message</c>, or <c>detail</c>. Null when none is present or the value is blank.
	/// </summary>
	private static string ReadErrorMessage( string body )
	{
		var text = ReadRoot<string>( body, root =>
		{
			if ( root.TryGetProperty( "error", out var error ) )
			{
				if ( error.ValueKind == JsonValueKind.Object && error.TryGetProperty( "message", out var nested ) && nested.ValueKind == JsonValueKind.String )
					return nested.GetString();
				if ( error.ValueKind == JsonValueKind.String && !IsErrorCodeToken( error.GetString() ) ) return error.GetString();
			}
			if ( root.TryGetProperty( "message", out var message ) && message.ValueKind == JsonValueKind.String ) return message.GetString();
			return root.TryGetProperty( "detail", out var detail ) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null;
		}, null );
		return string.IsNullOrWhiteSpace( text ) ? null : text;
	}

	private static T ReadRoot<T>( string body, Func<JsonElement, T> read, T fallback )
	{
		try
		{
			using var document = JsonDocument.Parse( body ?? "" );
			return document.RootElement.ValueKind == JsonValueKind.Object ? read( document.RootElement ) : fallback;
		}
		catch ( JsonException )
		{
			return fallback;
		}
	}
}
