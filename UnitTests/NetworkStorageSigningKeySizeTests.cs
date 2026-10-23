using System;
using Sandbox;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// A server may sign security configs with any RSA size from 1024 bits up. Such a key must be read as a signing key
// and verify its config, and the trust store must be able to read it back. The encryption-key rule stays RSA-2048 only.
[TestClass]
public class NetworkStorageSigningKeySizeTests
{
	[TestMethod]
	public void LargerSigningKeyIsReadAndVerifiesItsConfig()
	{
		using var rsa = RSA.Create( 3072 );
		var parameters = rsa.ExportParameters( false );
		var modulus = NetworkStorageRsaKey.NormalizeSigningModulus( parameters.Modulus );

		Assert.IsNotNull( modulus );
		Assert.IsNull( NetworkStorageRsaKey.NormalizeModulus( parameters.Modulus ) );

		var unsigned = "{\"projectId\":\"proj-test\",\"signing\":{\"algorithm\":\"rsa-sha256\",\"keyId\":\"abc\",\"publicKeyJwk\":{\"e\":\"" +
			B64Url( parameters.Exponent ) + "\",\"kty\":\"RSA\",\"n\":\"" + B64Url( modulus ) + "\"}},\"version\":1}";
		var signature = rsa.SignData( Encoding.UTF8.GetBytes( unsigned ), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1 );
		var signed = unsigned[..^1] + ",\"signature\":\"" + B64Url( signature ) + "\"}";
		var root = JsonDocument.Parse( signed ).RootElement;

		Assert.IsTrue( NetworkStorageSecuritySignature.TryReadSigningKey( root, out var embeddedModulus, out var embeddedExponent ) );
		CollectionAssert.AreEqual( modulus, embeddedModulus );
		CollectionAssert.AreEqual( parameters.Exponent, embeddedExponent );
		Assert.IsTrue( NetworkStorageSecuritySignature.VerifySecurityConfigSignature( root, embeddedModulus, embeddedExponent ) );
	}

	[TestMethod]
	public void SigningModulusBelowRsa1024IsRefused()
	{
		var justBelow = new byte[NetworkStorageRsaKey.MinSigningModulusBytes - 1];
		justBelow[0] = 0x80;
		var exactly = new byte[NetworkStorageRsaKey.MinSigningModulusBytes];
		exactly[0] = 0x80;

		Assert.IsNull( NetworkStorageRsaKey.NormalizeSigningModulus( justBelow ) );
		Assert.IsNotNull( NetworkStorageRsaKey.NormalizeSigningModulus( exactly ) );
	}

	private static string B64Url( byte[] bytes )
		=> Convert.ToBase64String( bytes ).TrimEnd( '=' ).Replace( '+', '-' ).Replace( '/', '_' );
}
