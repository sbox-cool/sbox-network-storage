using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

[TestClass]
public class NetworkStorageServerClockTests
{
	[TestInitialize]
	public void TestInitialize() => ResetServerClock();

	[TestCleanup]
	public void TestCleanup() => ResetServerClock();

	[TestMethod]
	public void ServerUtcNow_FollowsObservedServerClock()
	{
		var localAt = DateTimeOffset.UtcNow;
		NetworkStorage.ObserveServerUnixSeconds( localAt.ToUnixTimeSeconds() + 600, localAt );

		var skew = NetworkStorage.ServerUtcNow - DateTimeOffset.UtcNow;
		Assert.AreEqual( 600.0, skew.TotalSeconds, 2.0 );
	}

	[TestMethod]
	public void ServerUtcNow_IgnoresSkewWithinTolerance()
	{
		var localAt = DateTimeOffset.UtcNow;
		NetworkStorage.ObserveServerUnixSeconds( localAt.ToUnixTimeSeconds() + 3, localAt );

		Assert.IsTrue( Math.Abs( ( NetworkStorage.ServerUtcNow - DateTimeOffset.UtcNow ).TotalSeconds ) < 1.0 );
	}

	[TestMethod]
	public void ObserveServerUnixSeconds_ClampsOffsetToOneDay()
	{
		var localAt = DateTimeOffset.UtcNow;
		NetworkStorage.ObserveServerUnixSeconds( localAt.ToUnixTimeSeconds() + 365 * 24 * 3600, localAt );
		Assert.AreEqual( 24 * 3600.0, ( NetworkStorage.ServerUtcNow - DateTimeOffset.UtcNow ).TotalSeconds, 2.0 );

		NetworkStorage.ObserveServerUnixSeconds( localAt.ToUnixTimeSeconds() - 365 * 24 * 3600, localAt );
		Assert.AreEqual( -24 * 3600.0, ( NetworkStorage.ServerUtcNow - DateTimeOffset.UtcNow ).TotalSeconds, 2.0 );
	}

	[TestMethod]
	public void ObserveServerUnixSeconds_IgnoresNonPositiveAndOutOfRangeValues()
	{
		var localAt = DateTimeOffset.UtcNow;
		NetworkStorage.ObserveServerUnixSeconds( 0, localAt );
		NetworkStorage.ObserveServerUnixSeconds( -5, localAt );
		NetworkStorage.ObserveServerUnixSeconds( long.MaxValue, localAt );

		Assert.IsTrue( Math.Abs( ( NetworkStorage.ServerUtcNow - DateTimeOffset.UtcNow ).TotalSeconds ) < 1.0 );
	}

	[TestMethod]
	public void ObserveServerDateHeader_LearnsOffsetAtRequestMidpoint()
	{
		var sentAt = DateTimeOffset.UtcNow;
		var receivedAt = sentAt.AddSeconds( 2 );
		// The server answered at the midpoint of the round trip, ten minutes ahead of the local clock.
		using var response = DateResponse( sentAt.AddSeconds( 1 ).AddMinutes( 10 ), age: false );

		NetworkStorage.ObserveServerDateHeader( response, sentAt, receivedAt );

		Assert.AreEqual( 600.0, ( NetworkStorage.ServerUtcNow - DateTimeOffset.UtcNow ).TotalSeconds, 2.0 );
	}

	[TestMethod]
	public void ObserveServerDateHeader_IgnoresCachedResponses()
	{
		var now = DateTimeOffset.UtcNow;
		// A response with an Age header came from a cache, so its Date is not the server's current clock.
		using var cached = DateResponse( now.AddMinutes( 10 ), age: true );

		NetworkStorage.ObserveServerDateHeader( cached, now, now );

		Assert.IsTrue( Math.Abs( ( NetworkStorage.ServerUtcNow - DateTimeOffset.UtcNow ).TotalSeconds ) < 1.0 );
	}

	[TestMethod]
	public void ObserveServerDateHeader_IgnoresMissingOrMalformedDate()
	{
		var now = DateTimeOffset.UtcNow;
		using var missing = new HttpResponseMessage( HttpStatusCode.OK );
		using var malformed = new HttpResponseMessage( HttpStatusCode.OK );
		malformed.Headers.TryAddWithoutValidation( "Date", "not a date" );

		NetworkStorage.ObserveServerDateHeader( null, now, now );
		NetworkStorage.ObserveServerDateHeader( missing, now, now );
		NetworkStorage.ObserveServerDateHeader( malformed, now, now );

		Assert.IsTrue( Math.Abs( ( NetworkStorage.ServerUtcNow - DateTimeOffset.UtcNow ).TotalSeconds ) < 1.0 );
	}

	[TestMethod]
	public async Task SendWithRetries_LearnsServerClockFromDateHeader()
	{
		var serverNow = DateTimeOffset.UtcNow.AddMinutes( 10 );

		var body = await NetworkStorage.SendWithRetriesAsync( "GET", () => null,
			_ => Task.FromResult( DateResponse( serverNow, age: false ) ) );

		Assert.AreEqual( "{\"ok\":true,\"data\":{}}", body );
		Assert.AreEqual( 600.0, ( NetworkStorage.ServerUtcNow - DateTimeOffset.UtcNow ).TotalSeconds, 3.0 );
	}

	[TestMethod]
	public void ReadServerTimeSeconds_ReadsOnlyErrorServerTime()
	{
		using var stale = JsonDocument.Parse( "{\"ok\":false,\"error\":{\"code\":\"ENCRYPTED_REQUEST_STALE\",\"serverTime\":1767225600}}" );
		Assert.AreEqual( 1767225600L, NetworkStorage.ReadServerTimeSeconds( stale.RootElement ) );

		using var topLevel = JsonDocument.Parse( "{\"serverTime\":1767225600}" );
		Assert.AreEqual( 0L, NetworkStorage.ReadServerTimeSeconds( topLevel.RootElement ) );

		using var text = JsonDocument.Parse( "{\"error\":{\"serverTime\":\"soon\"}}" );
		Assert.AreEqual( 0L, NetworkStorage.ReadServerTimeSeconds( text.RootElement ) );

		using var missing = JsonDocument.Parse( "{\"error\":{\"code\":\"X\"}}" );
		Assert.AreEqual( 0L, NetworkStorage.ReadServerTimeSeconds( missing.RootElement ) );
	}

	[TestMethod]
	public void CreateEncryptedRequestId_UsesObservedServerClock()
	{
		var localAt = DateTimeOffset.UtcNow;
		NetworkStorage.ObserveServerUnixSeconds( localAt.ToUnixTimeSeconds() + 600, localAt );

		Assert.IsTrue( NetworkStorage.TryParseEncryptedRequestId( NetworkStorage.CreateEncryptedRequestId(), out var unixSeconds, out _ ) );
		Assert.AreEqual( 600.0, (double)( unixSeconds - localAt.ToUnixTimeSeconds() ), 2.0 );
	}

	private static void ResetServerClock()
	{
		var now = DateTimeOffset.UtcNow;
		NetworkStorage.ObserveServerUnixSeconds( now.ToUnixTimeSeconds(), now );
	}

	private static HttpResponseMessage DateResponse( DateTimeOffset serverDate, bool age )
	{
		var response = new HttpResponseMessage( HttpStatusCode.OK ) { Content = new StringContent( "{\"ok\":true,\"data\":{}}" ) };
		response.Headers.TryAddWithoutValidation( "Date", serverDate.UtcDateTime.ToString( "r", CultureInfo.InvariantCulture ) );
		if ( age ) response.Headers.TryAddWithoutValidation( "Age", "5" );
		return response;
	}
}
