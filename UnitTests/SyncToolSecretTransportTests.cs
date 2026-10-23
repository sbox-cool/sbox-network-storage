using Sandbox;

[TestClass]
public class SyncToolSecretTransportTests
{
	[TestMethod]
	public void SecretIsSentToHttpsAndLoopbackOnly()
	{
		Assert.IsTrue( SyncToolConfig.IsSecretSafeBaseUrl( "https://api.sboxcool.com" ) );
		Assert.IsTrue( SyncToolConfig.IsSecretSafeBaseUrl( "HTTPS://api.sboxcool.com" ) );
		Assert.IsTrue( SyncToolConfig.IsSecretSafeBaseUrl( "http://localhost:8080" ) );
		Assert.IsTrue( SyncToolConfig.IsSecretSafeBaseUrl( "http://127.0.0.1:5000" ) );
		Assert.IsTrue( SyncToolConfig.IsSecretSafeBaseUrl( "http://[::1]:5000" ) );
	}

	[TestMethod]
	public void SecretIsRefusedForPlainHttpNonLoopbackAndMalformedUrls()
	{
		Assert.IsFalse( SyncToolConfig.IsSecretSafeBaseUrl( "http://api.sboxcool.com" ) );
		Assert.IsFalse( SyncToolConfig.IsSecretSafeBaseUrl( "http://192.168.1.10:8080" ) );
		Assert.IsFalse( SyncToolConfig.IsSecretSafeBaseUrl( "ftp://api.sboxcool.com" ) );
		Assert.IsFalse( SyncToolConfig.IsSecretSafeBaseUrl( "api.sboxcool.com" ) );
		Assert.IsFalse( SyncToolConfig.IsSecretSafeBaseUrl( "" ) );
		Assert.IsFalse( SyncToolConfig.IsSecretSafeBaseUrl( null ) );
	}
}
