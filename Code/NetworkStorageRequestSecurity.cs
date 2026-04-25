using System;
using System.Globalization;
using System.Text;

namespace Sandbox;

/// <summary>
/// Frontend helpers for request security metadata used by encrypted Network Storage calls.
/// </summary>
public static partial class NetworkStorage
{
	private const int MinimumEncryptedRequestRandomLength = 6;
	private const int DefaultEncryptedRequestRandomLength = 16;

	/// <summary>
	/// Creates a one-use encrypted request id in "{unixSeconds}_{random}" format.
	/// The backend must reject stale or repeated ids before running endpoint logic.
	/// </summary>
	public static string CreateEncryptedRequestId()
	{
		return CreateEncryptedRequestId( DateTimeOffset.UtcNow, DefaultEncryptedRequestRandomLength );
	}

	/// <summary>
	/// Validates and parses an encrypted request id in "{unixSeconds}_{random6plus}" format.
	/// </summary>
	public static bool TryParseEncryptedRequestId( string requestId, out long unixSeconds, out string random )
	{
		unixSeconds = 0;
		random = null;

		if ( string.IsNullOrWhiteSpace( requestId ) )
			return false;

		var separatorIndex = requestId.IndexOf( '_' );
		if ( separatorIndex <= 0 || separatorIndex != requestId.LastIndexOf( '_' ) || separatorIndex == requestId.Length - 1 )
			return false;

		var unixPart = requestId[..separatorIndex];
		var randomPart = requestId[(separatorIndex + 1)..];
		if ( randomPart.Length < MinimumEncryptedRequestRandomLength )
			return false;

		foreach ( var ch in unixPart )
		{
			if ( ch < '0' || ch > '9' )
				return false;
		}

		foreach ( var ch in randomPart )
		{
			if ( !IsAsciiAlphaNumeric( ch ) )
				return false;
		}

		if ( !long.TryParse( unixPart, NumberStyles.None, CultureInfo.InvariantCulture, out unixSeconds ) || unixSeconds <= 0 )
			return false;

		random = randomPart;
		return true;
	}

	internal static string CreateEncryptedRequestId( DateTimeOffset now, int randomLength )
	{
		if ( randomLength < MinimumEncryptedRequestRandomLength )
			throw new ArgumentOutOfRangeException( nameof( randomLength ), $"Encrypted request ids need at least {MinimumEncryptedRequestRandomLength} random characters." );

		return $"{now.ToUnixTimeSeconds()}_{CreateRandomAlphaNumeric( randomLength )}";
	}

	private static string CreateRandomAlphaNumeric( int length )
	{
		var value = Guid.NewGuid().ToString( "N" );
		if ( length <= value.Length )
			return value[..length];

		var sb = new StringBuilder( length );
		while ( sb.Length < length )
			sb.Append( Guid.NewGuid().ToString( "N" ) );

		return sb.ToString( 0, length );
	}

	private static bool IsAsciiAlphaNumeric( char ch )
	{
		return (ch >= '0' && ch <= '9')
			|| (ch >= 'A' && ch <= 'Z')
			|| (ch >= 'a' && ch <= 'z');
	}
}
