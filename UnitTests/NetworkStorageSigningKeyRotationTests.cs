using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sandbox;

// An announced signing-key rotation moves a client's trust only through a chain of statements. Each statement is signed by
// the key it replaces, names the key it introduces, and is unexpired at the config's server-signed generatedAt.
[TestClass]
public class NetworkStorageSigningKeyRotationTests
{
	private const string Domain = "sboxcool.network-storage.signing-key-rotation.v1";
	private static readonly DateTimeOffset GeneratedAt = new( 2030, 1, 1, 0, 0, 0, TimeSpan.Zero );

	[TestMethod]
	public void AnnouncedRotationAcceptsSuccessorKey()
	{
		var a = new Signer();
		var b = new Signer();
		var rotations = new List<object> { Sealed( Link( a, b, GeneratedAt.AddDays( 1 ) ), a ) };
		var config = ConfigFor( b, Iso( GeneratedAt ), rotations );

		Assert.IsNull( NetworkStorage.FollowSigningKeyRotations( config, a.Trusted, b.Trusted ) );
		Assert.IsTrue( NetworkStorageSecuritySignature.VerifySecurityConfigSignature( config, b.Modulus, b.Exponent ) );
	}

	[TestMethod]
	public void UnannouncedReplacementIsRefusedAsSigningKeyChanged()
	{
		var a = new Signer();
		var b = new Signer();

		foreach ( var rotations in new List<List<object>> { null, new List<object>() } )
		{
			var refusal = NetworkStorage.FollowSigningKeyRotations( ConfigFor( b, Iso( GeneratedAt ), rotations ), a.Trusted, b.Trusted );
			Assert.IsNotNull( refusal );
			Assert.AreEqual( "ENCRYPTION_SIGNING_KEY_CHANGED", refusal.Code );
		}
	}

	[TestMethod]
	public void StatementSignedByWrongKeyIsRejected()
	{
		var a = new Signer();
		var b = new Signer();
		var c = new Signer();
		var rotations = new List<object> { Sealed( Link( a, b, GeneratedAt.AddDays( 1 ) ), c ) };

		AssertRotationInvalid( NetworkStorage.FollowSigningKeyRotations( ConfigFor( b, Iso( GeneratedAt ), rotations ), a.Trusted, b.Trusted ) );
	}

	[TestMethod]
	public void StatementSignedByReplacementKeyIsRejected()
	{
		var a = new Signer();
		var b = new Signer();
		var rotations = new List<object> { Sealed( Link( a, b, GeneratedAt.AddDays( 1 ) ), b ) };

		AssertRotationInvalid( NetworkStorage.FollowSigningKeyRotations( ConfigFor( b, Iso( GeneratedAt ), rotations ), a.Trusted, b.Trusted ) );
	}

	[TestMethod]
	public void TamperedStatementIsRejected()
	{
		var a = new Signer();
		var b = new Signer();
		var statement = Sealed( Link( a, b, GeneratedAt.AddDays( 1 ) ), a );
		statement["notAfter"] = Iso( GeneratedAt.AddDays( 365 ) );
		var rotations = new List<object> { statement };

		AssertRotationInvalid( NetworkStorage.FollowSigningKeyRotations( ConfigFor( b, Iso( GeneratedAt ), rotations ), a.Trusted, b.Trusted ) );
	}

	[TestMethod]
	public void ExpiredStatementIsRejectedAndBoundaryIsAccepted()
	{
		var a = new Signer();
		var b = new Signer();
		var expired = new List<object> { Sealed( Link( a, b, GeneratedAt.AddSeconds( -1 ) ), a ) };
		var boundary = new List<object> { Sealed( Link( a, b, GeneratedAt ), a ) };

		AssertRotationInvalid( NetworkStorage.FollowSigningKeyRotations( ConfigFor( b, Iso( GeneratedAt ), expired ), a.Trusted, b.Trusted ) );
		Assert.IsNull( NetworkStorage.FollowSigningKeyRotations( ConfigFor( b, Iso( GeneratedAt ), boundary ), a.Trusted, b.Trusted ) );
	}

	[TestMethod]
	public void ChainOfTwoRotationsIsFollowed()
	{
		var a = new Signer();
		var b = new Signer();
		var c = new Signer();
		var rotations = new List<object>
		{
			Sealed( Link( a, b, GeneratedAt.AddDays( 1 ) ), a ),
			Sealed( Link( b, c, GeneratedAt.AddDays( 1 ) ), b ),
		};

		Assert.IsNull( NetworkStorage.FollowSigningKeyRotations( ConfigFor( c, Iso( GeneratedAt ), rotations ), a.Trusted, c.Trusted ) );
	}

	[TestMethod]
	public void ChainThatStopsBeforeConfigKeyIsRejected()
	{
		var a = new Signer();
		var b = new Signer();
		var c = new Signer();
		var rotations = new List<object> { Sealed( Link( a, b, GeneratedAt.AddDays( 1 ) ), a ) };

		AssertRotationInvalid( NetworkStorage.FollowSigningKeyRotations( ConfigFor( c, Iso( GeneratedAt ), rotations ), a.Trusted, c.Trusted ) );
	}

	[TestMethod]
	public void ChainLimitIsEightStatements()
	{
		var keys = new[] { new Signer(), new Signer(), new Signer() };

		var eight = new List<object>();
		for ( var i = 0; i < 8; i++ )
			eight.Add( Sealed( Link( keys[i % 3], keys[( i + 1 ) % 3], GeneratedAt.AddDays( 1 ) ), keys[i % 3] ) );
		Assert.IsNull( NetworkStorage.FollowSigningKeyRotations( ConfigFor( keys[2], Iso( GeneratedAt ), eight ), keys[0].Trusted, keys[2].Trusted ) );

		var nine = new List<object>();
		for ( var i = 0; i < 9; i++ )
			nine.Add( Sealed( Link( keys[i % 3], keys[( i + 1 ) % 3], GeneratedAt.AddDays( 1 ) ), keys[i % 3] ) );
		AssertRotationInvalid( NetworkStorage.FollowSigningKeyRotations( ConfigFor( keys[0], Iso( GeneratedAt ), nine ), keys[0].Trusted, keys[0].Trusted ) );
	}

	[TestMethod]
	public void StatementNamingAnotherPreviousKeyIsRejected()
	{
		var a = new Signer();
		var b = new Signer();
		var c = new Signer();
		var rotations = new List<object> { Sealed( Link( c, b, GeneratedAt.AddDays( 1 ) ), c ) };

		AssertRotationInvalid( NetworkStorage.FollowSigningKeyRotations( ConfigFor( b, Iso( GeneratedAt ), rotations ), a.Trusted, b.Trusted ) );
	}

	[TestMethod]
	public void NextFingerprintMustMatchNextKey()
	{
		var a = new Signer();
		var b = new Signer();
		var c = new Signer();
		var link = Link( a, b, GeneratedAt.AddDays( 1 ) );
		link["nextFingerprint"] = c.Fingerprint;
		var rotations = new List<object> { Sealed( link, a ) };

		AssertRotationInvalid( NetworkStorage.FollowSigningKeyRotations( ConfigFor( b, Iso( GeneratedAt ), rotations ), a.Trusted, b.Trusted ) );
	}

	[TestMethod]
	public void SignatureDomainIsNotOptional()
	{
		var a = new Signer();
		var b = new Signer();
		var link = Link( a, b, GeneratedAt.AddDays( 1 ) );
		link["signature"] = B64( a.Rsa.SignData( Encoding.UTF8.GetBytes( JsonSerializer.Serialize( link ) ), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1 ) );
		var rotations = new List<object> { link };

		AssertRotationInvalid( NetworkStorage.FollowSigningKeyRotations( ConfigFor( b, Iso( GeneratedAt ), rotations ), a.Trusted, b.Trusted ) );
	}

	[TestMethod]
	public void ConfigWithoutGeneratedAtCannotJudgeExpiry()
	{
		var a = new Signer();
		var b = new Signer();
		var rotations = new List<object> { Sealed( Link( a, b, GeneratedAt.AddDays( 1 ) ), a ) };

		AssertRotationInvalid( NetworkStorage.FollowSigningKeyRotations( ConfigFor( b, null, rotations ), a.Trusted, b.Trusted ) );
	}

	private static void AssertRotationInvalid( NetworkStorage.SecurityConfigRefusal refusal )
	{
		Assert.IsNotNull( refusal );
		Assert.AreEqual( "ENCRYPTION_SIGNING_KEY_ROTATION_INVALID", refusal.Code );
	}

	// Canonical form matches the library: keys sorted ordinally, compact, the top-level signature left out.
	private static SortedDictionary<string, object> Link( Signer previous, Signer next, DateTimeOffset notAfter ) => new( StringComparer.Ordinal )
	{
		["issuedAt"] = Iso( GeneratedAt ),
		["nextFingerprint"] = next.Fingerprint,
		["nextPublicKeyJwk"] = next.Jwk,
		["notAfter"] = Iso( notAfter ),
		["previousFingerprint"] = previous.Fingerprint,
	};

	private static SortedDictionary<string, object> Sealed( SortedDictionary<string, object> statement, Signer signer )
	{
		var message = Encoding.UTF8.GetBytes( Domain + "\n" + JsonSerializer.Serialize( statement ) );
		statement["signature"] = B64( signer.Rsa.SignData( message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1 ) );
		return statement;
	}

	private static JsonElement ConfigFor( Signer key, string generatedAt, List<object> rotations )
	{
		var signing = new SortedDictionary<string, object>( StringComparer.Ordinal )
		{
			["algorithm"] = "rsa-sha256",
			["keyId"] = key.Fingerprint[..16],
			["publicKeyJwk"] = key.Jwk,
		};
		if ( rotations is not null )
			signing["rotations"] = rotations;

		var config = new SortedDictionary<string, object>( StringComparer.Ordinal )
		{
			["projectId"] = "proj-test",
			["signing"] = signing,
			["version"] = 1,
		};
		if ( generatedAt is not null )
			config["generatedAt"] = generatedAt;

		config["signature"] = B64( key.Rsa.SignData( Encoding.UTF8.GetBytes( JsonSerializer.Serialize( config ) ), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1 ) );
		return JsonDocument.Parse( JsonSerializer.Serialize( config ) ).RootElement;
	}

	private static string Iso( DateTimeOffset value ) => value.UtcDateTime.ToString( "O", CultureInfo.InvariantCulture );

	private static string B64( byte[] bytes ) => Convert.ToBase64String( bytes ).TrimEnd( '=' ).Replace( '+', '-' ).Replace( '/', '_' );

	private sealed class Signer
	{
		public Signer()
		{
			Rsa = RSA.Create( 2048 );
			var parameters = Rsa.ExportParameters( false );
			Modulus = NetworkStorageRsaKey.NormalizeSigningModulus( parameters.Modulus );
			Exponent = parameters.Exponent;
			Fingerprint = NetworkStorageRsaKey.Fingerprint( Modulus, Exponent );
		}

		public RSA Rsa { get; }
		public byte[] Modulus { get; }
		public byte[] Exponent { get; }
		public string Fingerprint { get; }
		public NetworkStorage.TrustedSigningKey Trusted => new( Fingerprint, Modulus, Exponent );

		public SortedDictionary<string, object> Jwk => new( StringComparer.Ordinal )
		{
			["e"] = B64( Exponent ),
			["kty"] = "RSA",
			["n"] = B64( Modulus ),
		};
	}
}
