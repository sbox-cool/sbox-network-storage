using Sandbox;

[TestClass]
public class NetworkStorageRequestSecurityTests
{
	[TestMethod]
	public void CreateEncryptedRequestId_ProducesParseableUniqueId()
	{
		var first = NetworkStorage.CreateEncryptedRequestId();
		var second = NetworkStorage.CreateEncryptedRequestId();

		Assert.AreNotEqual( first, second );
		Assert.IsTrue( NetworkStorage.TryParseEncryptedRequestId( first, out var unixSeconds, out var random ) );
		Assert.IsTrue( unixSeconds > 0 );
		Assert.IsTrue( random.Length >= 6 );
		Assert.IsTrue( first.StartsWith( $"{unixSeconds}_" ) );
	}

	[TestMethod]
	public void TryParseEncryptedRequestId_RejectsMalformedIds()
	{
		Assert.IsFalse( NetworkStorage.TryParseEncryptedRequestId( "", out _, out _ ) );
		Assert.IsFalse( NetworkStorage.TryParseEncryptedRequestId( "abc_abcdef", out _, out _ ) );
		Assert.IsFalse( NetworkStorage.TryParseEncryptedRequestId( "12345_short", out _, out _ ) );
		Assert.IsFalse( NetworkStorage.TryParseEncryptedRequestId( "12345_abcd!", out _, out _ ) );
		Assert.IsFalse( NetworkStorage.TryParseEncryptedRequestId( "12345_abcdef_extra", out _, out _ ) );
	}

	[TestMethod]
	public void IsSecurityConfigMismatchCode_DetectsServerSecurityMismatch()
	{
		Assert.IsTrue( NetworkStorage.IsSecurityConfigMismatchCode( "SECURITY_ENCRYPTION_REQUIRED" ) );
		Assert.IsTrue( NetworkStorage.IsSecurityConfigMismatchCode( "security_session_disabled" ) );
		Assert.IsFalse( NetworkStorage.IsSecurityConfigMismatchCode( "SBOX_AUTH_FAILED" ) );
		Assert.IsFalse( NetworkStorage.IsSecurityConfigMismatchCode( "" ) );
	}
}
