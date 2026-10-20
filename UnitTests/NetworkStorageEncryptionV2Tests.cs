using Sandbox;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Golden vectors come from the server's encrypted-request-v2-vectors.json. The .NET crypto classes below are
// test oracles only; the library itself uses none of them.
[TestClass]
public class NetworkStorageEncryptionV2Tests
{
	private const string Algorithm = "rsa-oaep-256+aes-256-gcm";

	[TestMethod]
	public void ServerKey_FingerprintAndSpkiMatchGoldenVector()
	{
		var serverKey = Vectors().GetProperty( "serverKey" );
		using var rsa = RSA.Create();
		rsa.ImportFromPem( serverKey.GetProperty( "publicKeyPem" ).GetString() );
		var parameters = rsa.ExportParameters( false );
		var modulus = NetworkStorageRsaKey.NormalizeModulus( parameters.Modulus );

		Assert.IsNotNull( modulus );
		CollectionAssert.AreEqual( rsa.ExportSubjectPublicKeyInfo(), NetworkStorageRsaKey.SubjectPublicKeyInfo( modulus, parameters.Exponent ) );

		var fingerprint = NetworkStorageRsaKey.Fingerprint( modulus, parameters.Exponent );
		Assert.AreEqual( serverKey.GetProperty( "fingerprint" ).GetString(), fingerprint );
		Assert.AreEqual( serverKey.GetProperty( "keyId" ).GetString(), fingerprint[..32] );
	}

	[TestMethod]
	public void ValidSteamRequest_WrappedKeyUnwrapsWithGoldenPrivateKey()
	{
		var request = ByName( Vectors().GetProperty( "requests" ), "valid-steam-request" );
		using var rsa = RSA.Create();
		rsa.ImportFromPem( Vectors().GetProperty( "serverKey" ).GetProperty( "privateKeyPem" ).GetString() );

		var aesKey = rsa.Decrypt( Oracle( request.GetProperty( "envelope" ).GetProperty( "wrappedKey" ).GetString() ), RSAEncryptionPadding.OaepSHA256 );
		CollectionAssert.AreEqual( Oracle( request.GetProperty( "aesKey" ).GetString() ), aesKey );
	}

	[TestMethod]
	public void ValidSteamRequest_AadBindsGoldenFieldsAndOpensPayload()
	{
		var request = ByName( Vectors().GetProperty( "requests" ), "valid-steam-request" );
		var envelope = request.GetProperty( "envelope" );
		var aad = NetworkStorage.EncryptedRequestAadV2(
			envelope.GetProperty( "projectId" ).GetString(),
			envelope.GetProperty( "endpointSlug" ).GetString(),
			"steam",
			envelope.GetProperty( "steamId" ).GetString(),
			envelope.GetProperty( "nonce" ).GetString(),
			envelope.GetProperty( "keyId" ).GetString() );

		Assert.AreEqual(
			"{\"algorithm\":\"rsa-oaep-256+aes-256-gcm\",\"endpointSlug\":\"player-coins-v2\",\"identity\":\"76561198012345678\",\"identityType\":\"steam\",\"keyId\":\"f753feefa9e7837fa0495b6cc487b0cc\",\"nonce\":\"JFunwRbuRxS_nYkX\",\"projectId\":\"proj-enc-v2-fixture\",\"version\":\"2\"}",
			Encoding.UTF8.GetString( aad ) );

		var plaintext = OpenAesGcm(
			Oracle( request.GetProperty( "aesKey" ).GetString() ),
			Oracle( envelope.GetProperty( "iv" ).GetString() ),
			Oracle( envelope.GetProperty( "encryptedPayload" ).GetString() ),
			Oracle( envelope.GetProperty( "tag" ).GetString() ),
			aad );
		var payload = JsonDocument.Parse( plaintext ).RootElement;
		Assert.AreEqual( "ruby", payload.GetProperty( "item" ).GetString() );
		Assert.AreEqual( 42, payload.GetProperty( "coins" ).GetInt32() );
	}

	[TestMethod]
	public void OaepSha256Encrypt_UnwrapsWithDotNetRsa()
	{
		using var rsa = RSA.Create( 2048 );
		var parameters = rsa.ExportParameters( false );
		var modulus = NetworkStorageRsaKey.NormalizeModulus( parameters.Modulus );
		var message = RandomNumberGenerator.GetBytes( 32 );

		var wrapped = NetworkStorageRsaKey.OaepSha256Encrypt( message, modulus, parameters.Exponent, RandomNumberGenerator.GetBytes( 32 ) );

		Assert.AreEqual( 256, wrapped.Length );
		CollectionAssert.AreEqual( message, rsa.Decrypt( wrapped, RSAEncryptionPadding.OaepSHA256 ) );
	}

	[TestMethod]
	public void SealRequestV2_EnvelopeOpensWithIndependentOracle()
	{
		using var rsa = RSA.Create( 2048 );
		var key = GeneratedKey( rsa );
		var payload = new Dictionary<string, object> { ["_endpointSlug"] = "player-coins-v2", ["coins"] = 42, ["item"] = "ruby" };

		var envelope = NetworkStorage.SealRequestV2( key, "proj-test", "player-coins-v2", "steam", "76561198000000000", payload, out var context );

		Assert.AreEqual( "2", envelope["version"] );
		Assert.AreEqual( Algorithm, envelope["algorithm"] );
		Assert.AreEqual( "proj-test", envelope["projectId"] );
		Assert.AreEqual( key.KeyId, envelope["keyId"] );
		Assert.AreEqual( "player-coins-v2", envelope["endpointSlug"] );
		Assert.AreEqual( "76561198000000000", envelope["steamId"] );
		Assert.IsFalse( envelope.ContainsKey( "sessionId" ) );

		var nonce = (string)envelope["nonce"];
		Assert.AreEqual( nonce, context.Nonce );
		Assert.AreEqual( key.KeyId, context.KeyId );

		var aesKey = rsa.Decrypt( Oracle( (string)envelope["wrappedKey"] ), RSAEncryptionPadding.OaepSHA256 );
		Assert.AreEqual( 32, aesKey.Length );
		CollectionAssert.AreEqual( aesKey, context.AesKey );

		var aad = Encoding.UTF8.GetBytes(
			"{\"algorithm\":\"rsa-oaep-256+aes-256-gcm\",\"endpointSlug\":\"player-coins-v2\",\"identity\":\"76561198000000000\",\"identityType\":\"steam\",\"keyId\":\"" + key.KeyId +
			"\",\"nonce\":\"" + nonce + "\",\"projectId\":\"proj-test\",\"version\":\"2\"}" );
		var plaintext = OpenAesGcm(
			aesKey,
			Oracle( (string)envelope["iv"] ),
			Oracle( (string)envelope["encryptedPayload"] ),
			Oracle( (string)envelope["tag"] ),
			aad );
		Assert.AreEqual( JsonSerializer.Serialize( payload ), Encoding.UTF8.GetString( plaintext ) );
	}

	[TestMethod]
	public void SealRequestV2_SessionIdentityIsSentAsSessionId()
	{
		using var rsa = RSA.Create( 2048 );

		var envelope = NetworkStorage.SealRequestV2( GeneratedKey( rsa ), "proj-test", "", "session", "sess-123", new Dictionary<string, object> { ["ok"] = true }, out _ );

		Assert.AreEqual( "sess-123", envelope["sessionId"] );
		Assert.IsFalse( envelope.ContainsKey( "steamId" ) );
		Assert.IsFalse( envelope.ContainsKey( "endpointSlug" ) );
	}

	[TestMethod]
	public void UnsealEndpointResponse_OpensGoldenSeal()
	{
		var response = ByName( Vectors().GetProperty( "responses" ), "response-seal" );

		var body = NetworkStorage.UnsealEndpointResponse( Context( response ), "player-coins-v2", response.GetProperty( "sealed" ).GetRawText() );

		Assert.AreEqual( response.GetProperty( "bodyJson" ).GetString(), body );
	}

	[TestMethod]
	public void UnsealEndpointResponse_RejectsTamperedOrMisboundSeal()
	{
		var response = ByName( Vectors().GetProperty( "responses" ), "response-seal" );
		var seal = response.GetProperty( "sealed" );
		var context = Context( response );

		var tampered = JsonSerializer.Serialize( new Dictionary<string, object>
		{
			["encrypted"] = true,
			["v"] = 2,
			["iv"] = seal.GetProperty( "iv" ).GetString(),
			["tag"] = seal.GetProperty( "tag" ).GetString(),
			["payload"] = Tamper( seal.GetProperty( "payload" ).GetString() )
		} );
		Assert.IsNull( NetworkStorage.UnsealEndpointResponse( context, "", tampered ) );

		var misbound = new NetworkStorage.EncryptedResponseContext( context.AesKey, "AAAAAAAAAAAAAAAA", context.KeyId );
		Assert.IsNull( NetworkStorage.UnsealEndpointResponse( misbound, "", seal.GetRawText() ) );
	}

	[TestMethod]
	public void UnsealEndpointResponse_PassesPlainBodiesThrough()
	{
		var context = new NetworkStorage.EncryptedResponseContext( new byte[32], "AAAAAAAAAAAAAAAA", "f753feefa9e7837fa0495b6cc487b0cc" );

		Assert.AreEqual( "{\"ok\":true}", NetworkStorage.UnsealEndpointResponse( context, "", "{\"ok\":true}" ) );
		Assert.AreEqual( "not json", NetworkStorage.UnsealEndpointResponse( context, "", "not json" ) );
	}

	[TestMethod]
	public void TryRelaySealedResponse_RelaysSealsOnly()
	{
		var seal = ByName( Vectors().GetProperty( "responses" ), "response-seal" ).GetProperty( "sealed" );

		Assert.IsTrue( NetworkStorage.TryRelaySealedResponse( seal.GetRawText(), out var relayed ) );
		Assert.AreEqual( seal.GetProperty( "iv" ).GetString(), relayed.Value.GetProperty( "iv" ).GetString() );
		Assert.IsFalse( NetworkStorage.TryRelaySealedResponse( "{\"ok\":true}", out _ ) );
		Assert.IsFalse( NetworkStorage.TryRelaySealedResponse( "nope", out _ ) );
	}

	[TestMethod]
	public void TryReadEncryptionKey_AcceptsConsistentBlockOnly()
	{
		using var rsa = RSA.Create( 2048 );
		var parameters = rsa.ExportParameters( false );
		var modulus = NetworkStorageRsaKey.NormalizeModulus( parameters.Modulus );
		var fingerprint = NetworkStorageRsaKey.Fingerprint( modulus, parameters.Exponent );
		var keyId = fingerprint[..32];

		Assert.IsTrue( NetworkStorage.TryReadEncryptionKey( KeyBlock( keyId, fingerprint, Algorithm, modulus, parameters.Exponent ), out var key, out var error ), error );
		Assert.AreEqual( keyId, key.KeyId );
		Assert.AreEqual( fingerprint, key.Fingerprint );

		var otherFingerprint = (fingerprint[0] == '0' ? "1" : "0") + fingerprint[1..];
		Assert.IsFalse( NetworkStorage.TryReadEncryptionKey( KeyBlock( keyId, otherFingerprint, Algorithm, modulus, parameters.Exponent ), out _, out _ ) );
		Assert.IsFalse( NetworkStorage.TryReadEncryptionKey( KeyBlock( new string( '0', 32 ), fingerprint, Algorithm, modulus, parameters.Exponent ), out _, out _ ) );
		Assert.IsFalse( NetworkStorage.TryReadEncryptionKey( KeyBlock( keyId, fingerprint, "rsa-pkcs1", modulus, parameters.Exponent ), out _, out _ ) );
		Assert.IsFalse( NetworkStorage.TryReadEncryptionKey( KeyBlock( keyId, fingerprint, Algorithm, modulus[..128], parameters.Exponent ), out _, out _ ) );
	}

	[TestMethod]
	public void NormalizeSecurityKeyFingerprint_IgnoresPastedSeparators()
	{
		Assert.AreEqual( "f753feef7e", NetworkStorage.NormalizeSecurityKeyFingerprint( " F7:53:FE\tEF 7E\n" ) );
		Assert.AreEqual( "", NetworkStorage.NormalizeSecurityKeyFingerprint( null ) );
	}

	[TestMethod]
	public void SecureRandom_ReturnsRequestedLengthAndVaries()
	{
		Assert.AreEqual( 0, NetworkStorageSecureRandom.Bytes( 0 ).Length );
		Assert.AreEqual( 12, NetworkStorageSecureRandom.Bytes( 12 ).Length );
		Assert.AreEqual( 70, NetworkStorageSecureRandom.Bytes( 70 ).Length );
		CollectionAssert.AreNotEqual( NetworkStorageSecureRandom.Bytes( 32 ), NetworkStorageSecureRandom.Bytes( 32 ) );
	}

	[TestMethod]
	public void VerifySecurityConfigSignature_AcceptsOwnKeyAndRejectsTampering()
	{
		using var rsa = RSA.Create( 2048 );
		var parameters = rsa.ExportParameters( false );
		var modulus = NetworkStorageRsaKey.NormalizeModulus( parameters.Modulus );

		// Keys are already in stable (sorted, compact) order, so this text is the canonical form the verifier hashes.
		var unsigned = "{\"projectId\":\"proj-test\",\"signing\":{\"algorithm\":\"rsa-sha256\",\"keyId\":\"abc\",\"publicKeyJwk\":{\"e\":\"" +
			B64Url( parameters.Exponent ) + "\",\"kty\":\"RSA\",\"n\":\"" + B64Url( modulus ) + "\"}},\"version\":1}";
		var signature = rsa.SignData( Encoding.UTF8.GetBytes( unsigned ), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1 );
		var signed = unsigned[..^1] + ",\"signature\":\"" + B64Url( signature ) + "\"}";
		var root = JsonDocument.Parse( signed ).RootElement;

		Assert.IsTrue( NetworkStorageSecuritySignature.TryReadSigningKey( root, out var embeddedModulus, out var embeddedExponent ) );
		CollectionAssert.AreEqual( modulus, embeddedModulus );
		CollectionAssert.AreEqual( parameters.Exponent, embeddedExponent );
		Assert.IsTrue( NetworkStorageSecuritySignature.VerifySecurityConfigSignature( root, modulus, parameters.Exponent ) );

		var tampered = JsonDocument.Parse( signed.Replace( "proj-test", "proj-evil" ) ).RootElement;
		Assert.IsFalse( NetworkStorageSecuritySignature.VerifySecurityConfigSignature( tampered, modulus, parameters.Exponent ) );
	}

	// Pulls the server key vector's request or response by its name field.
	private static JsonElement ByName( JsonElement array, string name )
		=> array.EnumerateArray().First( item => item.GetProperty( "name" ).GetString() == name );

	private static JsonElement Vectors()
	{
		using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream( "encrypted-request-v2-vectors.json" );
		Assert.IsNotNull( stream, "encrypted-request-v2-vectors.json is not embedded in the unit test assembly." );
		using var reader = new StreamReader( stream );
		return JsonDocument.Parse( reader.ReadToEnd() ).RootElement.Clone();
	}

	private static NetworkStorage.EncryptedResponseContext Context( JsonElement response )
		=> new NetworkStorage.EncryptedResponseContext( Oracle( response.GetProperty( "aesKey" ).GetString() ), response.GetProperty( "nonce" ).GetString(), response.GetProperty( "keyId" ).GetString() );

	private static NetworkStorage.RuntimeEncryptionKey GeneratedKey( RSA rsa )
	{
		var parameters = rsa.ExportParameters( false );
		var modulus = NetworkStorageRsaKey.NormalizeModulus( parameters.Modulus );
		var fingerprint = NetworkStorageRsaKey.Fingerprint( modulus, parameters.Exponent );
		return new NetworkStorage.RuntimeEncryptionKey( fingerprint[..32], fingerprint, modulus, parameters.Exponent );
	}

	private static JsonElement KeyBlock( string keyId, string fingerprint, string algorithm, byte[] modulus, byte[] exponent )
	{
		var json = JsonSerializer.Serialize( new Dictionary<string, object>
		{
			["algorithm"] = algorithm,
			["keyId"] = keyId,
			["fingerprint"] = fingerprint,
			["required"] = true,
			["publicKeyJwk"] = new Dictionary<string, string> { ["kty"] = "RSA", ["n"] = B64Url( modulus ), ["e"] = B64Url( exponent ) }
		} );
		return JsonDocument.Parse( json ).RootElement.Clone();
	}

	private static byte[] OpenAesGcm( byte[] key, byte[] iv, byte[] ciphertext, byte[] tag, byte[] aad )
	{
		var plaintext = new byte[ciphertext.Length];
		using var aes = new AesGcm( key, 16 );
		aes.Decrypt( iv, ciphertext, tag, plaintext, aad );
		return plaintext;
	}

	// Flips the first base64url character, which changes the ciphertext without making it undecodable.
	private static string Tamper( string value ) => (value[0] == 'A' ? "B" : "A") + value[1..];

	private static string B64Url( byte[] bytes ) => Convert.ToBase64String( bytes ).TrimEnd( '=' ).Replace( '+', '-' ).Replace( '/', '_' );

	private static byte[] Oracle( string value )
	{
		var padded = value.Replace( '-', '+' ).Replace( '_', '/' );
		padded = padded.PadRight( padded.Length + (4 - padded.Length % 4) % 4, '=' );
		return Convert.FromBase64String( padded );
	}
}
