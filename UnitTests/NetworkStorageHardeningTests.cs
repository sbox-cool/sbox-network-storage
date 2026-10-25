using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

[TestClass]
public class NetworkStorageHardeningTests
{
	private static JsonElement Parse( string json ) => JsonDocument.Parse( json ).RootElement.Clone();

	[TestMethod]
	public void ClassifyFailure_KeepsServerCodeAndMessage()
	{
		const string body = "{\"ok\":false,\"error\":\"ENDPOINT_ONLY\",\"message\":\"This collection can only be accessed through endpoints.\"}";
		NetworkStorage.ClassifyFailure( new RawReply( 403, body, null, null ), "REQUEST_FAILED", out var code, out var message );
		Assert.AreEqual( "ENDPOINT_ONLY", code );
		Assert.AreEqual( "This collection can only be accessed through endpoints.", message );
	}

	[TestMethod]
	public void ClassifyFailure_NonJsonKeepsStatusInMessage()
	{
		NetworkStorage.ClassifyFailure( new RawReply( 413, "", null, null ), "HTTP_ERROR", out var code, out var message );
		Assert.AreEqual( "HTTP_ERROR", code );
		Assert.AreEqual( "HTTP 413 RequestEntityTooLarge", message );
	}

	[TestMethod]
	public void ClassifyFailure_RateLimitAndTransportFailures()
	{
		NetworkStorage.ClassifyFailure( new RawReply( 429, "", null, null ), "REQUEST_FAILED", out var code, out _ );
		Assert.AreEqual( "RATE_LIMITED", code );

		NetworkStorage.ClassifyFailure( new RawReply( 0, null, "Access to 'x' is not allowed.", null ), "HTTP_ERROR", out code, out var message );
		Assert.AreEqual( "REQUEST_FAILED", code );
		Assert.AreEqual( "Access to 'x' is not allowed.", message );
	}

	[TestMethod]
	public void IsErrorCodeToken_AcceptsOnlyCodeShapedStrings()
	{
		Assert.IsTrue( NetworkStorage.IsErrorCodeToken( "ENDPOINT_ONLY" ) );
		Assert.IsFalse( NetworkStorage.IsErrorCodeToken( "just a string" ) );
	}

	[TestMethod]
	public void TryReadRawDocument_PassesDocumentsThroughVerbatim()
	{
		// Top-level arrays and envelope-shaped documents are returned as stored, never unwrapped.
		Assert.IsTrue( NetworkStorage.TryReadRawDocument( new RawReply( 200, "[1,2]", null, null ), out var array, out _, out _ ) );
		Assert.AreEqual( JsonValueKind.Array, array.ValueKind );

		const string envelope = "{\"ok\":true,\"body\":{\"gold\":5}}";
		Assert.IsTrue( NetworkStorage.TryReadRawDocument( new RawReply( 200, envelope, null, null ), out var document, out _, out _ ) );
		Assert.AreEqual( 5, document.GetProperty( "body" ).GetProperty( "gold" ).GetInt32() );
	}

	[TestMethod]
	public void TryReadRawDocument_ReportsStatusAndBodyFailures()
	{
		Assert.IsFalse( NetworkStorage.TryReadRawDocument( new RawReply( 404, "", null, null ), out _, out var notFound, out _ ) );
		Assert.AreEqual( "NOT_FOUND", notFound );

		Assert.IsFalse( NetworkStorage.TryReadRawDocument( new RawReply( 200, "", null, null ), out _, out var empty, out _ ) );
		Assert.AreEqual( "EMPTY_RESPONSE", empty );

		Assert.IsFalse( NetworkStorage.TryReadRawDocument( new RawReply( 200, "{oops", null, null ), out _, out var invalid, out _ ) );
		Assert.AreEqual( "INVALID_JSON", invalid );
	}

	[TestMethod]
	public void RateLimitDelaySeconds_ReadsFractionalBodySeconds()
	{
		Assert.AreEqual( 2.5, NetworkStorage.RateLimitDelaySeconds( null, "{\"retryAfterSeconds\":2.5}" ), 0.0 );
	}

	[TestMethod]
	public void IsRetrySafeSecurityRefusal_NeedsSecurityCodeAndRetrySafe()
	{
		Assert.IsTrue( NetworkStorage.IsRetrySafeSecurityRefusal( "{\"ok\":false,\"error\":{\"code\":\"SECURITY_CONFIG_STALE\"},\"retrySafe\":true}" ) );
		Assert.IsFalse( NetworkStorage.IsRetrySafeSecurityRefusal( "{\"ok\":false,\"error\":{\"code\":\"SECURITY_CONFIG_STALE\"}}" ) );
		Assert.IsFalse( NetworkStorage.IsRetrySafeSecurityRefusal( "{\"ok\":false,\"error\":{\"code\":\"ENCRYPTED_REQUEST_STALE\"},\"retrySafe\":true}" ) );
	}

	[TestMethod]
	public void JsonHelpers_NeverThrowOnMismatchedValues()
	{
		var doc = Parse( "{\"f\":2.5,\"big\":99999999999,\"n\":5,\"s\":\"12\",\"b\":true,\"o\":{}}" );
		Assert.AreEqual( 7, JsonHelpers.GetInt( doc, "f", 7 ) );
		Assert.AreEqual( 7, JsonHelpers.GetInt( doc, "big", 7 ) );
		Assert.AreEqual( 12, JsonHelpers.GetInt( doc, "s", 7 ) );
		Assert.AreEqual( "5", JsonHelpers.GetString( doc, "n", "fallback" ) );
		Assert.AreEqual( "true", JsonHelpers.GetString( doc, "b", "fallback" ) );
		Assert.AreEqual( "fallback", JsonHelpers.GetString( doc, "o", "fallback" ) );
		Assert.AreEqual( 7, JsonHelpers.GetInt( Parse( "[1]" ), "f", 7 ) );
	}

	[TestMethod]
	public async Task SendAsync_RefusesBadRequestsBeforeAnySend()
	{
		var logErrors = NetworkStorageLogConfig.LogErrors;
		NetworkStorageLogConfig.LogErrors = false;
		try
		{
			Assert.AreEqual( "INVALID_REQUEST", ( await NetworkStorage.SendAsync( null ) ).Code );
			Assert.AreEqual( "USE_CALL_ENDPOINT", ( await NetworkStorage.SendAsync( new NetworkStorageRequest { Path = "/endpoints/x" } ) ).Code );
			Assert.AreEqual( "INVALID_PATH", ( await NetworkStorage.SendAsync( new NetworkStorageRequest { Path = "/" } ) ).Code );
			Assert.AreEqual( "INVALID_PATH", ( await NetworkStorage.SendAsync( new NetworkStorageRequest { Path = "storage" } ) ).Code );
			Assert.AreEqual( "INVALID_PATH", ( await NetworkStorage.SendAsync( new NetworkStorageRequest { Path = "/../storage" } ) ).Code );
			Assert.AreEqual( "UNSUPPORTED_PATH", ( await NetworkStorage.SendAsync( new NetworkStorageRequest { Path = "/manage/x" } ) ).Code );
			Assert.AreEqual( "INVALID_METHOD", ( await NetworkStorage.SendAsync( new NetworkStorageRequest { Method = "TRACE", Path = "/storage" } ) ).Code );
			Assert.AreEqual( "INVALID_REQUEST", ( await NetworkStorage.SendAsync( new NetworkStorageRequest { Path = "/storage", Body = new { a = 1 } } ) ).Code );
			Assert.AreEqual( "INVALID_REQUEST", ( await NetworkStorage.SendAsync( new NetworkStorageRequest { Path = "/storage", IdempotencyKey = new string( 'x', 129 ) } ) ).Code );
			Assert.AreEqual( "INVALID_REQUEST", ( await NetworkStorage.SendAsync( new NetworkStorageRequest { Path = "/storage", Query = new() { ["apiKey"] = "x" } } ) ).Code );
		}
		finally
		{
			NetworkStorageLogConfig.LogErrors = logErrors;
		}
	}
}
