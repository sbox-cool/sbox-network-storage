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
	public static bool IsRetryableRateLimit( string method, int statusCode, string body )
	{
		var status = statusCode == 200 ? ReadRoot( body, root => root.TryGetProperty( "status", out var s ) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32( out var n ) ? n : 0, 0 ) : statusCode;
		if ( status != 429 ) return false;

		var code = ReadErrorCode( body );
		if ( code != null && code != "RATE_LIMITED" ) return false;

		if ( string.Equals( method, "GET", StringComparison.OrdinalIgnoreCase ) ) return true;
		return ReadRoot( body, root => root.TryGetProperty( "retrySafe", out var r ) && r.ValueKind == JsonValueKind.True, false );
	}

	/// <summary>
	/// Seconds to wait before retrying: the Retry-After header, else the body's <c>retryAfterSec</c>, else the default.
	/// A header or body value that is missing, negative or not a number is ignored.
	/// </summary>
	public static double RateLimitDelaySeconds( string retryAfterHeader, string body )
	{
		if ( double.TryParse( retryAfterHeader, NumberStyles.Float, CultureInfo.InvariantCulture, out var headerSeconds ) && headerSeconds >= 0 )
			return headerSeconds;

		var bodySeconds = ReadRoot<double?>( body, root => root.TryGetProperty( "retryAfterSec", out var r ) && r.ValueKind == JsonValueKind.Number && r.TryGetDouble( out var d ) ? d : null, null );
		return bodySeconds is >= 0 ? bodySeconds.Value : DefaultRateLimitWaitSeconds;
	}

	private static string RetryAfterHeader( HttpResponseMessage response )
		=> response.Headers.TryGetValues( "Retry-After", out var values ) ? values.FirstOrDefault() : null;

	/// <summary>The error code of a JSON error body: <c>error.code</c> or a plain <c>error</c> string.</summary>
	private static string ReadErrorCode( string body )
		=> ReadRoot<string>( body, root =>
		{
			if ( !root.TryGetProperty( "error", out var error ) ) return null;
			if ( error.ValueKind == JsonValueKind.String ) return error.GetString();
			return error.ValueKind == JsonValueKind.Object && error.TryGetProperty( "code", out var code ) && code.ValueKind == JsonValueKind.String
				? code.GetString()
				: null;
		}, null );

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
