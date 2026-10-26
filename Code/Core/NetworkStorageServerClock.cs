using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace Sandbox;

/// <summary>
/// The server's clock as this client estimates it. Encrypted request ids embed a unix timestamp that the server
/// accepts only inside its replay window, so a client whose clock is far off would be refused on every encrypted
/// call. The estimate comes from the server itself: the <c>serverTime</c> of an <c>ENCRYPTED_REQUEST_STALE</c>
/// refusal, or the <c>Date</c> header of a response that was not served from a cache.
/// </summary>
public static partial class NetworkStorage
{
	// Date has one-second resolution and a round trip adds error, so smaller differences are treated as zero.
	private static readonly TimeSpan ServerClockTolerance = TimeSpan.FromSeconds( 5 );

	// Bounds one bad server value so it cannot push request ids far from any real time.
	private static readonly TimeSpan MaxServerClockOffset = TimeSpan.FromSeconds( 24 * 60 * 60 );

	// Largest unix second DateTimeOffset can represent; larger values are ignored instead of thrown.
	private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

	private static TimeSpan _serverClockOffset;
	private static bool _loggedServerClockOffset;

	/// <summary>The server's current time as this client estimates it. Equals UtcNow when the clocks agree.</summary>
	internal static DateTimeOffset ServerUtcNow => DateTimeOffset.UtcNow + EffectiveServerClockOffset;

	private static TimeSpan EffectiveServerClockOffset
		=> _serverClockOffset < ServerClockTolerance && _serverClockOffset > -ServerClockTolerance ? TimeSpan.Zero : _serverClockOffset;

	/// <summary>
	/// Records that the server's clock read <paramref name="serverUnixSeconds"/> when this client's clock read
	/// <paramref name="localAt"/>. The server read its clock somewhere inside that whole second, so the estimate uses
	/// the middle of it. Values outside the representable range are ignored.
	/// </summary>
	internal static void ObserveServerUnixSeconds( long serverUnixSeconds, DateTimeOffset localAt )
	{
		if ( serverUnixSeconds <= 0 || serverUnixSeconds > MaxUnixSeconds ) return;

		// Offset from the server's whole second to the midpoint of its second, less the local clock's place in its own second.
		var localMillisecondsIntoSecond = localAt.ToUnixTimeMilliseconds() - localAt.ToUnixTimeSeconds() * 1000;
		var offset = TimeSpan.FromSeconds( serverUnixSeconds - localAt.ToUnixTimeSeconds() ) - TimeSpan.FromMilliseconds( localMillisecondsIntoSecond ) + TimeSpan.FromMilliseconds( 500 );
		_serverClockOffset = offset > MaxServerClockOffset ? MaxServerClockOffset
			: offset < -MaxServerClockOffset ? -MaxServerClockOffset
			: offset;

		var effective = EffectiveServerClockOffset;
		if ( effective == TimeSpan.Zero || _loggedServerClockOffset ) return;
		_loggedServerClockOffset = true;
		Log.Info( $"[NetworkStorage] Local clock differs from the server by {effective.TotalSeconds:+0.0;-0.0} s; encrypted request ids use server time." );
	}

	/// <summary>
	/// Learns the server clock from a response's <c>Date</c> header, read against the midpoint of the request. A response
	/// carrying <c>Age</c> came from a cache, where <c>Date</c> is when the entry was stored, so it is ignored.
	/// </summary>
	internal static void ObserveServerDateHeader( HttpResponseMessage response, DateTimeOffset sentAt, DateTimeOffset receivedAt )
	{
		if ( response is null || response.Headers.TryGetValues( "Age", out _ ) ) return;
		if ( !response.Headers.TryGetValues( "Date", out var values ) ) return;
		if ( !DateTimeOffset.TryParse( values.FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var serverDate ) ) return;

		var localMidpoint = sentAt + TimeSpan.FromTicks( ( receivedAt - sentAt ).Ticks / 2 );
		ObserveServerUnixSeconds( serverDate.ToUnixTimeSeconds(), localMidpoint );
	}

	/// <summary>The server's unix time in a refusal body (<c>error.serverTime</c>). Zero when absent or malformed.</summary>
	internal static long ReadServerTimeSeconds( JsonElement body )
	{
		if ( body.ValueKind != JsonValueKind.Object || !body.TryGetProperty( "error", out var error ) || error.ValueKind != JsonValueKind.Object ) return 0;
		if ( !error.TryGetProperty( "serverTime", out var value ) || value.ValueKind != JsonValueKind.Number ) return 0;
		return value.TryGetInt64( out var seconds ) ? seconds : 0;
	}

	/// <summary>True when the last refusal for slug was a stale encrypted request id, which a fresh id can fix.</summary>
	private static bool LastEndpointErrorWasStaleRequest( string slug )
		=> TryGetLastEndpointError( slug, out var code, out _ ) && code == "ENCRYPTED_REQUEST_STALE";
}
