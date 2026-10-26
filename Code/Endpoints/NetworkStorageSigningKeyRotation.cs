using System;
using System.Globalization;
using System.Text.Json;

namespace Sandbox;

// Announced signing-key rotation. A server that replaces its signing key signs a statement with the key it replaces and
// carries the statements in the config's signed `signing.rotations`, oldest first. A client that trusts the old key follows
// the chain to the config's key. Expiry is read against the config's server-signed generatedAt, never the client clock.
public static partial class NetworkStorage
{
	// Longest chain a config may carry. A client that missed several rotations can still follow them in one config.
	private const int MaxSigningKeyRotations = 8;

	private const string RotationInvalidCode = "ENCRYPTION_SIGNING_KEY_ROTATION_INVALID";

	// Follows the statements from the trusted key to the key the config carries. Returns null when the chain reaches
	// `embedded`. With no statements the replacement is unannounced and is refused as ENCRYPTION_SIGNING_KEY_CHANGED.
	internal static SecurityConfigRefusal FollowSigningKeyRotations( JsonElement config, TrustedSigningKey trusted, TrustedSigningKey embedded )
	{
		var signing = config.TryGetProperty( "signing", out var signingProp ) ? signingProp : default;
		if ( signing.ValueKind != JsonValueKind.Object || !signing.TryGetProperty( "rotations", out var rotations ) || rotations.ValueKind != JsonValueKind.Array || rotations.GetArrayLength() == 0 )
			return new SecurityConfigRefusal( "ENCRYPTION_SIGNING_KEY_CHANGED", "security config signing key differs from the key trusted on first use and no rotation statement announces it; verify the server identity, then call NetworkStorage.ResetSigningKeyTrust() or pin securityKeyFingerprint" );

		if ( rotations.GetArrayLength() > MaxSigningKeyRotations )
			return RotationRefusal( $"{rotations.GetArrayLength()} statements exceed the limit of {MaxSigningKeyRotations}" );

		if ( !DateTimeOffset.TryParse( ReadString( config, "generatedAt" ), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var generatedAt ) )
			return RotationRefusal( "the security config has no valid generatedAt" );

		// A chain can start before the trusted key: a client that took K1 meets K0->K1 and K1->K2 when K1->K2 is added.
		// Statements before the trusted key are not on its path to the config key; they are skipped, not trusted.
		var current = trusted;
		var following = false;
		foreach ( var statement in rotations.EnumerateArray() )
		{
			if ( !following )
			{
				if ( !StatementFollows( statement, current ) )
					continue;

				following = true;
			}

			if ( !TryFollowRotation( statement, current, generatedAt, out var next, out var reason ) )
				return RotationRefusal( reason );
			current = next;
		}

		if ( !following )
			return RotationRefusal( "no statement follows the trusted key" );

		return current.Fingerprint == embedded.Fingerprint
			? null
			: RotationRefusal( "the statements do not end at the security config's signing key" );
	}

	// True when the statement is the one that leaves `current`.
	private static bool StatementFollows( JsonElement statement, TrustedSigningKey current )
	{
		return statement.ValueKind == JsonValueKind.Object
			&& string.Equals( NormalizeSecurityKeyFingerprint( ReadString( statement, "previousFingerprint" ) ), current.Fingerprint, StringComparison.Ordinal );
	}

	// Checks one statement against the key it must follow: its previous fingerprint, the next key it names, expiry at the
	// config's generatedAt, and the signature made by the previous key.
	private static bool TryFollowRotation( JsonElement statement, TrustedSigningKey current, DateTimeOffset generatedAt, out TrustedSigningKey next, out string reason )
	{
		next = null;
		reason = "";
		if ( statement.ValueKind != JsonValueKind.Object )
		{
			reason = "a statement is not an object";
			return false;
		}

		if ( !string.Equals( NormalizeSecurityKeyFingerprint( ReadString( statement, "previousFingerprint" ) ), current.Fingerprint, StringComparison.Ordinal ) )
		{
			reason = "a statement does not follow the trusted key";
			return false;
		}

		if ( !statement.TryGetProperty( "nextPublicKeyJwk", out var jwk ) || jwk.ValueKind != JsonValueKind.Object )
		{
			reason = "a statement has no nextPublicKeyJwk";
			return false;
		}

		var modulus = NetworkStorageRsaKey.NormalizeSigningModulus( NetworkStorageSecuritySignature.Base64UrlDecode( ReadString( jwk, "n" ) ) );
		var exponent = NetworkStorageSecuritySignature.Base64UrlDecode( ReadString( jwk, "e" ) );
		if ( modulus is null || exponent.Length == 0 )
		{
			reason = "a statement's nextPublicKeyJwk is not an RSA key of at least 1024 bits";
			return false;
		}

		var fingerprint = NetworkStorageRsaKey.Fingerprint( modulus, exponent );
		if ( NormalizeSecurityKeyFingerprint( ReadString( statement, "nextFingerprint" ) ) != fingerprint )
		{
			reason = "a statement's nextFingerprint does not match its nextPublicKeyJwk";
			return false;
		}

		if ( !DateTimeOffset.TryParse( ReadString( statement, "notAfter" ), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var notAfter ) || generatedAt > notAfter )
		{
			reason = "a statement has expired or has no valid notAfter";
			return false;
		}

		if ( !NetworkStorageSecuritySignature.VerifySigningKeyRotation( statement, current.Modulus, current.Exponent ) )
		{
			reason = "a statement's signature did not verify with the key it replaces";
			return false;
		}

		next = new TrustedSigningKey( fingerprint, modulus, exponent );
		return true;
	}

	private static SecurityConfigRefusal RotationRefusal( string reason )
	{
		return new SecurityConfigRefusal( RotationInvalidCode, $"signing key rotation statements are not valid: {reason}" );
	}
}
