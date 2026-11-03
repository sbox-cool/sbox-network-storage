using System.Text.Json;

namespace Sandbox;

/// <summary>
/// Reply format for proxied requests. The host wraps every reply, so a requesting client can tell a relayed server
/// reply from a host failure and keeps the host's error code and message. Replies without the marker are treated as
/// raw server text from an older host.
/// </summary>
internal static class NetworkStorageProxyEnvelope
{
	internal const int Version = 1;
	internal const string EndpointKind = "endpoint";
	internal const string DocumentKind = "document";

	private const string Marker = "nsProxyEnvelope";

	/// <summary>A decoded host reply. <see cref="Body"/> holds the relayed server text when <see cref="Ok"/> is true.</summary>
	internal readonly record struct HostReply( string Kind, bool Ok, string Body, string ErrorCode, string ErrorMessage );

	internal static string Success( string kind, string body )
		=> Serialize( kind, true, body, null, null );

	internal static string Failure( string kind, string errorCode, string errorMessage )
		=> Serialize( kind, false, null, errorCode, errorMessage );

	/// <summary>Decodes a host reply. Never throws; malformed or empty replies become failures.</summary>
	internal static HostReply Read( string raw, string expectedKind )
	{
		// An empty reply means the host failed before it could answer.
		if ( string.IsNullOrWhiteSpace( raw ) )
			return Fail( expectedKind, "PROXY_FAILED", "Host returned no result." );

		JsonDocument document;
		try
		{
			document = JsonDocument.Parse( raw );
		}
		catch ( JsonException )
		{
			// Not JSON: pass it through so the client reports it the same way as any other server text.
			return new HostReply( expectedKind, true, raw, null, null );
		}

		using ( document )
		{
			var root = document.RootElement;
			if ( root.ValueKind != JsonValueKind.Object || !root.TryGetProperty( Marker, out var version ) )
				return new HostReply( expectedKind, true, raw, null, null );

			if ( version.ValueKind != JsonValueKind.Number || !version.TryGetInt32( out var number ) || number != Version )
				return Fail( expectedKind, "PROXY_FAILED", "Host replied with an unsupported proxy envelope version." );

			if ( ReadString( root, "kind" ) != expectedKind )
				return Fail( expectedKind, "PROXY_FAILED", "Host replied with an unexpected envelope kind." );

			var ok = root.TryGetProperty( "ok", out var okValue ) && okValue.ValueKind == JsonValueKind.True;
			var body = ReadString( root, "body" );
			if ( ok && body is not null )
				return new HostReply( expectedKind, true, body, null, null );

			return Fail(
				expectedKind,
				ReadString( root, "errorCode" ) ?? "PROXY_FAILED",
				ReadString( root, "errorMessage" ) ?? "Host reply failed." );
		}
	}

	private static HostReply Fail( string kind, string errorCode, string errorMessage )
		=> new( kind, false, null, errorCode, errorMessage );

	private static string ReadString( JsonElement root, string name )
		=> root.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static string Serialize( string kind, bool ok, string body, string errorCode, string errorMessage )
		=> JsonSerializer.Serialize( new { nsProxyEnvelope = Version, kind, ok, body, errorCode, errorMessage } );
}
