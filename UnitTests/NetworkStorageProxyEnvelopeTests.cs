using Sandbox;

[TestClass]
public class NetworkStorageProxyEnvelopeTests
{
	[TestMethod]
	public void Success_RoundTripsBodyAsOkReply()
	{
		var raw = NetworkStorageProxyEnvelope.Success( NetworkStorageProxyEnvelope.EndpointKind, "{\"ok\":true,\"data\":{}}" );
		var reply = NetworkStorageProxyEnvelope.Read( raw, NetworkStorageProxyEnvelope.EndpointKind );

		Assert.IsTrue( reply.Ok );
		Assert.AreEqual( "{\"ok\":true,\"data\":{}}", reply.Body );
	}

	[TestMethod]
	public void Failure_KeepsHostErrorCodeAndMessage()
	{
		var raw = NetworkStorageProxyEnvelope.Failure( NetworkStorageProxyEnvelope.DocumentKind, "NOT_FOUND", "404 Not Found" );
		var reply = NetworkStorageProxyEnvelope.Read( raw, NetworkStorageProxyEnvelope.DocumentKind );

		Assert.IsFalse( reply.Ok );
		Assert.AreEqual( "NOT_FOUND", reply.ErrorCode );
		Assert.AreEqual( "404 Not Found", reply.ErrorMessage );
	}

	[TestMethod]
	public void EmptyReply_IsProxyFailure()
	{
		// The old host sent an empty string on failure; the client must not report it as success.
		var reply = NetworkStorageProxyEnvelope.Read( "", NetworkStorageProxyEnvelope.EndpointKind );

		Assert.IsFalse( reply.Ok );
		Assert.AreEqual( "PROXY_FAILED", reply.ErrorCode );
	}

	[TestMethod]
	public void LegacyReply_PassesRawServerTextThrough()
	{
		// Hosts older than the envelope send raw server text without the marker.
		const string raw = "{\"ok\":false,\"error\":{\"code\":\"RATE_LIMITED\"}}";
		var reply = NetworkStorageProxyEnvelope.Read( raw, NetworkStorageProxyEnvelope.EndpointKind );

		Assert.IsTrue( reply.Ok );
		Assert.AreEqual( raw, reply.Body );
	}

	[TestMethod]
	public void NonJsonReply_PassesRawTextThrough()
	{
		var reply = NetworkStorageProxyEnvelope.Read( "not json", NetworkStorageProxyEnvelope.DocumentKind );

		Assert.IsTrue( reply.Ok );
		Assert.AreEqual( "not json", reply.Body );
	}

	[TestMethod]
	public void UnsupportedVersion_IsProxyFailure()
	{
		const string raw = "{\"nsProxyEnvelope\":2,\"kind\":\"endpoint\",\"ok\":true,\"body\":\"{}\"}";
		var reply = NetworkStorageProxyEnvelope.Read( raw, NetworkStorageProxyEnvelope.EndpointKind );

		Assert.IsFalse( reply.Ok );
		Assert.AreEqual( "PROXY_FAILED", reply.ErrorCode );
	}

	[TestMethod]
	public void KindMismatch_IsProxyFailure()
	{
		var raw = NetworkStorageProxyEnvelope.Success( NetworkStorageProxyEnvelope.DocumentKind, "{}" );
		var reply = NetworkStorageProxyEnvelope.Read( raw, NetworkStorageProxyEnvelope.EndpointKind );

		Assert.IsFalse( reply.Ok );
		Assert.AreEqual( "PROXY_FAILED", reply.ErrorCode );
	}

	[TestMethod]
	public void OkWithoutBody_IsFailure()
	{
		const string raw = "{\"nsProxyEnvelope\":1,\"kind\":\"document\",\"ok\":true,\"body\":null}";
		var reply = NetworkStorageProxyEnvelope.Read( raw, NetworkStorageProxyEnvelope.DocumentKind );

		Assert.IsFalse( reply.Ok );
		Assert.AreEqual( "PROXY_FAILED", reply.ErrorCode );
	}

	[TestMethod]
	public void SteamIdMatches_RequiresNonEmptyEqualIds()
	{
		Assert.IsFalse( NetworkStorageProxyComponent.SteamIdMatches( "", "" ) );
		Assert.IsFalse( NetworkStorageProxyComponent.SteamIdMatches( null, "76561198000000001" ) );
		Assert.IsFalse( NetworkStorageProxyComponent.SteamIdMatches( "76561198000000001", "76561198000000002" ) );
		Assert.IsTrue( NetworkStorageProxyComponent.SteamIdMatches( "76561198000000001", "76561198000000001" ) );
	}
}
