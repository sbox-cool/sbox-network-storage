using Sandbox;

[TestClass]
public class NetworkStorageRateLimitTests
{
	[TestMethod]
	public void IsRetryableRateLimit_RetriesReadsAndOnlyRetrySafeWrites()
	{
		// A real HTTP 429 hides its body in s&box, so a read may retry and a write may not (retrySafe unknown).
		Assert.IsTrue( NetworkStorage.IsRetryableRateLimit( "GET", 429, "" ) );
		Assert.IsFalse( NetworkStorage.IsRetryableRateLimit( "POST", 429, "" ) );

		// Endpoint wrapper: HTTP 200 carrying status 429 and a RATE_LIMITED code.
		const string limited = "{\"ok\":false,\"status\":429,\"error\":{\"code\":\"RATE_LIMITED\",\"message\":\"slow down\"}}";
		const string limitedRetrySafe = "{\"ok\":false,\"status\":429,\"error\":{\"code\":\"RATE_LIMITED\",\"message\":\"slow down\"},\"retrySafe\":true}";
		Assert.IsTrue( NetworkStorage.IsRetryableRateLimit( "GET", 200, limited ) );
		Assert.IsFalse( NetworkStorage.IsRetryableRateLimit( "POST", 200, limited ) );
		Assert.IsTrue( NetworkStorage.IsRetryableRateLimit( "POST", 200, limitedRetrySafe ) );
	}

	[TestMethod]
	public void IsRetryableRateLimit_IgnoresAnswersThatAreNotTransient()
	{
		// Plan or quota limits also answer 429 but are not transient.
		Assert.IsFalse( NetworkStorage.IsRetryableRateLimit( "GET", 429, "{\"ok\":false,\"error\":{\"code\":\"PLAN_LIMIT_EXCEEDED\"}}" ) );
		Assert.IsFalse( NetworkStorage.IsRetryableRateLimit( "GET", 200, "{\"ok\":false,\"status\":429,\"error\":{\"code\":\"PLAN_LIMIT_EXCEEDED\"}}" ) );

		// Successes and other statuses are never retried.
		Assert.IsFalse( NetworkStorage.IsRetryableRateLimit( "GET", 200, "{\"ok\":true,\"data\":{}}" ) );
		Assert.IsFalse( NetworkStorage.IsRetryableRateLimit( "GET", 200, "{\"ok\":false,\"status\":409,\"error\":{\"code\":\"PLAYER_EXISTS\"}}" ) );
		Assert.IsFalse( NetworkStorage.IsRetryableRateLimit( "GET", 500, "" ) );
	}

	[TestMethod]
	public void IsRetryableRateLimit_AcceptsPlainRateLimitErrorString()
	{
		// Request limits answer with a plain error string rather than an error object.
		Assert.IsTrue( NetworkStorage.IsRetryableRateLimit( "GET", 429, "{\"error\":\"RATE_LIMITED\"}" ) );
		Assert.IsTrue( NetworkStorage.IsRetryableRateLimit( "GET", 429, "not json" ) );
	}

	[TestMethod]
	public void RateLimitDelaySeconds_PrefersHeaderThenBodyThenDefault()
	{
		Assert.AreEqual( 2.0, NetworkStorage.RateLimitDelaySeconds( "2", "" ), 0.0 );
		Assert.AreEqual( 0.0, NetworkStorage.RateLimitDelaySeconds( "0", "" ), 0.0 );
		Assert.AreEqual( 3.0, NetworkStorage.RateLimitDelaySeconds( "soon", "{\"retryAfterSec\":3}" ), 0.0 );
		Assert.AreEqual( 1.0, NetworkStorage.RateLimitDelaySeconds( "-1", "" ), 0.0 );
		Assert.AreEqual( 1.0, NetworkStorage.RateLimitDelaySeconds( null, "not json" ), 0.0 );
		Assert.AreEqual( 1.0, NetworkStorage.RateLimitDelaySeconds( null, "" ), 0.0 );
	}
}
