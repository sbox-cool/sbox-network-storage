using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	private sealed class EndpointSecurityRequest
	{
		public object Body { get; init; }
		public Dictionary<string, string> Headers { get; init; }
		public string Mode { get; init; }
		public string RoutePath { get; init; }
		public string RouteLabel { get; init; }
	}

	private sealed class RuntimeAuthSession
	{
		public string Token { get; init; }
		public string Id { get; init; }
		public DateTimeOffset ExpiresAt { get; init; }
	}

	private static RuntimeAuthSession _runtimeAuthSession;

	private static async Task<EndpointSecurityRequest> BuildEndpointSecurityRequest( string slug, object input, bool allowAuthSession = true )
	{
		await EnsureRuntimeSecurityConfigAsync( "endpoint" );

		var session = RuntimeEnableAuthSessions && allowAuthSession
			? await TryEnsureRuntimeAuthSessionAsync()
			: null;
		if ( !RuntimeEnableAuthSessions )
			_runtimeAuthSession = null;

		var mode = RuntimeEnableEncryptedRequests && session is null
			? "encrypted"
			: RuntimeSecurityClientMode;
		var headers = await BuildAuthHeaders( session?.Token, mode );
		var payload = ObjectToDictionary( input );
		payload["security"] = BuildClientSecurityContext( mode );

		if ( !RuntimeEnableEncryptedRequests )
		{
			payload["_endpointSlug"] = slug;
			return new EndpointSecurityRequest
			{
				Body = payload,
				Headers = headers,
				Mode = mode,
				RoutePath = $"/endpoints/{ProjectId}",
				RouteLabel = $"/endpoints/{ProjectId}/{slug}"
			};
		}

		var encryptedRequestId = CreateEncryptedRequestId();
		payload["encryptedRequestId"] = encryptedRequestId;
		payload["_endpointSlug"] = slug;
		if ( NetworkStorageLogConfig.LogRequests )
			Log.Info( $"[NetworkStorage] {slug} encrypted request id={encryptedRequestId} mode={mode}" );
		var envelope = CreateEncryptedEndpointEnvelope( slug, payload, session );
		return new EndpointSecurityRequest
		{
			Body = new Dictionary<string, object>
			{
				["security"] = BuildClientSecurityContext( mode ),
				["encrypted"] = true,
				["envelope"] = envelope
			},
			Headers = headers,
			Mode = mode,
			RoutePath = $"/endpoints/{ProjectId}",
			RouteLabel = $"/endpoints/{ProjectId}"
		};
	}

	private static Dictionary<string, object> BuildClientSecurityContext( string mode ) => new()
	{
		["configVersion"] = RuntimeSecurityConfigVersion ?? "",
		["clientMode"] = mode,
		["authSessions"] = RuntimeEnableAuthSessions ? "enabled" : "disabled",
		["encryptedRequests"] = RuntimeEnableEncryptedRequests ? "required" : "disabled"
	};

	private static async Task<RuntimeAuthSession> TryEnsureRuntimeAuthSessionAsync()
	{
		try
		{
			return await EnsureRuntimeAuthSessionAsync();
		}
		catch ( Exception ex )
		{
			_runtimeAuthSession = null;
			if ( NetworkStorageLogConfig.LogTokens )
				Log.Warning( $"[NetworkStorage] auth session unavailable ({ex.Message}); using steam-bound encrypted request mode" );
			return null;
		}
	}

	private static async Task<RuntimeAuthSession> EnsureRuntimeAuthSessionAsync()
	{
		if ( _runtimeAuthSession is not null && _runtimeAuthSession.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds( 30 ) )
			return _runtimeAuthSession;

		var steamId = Game.SteamId.ToString();
		var token = await GetAuthTokenWithRetry( $"steamId={steamId}" );
		if ( string.IsNullOrWhiteSpace( token ) )
			throw new InvalidOperationException( "Auth session required, but s&box auth token is unavailable." );

		var url = BuildUrl( $"/auth-sessions/{ProjectId}/create" );
		var headers = new Dictionary<string, string>
		{
			["x-api-key"] = ApiKey ?? "",
			["x-public-key"] = ApiKey ?? "",
			["x-steam-id"] = steamId,
			["x-sbox-token"] = token
		};
		var body = new Dictionary<string, object> { ["steamId"] = steamId };
		var raw = await Http.RequestStringAsync( url, "POST", Http.CreateJsonContent( body ), headers );
		if ( NetworkStorageLogConfig.LogTokens )
			Log.Info( $"[NetworkStorage] auth session create -> {TruncateForLog( raw, 500 )}" );
		using var doc = JsonDocument.Parse( raw );
		var root = doc.RootElement;
		if ( root.TryGetProperty( "ok", out var ok ) && ok.ValueKind == JsonValueKind.False )
			throw new InvalidOperationException( ReadServerMessage( root, "Auth session create failed." ) );

		var sessionToken = ReadString( root, "sessionToken" );
		var ttlSeconds = ReadInt( root, "ttlSeconds", 3600 );
		var session = root.TryGetProperty( "session", out var sessionProp ) ? sessionProp : default;
		var sessionId = ReadString( session, "id" );
		if ( string.IsNullOrWhiteSpace( sessionToken ) || string.IsNullOrWhiteSpace( sessionId ) )
			throw new InvalidOperationException( "Auth session create response was missing token or session id." );

		_runtimeAuthSession = new RuntimeAuthSession
		{
			Token = sessionToken,
			Id = sessionId,
			ExpiresAt = DateTimeOffset.UtcNow.AddSeconds( Math.Max( 60, ttlSeconds ) )
		};
		if ( NetworkStorageLogConfig.LogTokens )
			Log.Info( $"[NetworkStorage] auth session loaded id={sessionId} ttl={ttlSeconds}s mode={RuntimeSecurityClientMode}" );
		return _runtimeAuthSession;
	}

	private static Dictionary<string, object> CreateEncryptedEndpointEnvelope( string slug, Dictionary<string, object> payload, RuntimeAuthSession session )
	{
		var envelope = new Dictionary<string, object>
		{
			["version"] = "1",
			["algorithm"] = "aes-256-gcm+hmac-sha256",
			["projectId"] = ProjectId,
			["nonce"] = Base64UrlEncode( Encoding.UTF8.GetBytes( Guid.NewGuid().ToString( "N" ) ) ),
			["publicKeyFingerprint"] = PublicKeyFingerprint( ApiKey )
		};
		if ( session is not null )
			envelope["sessionId"] = session.Id;
		else
			envelope["steamId"] = Game.SteamId.ToString();

		var iv = Guid.NewGuid().ToByteArray()[..12];
		envelope["iv"] = Base64UrlEncode( iv );
		var key = Sha256( Encoding.UTF8.GetBytes( $"sboxcool.network-storage.encrypted-request.v1\0{ApiKey ?? ""}\0{StableStringify( EnvelopeContext( envelope ) )}" ) );
		var plaintext = Encoding.UTF8.GetBytes( JsonSerializer.Serialize( payload ) );
		var aad = Encoding.UTF8.GetBytes( StableStringify( EnvelopeContext( envelope ) ) );
		var (ciphertext, tag) = Aes256GcmEncrypt( key, iv, plaintext, aad );

		envelope["tag"] = Base64UrlEncode( tag );
		envelope["encryptedPayload"] = Base64UrlEncode( ciphertext );
		envelope["signature"] = ToHex( HmacSha256( Encoding.UTF8.GetBytes( ApiKey ?? "" ), Encoding.UTF8.GetBytes( SignatureBinding( envelope ) ) ) );
		return envelope;
	}

	private static Dictionary<string, object> EnvelopeContext( Dictionary<string, object> envelope )
	{
		var identity = envelope.ContainsKey( "sessionId" )
			? ("session", Convert.ToString( envelope["sessionId"] ) ?? "")
			: ("steam", Convert.ToString( envelope["steamId"] ) ?? "");
		return new Dictionary<string, object>
		{
			["version"] = Convert.ToString( envelope["version"] ) ?? "1",
			["algorithm"] = Convert.ToString( envelope["algorithm"] ) ?? "aes-256-gcm+hmac-sha256",
			["projectId"] = Convert.ToString( envelope["projectId"] ) ?? "",
			["identityType"] = identity.Item1,
			["identity"] = identity.Item2,
			["nonce"] = Convert.ToString( envelope["nonce"] ) ?? "",
			["publicKeyFingerprint"] = Convert.ToString( envelope["publicKeyFingerprint"] ) ?? ""
		};
	}

	private static string SignatureBinding( Dictionary<string, object> envelope )
	{
		var binding = EnvelopeContext( envelope );
		binding["iv"] = Convert.ToString( envelope["iv"] ) ?? "";
		binding["encryptedPayload"] = Convert.ToString( envelope["encryptedPayload"] ) ?? "";
		binding["tag"] = Convert.ToString( envelope["tag"] ) ?? "";
		return StableStringify( binding );
	}

	private static Dictionary<string, object> ObjectToDictionary( object input )
	{
		if ( input is Dictionary<string, object> existing )
			return new Dictionary<string, object>( existing );

		var json = JsonSerializer.Serialize( input ?? new { } );
		return JsonSerializer.Deserialize<Dictionary<string, object>>( json ) ?? new Dictionary<string, object>();
	}

	private static string PublicKeyFingerprint( string apiKey )
		=> ToHex( Sha256( Encoding.UTF8.GetBytes( apiKey ?? "" ) ) )[..32];

	private static string ReadServerMessage( JsonElement root, string fallback )
	{
		if ( root.TryGetProperty( "message", out var message ) && message.ValueKind == JsonValueKind.String )
			return message.GetString() ?? fallback;
		if ( root.TryGetProperty( "error", out var error ) && error.ValueKind == JsonValueKind.Object &&
			error.TryGetProperty( "message", out var nested ) && nested.ValueKind == JsonValueKind.String )
			return nested.GetString() ?? fallback;
		return fallback;
	}

	private static string StableStringify( object value )
	{
		var sb = new StringBuilder();
		WriteStableJson( sb, value );
		return sb.ToString();
	}

	private static void WriteStableJson( StringBuilder sb, object value )
	{
		if ( value is Dictionary<string, object> dict )
		{
			sb.Append( '{' );
			var keys = new List<string>( dict.Keys );
			keys.Sort( StringComparer.Ordinal );
			for ( var i = 0; i < keys.Count; i++ )
			{
				if ( i > 0 ) sb.Append( ',' );
				WriteJsonString( sb, keys[i] );
				sb.Append( ':' );
				WriteStableJson( sb, dict[keys[i]] );
			}
			sb.Append( '}' );
			return;
		}
		if ( value is string stringValue )
		{
			WriteJsonString( sb, stringValue );
			return;
		}
		sb.Append( JsonSerializer.Serialize( value ) );
	}

	private static void WriteJsonString( StringBuilder sb, string value )
	{
		sb.Append( '"' );
		foreach ( var ch in value ?? "" )
		{
			switch ( ch )
			{
				case '"': sb.Append( "\\\"" ); break;
				case '\\': sb.Append( "\\\\" ); break;
				case '\b': sb.Append( "\\b" ); break;
				case '\f': sb.Append( "\\f" ); break;
				case '\n': sb.Append( "\\n" ); break;
				case '\r': sb.Append( "\\r" ); break;
				case '\t': sb.Append( "\\t" ); break;
				default:
					if ( ch < ' ' )
						sb.Append( "\\u" ).Append( ((int)ch).ToString( "x4" ) );
					else
						sb.Append( ch );
					break;
			}
		}
		sb.Append( '"' );
	}

	private static string Base64UrlEncode( byte[] value )
		=> Convert.ToBase64String( value ).TrimEnd( '=' ).Replace( '+', '-' ).Replace( '/', '_' );

	private static string TruncateForLog( string value, int maxLength )
	{
		if ( string.IsNullOrEmpty( value ) || value.Length <= maxLength )
			return value;

		return $"{value[..maxLength]}...";
	}

	private static string ToHex( byte[] value )
	{
		var sb = new StringBuilder( value.Length * 2 );
		foreach ( var b in value )
			sb.Append( b.ToString( "x2" ) );
		return sb.ToString();
	}

	private static (byte[] Ciphertext, byte[] Tag) Aes256GcmEncrypt( byte[] key, byte[] iv, byte[] plaintext, byte[] aad )
	{
		var roundKeys = Aes256ExpandKey( key );
		var h = AesEncryptBlock( new byte[16], roundKeys );
		var j0 = new byte[16];
		Array.Copy( iv, 0, j0, 0, 12 );
		j0[15] = 1;

		var ciphertext = new byte[plaintext.Length];
		var counter = CopyBytes( j0 );
		var offset = 0;
		while ( offset < plaintext.Length )
		{
			IncrementCounter32( counter );
			var stream = AesEncryptBlock( counter, roundKeys );
			var count = Math.Min( 16, plaintext.Length - offset );
			for ( var i = 0; i < count; i++ )
				ciphertext[offset + i] = (byte)(plaintext[offset + i] ^ stream[i]);
			offset += count;
		}

		var ghash = GHash( h, aad, ciphertext );
		var s = AesEncryptBlock( j0, roundKeys );
		var tag = new byte[16];
		for ( var i = 0; i < 16; i++ )
			tag[i] = (byte)(s[i] ^ ghash[i]);
		return (ciphertext, tag);
	}

	private static void IncrementCounter32( byte[] counter )
	{
		for ( var i = 15; i >= 12; i-- )
		{
			counter[i]++;
			if ( counter[i] != 0 )
				break;
		}
	}

	private static byte[] GHash( byte[] h, byte[] aad, byte[] ciphertext )
	{
		var y = new byte[16];
		GHashBlocks( y, h, aad );
		GHashBlocks( y, h, ciphertext );
		var len = new byte[16];
		WriteUInt64BE( len, 0, (ulong)aad.Length * 8 );
		WriteUInt64BE( len, 8, (ulong)ciphertext.Length * 8 );
		XorBlock( y, len );
		return GfMultiply( y, h );
	}

	private static void GHashBlocks( byte[] y, byte[] h, byte[] data )
	{
		for ( var offset = 0; offset < data.Length; offset += 16 )
		{
			var block = new byte[16];
			Array.Copy( data, offset, block, 0, Math.Min( 16, data.Length - offset ) );
			XorBlock( y, block );
			var next = GfMultiply( y, h );
			Array.Copy( next, y, 16 );
		}
	}

	private static byte[] GfMultiply( byte[] x, byte[] y )
	{
		var z = new byte[16];
		var v = CopyBytes( y );
		for ( var i = 0; i < 128; i++ )
		{
			if ( (x[i / 8] & (1 << (7 - (i % 8)))) != 0 )
				XorBlock( z, v );
			var lsb = (v[15] & 1) != 0;
			ShiftRightOne( v );
			if ( lsb )
				v[0] ^= 0xe1;
		}
		return z;
	}

	private static void XorBlock( byte[] target, byte[] value )
	{
		for ( var i = 0; i < 16; i++ )
			target[i] ^= value[i];
	}

	private static void ShiftRightOne( byte[] value )
	{
		var carry = 0;
		for ( var i = 0; i < value.Length; i++ )
		{
			var nextCarry = value[i] & 1;
			value[i] = (byte)((value[i] >> 1) | (carry << 7));
			carry = nextCarry;
		}
	}

	private static void WriteUInt64BE( byte[] buffer, int offset, ulong value )
	{
		for ( var i = 7; i >= 0; i-- )
		{
			buffer[offset + i] = (byte)value;
			value >>= 8;
		}
	}

	private static byte[][] Aes256ExpandKey( byte[] key )
	{
		const int nk = 8;
		const int nb = 4;
		const int nr = 14;
		var w = new byte[nb * (nr + 1)][];
		for ( var i = 0; i < nk; i++ )
			w[i] = new[] { key[4 * i], key[4 * i + 1], key[4 * i + 2], key[4 * i + 3] };
		for ( var i = nk; i < w.Length; i++ )
		{
			var temp = CopyBytes( w[i - 1] );
			if ( i % nk == 0 )
			{
				temp = SubWord( RotWord( temp ) );
				temp[0] ^= Rcon[i / nk];
			}
			else if ( i % nk == 4 )
			{
				temp = SubWord( temp );
			}
			w[i] = new byte[4];
			for ( var j = 0; j < 4; j++ )
				w[i][j] = (byte)(w[i - nk][j] ^ temp[j]);
		}
		return w;
	}

	private static byte[] AesEncryptBlock( byte[] input, byte[][] w )
	{
		var state = new byte[16];
		Array.Copy( input, state, 16 );
		AddRoundKey( state, w, 0 );
		for ( var round = 1; round < 14; round++ )
		{
			SubBytes( state );
			ShiftRows( state );
			MixColumns( state );
			AddRoundKey( state, w, round );
		}
		SubBytes( state );
		ShiftRows( state );
		AddRoundKey( state, w, 14 );
		return state;
	}

	private static byte[] RotWord( byte[] word ) => new[] { word[1], word[2], word[3], word[0] };
	private static byte[] SubWord( byte[] word ) => new[] { SBox[word[0]], SBox[word[1]], SBox[word[2]], SBox[word[3]] };

	private static byte[] CopyBytes( byte[] source )
	{
		var copy = new byte[source.Length];
		Array.Copy( source, copy, source.Length );
		return copy;
	}

	private static void AddRoundKey( byte[] state, byte[][] w, int round )
	{
		for ( var c = 0; c < 4; c++ )
		for ( var r = 0; r < 4; r++ )
			state[c * 4 + r] ^= w[round * 4 + c][r];
	}

	private static void SubBytes( byte[] state )
	{
		for ( var i = 0; i < state.Length; i++ )
			state[i] = SBox[state[i]];
	}

	private static void ShiftRows( byte[] s )
	{
		(s[1], s[5], s[9], s[13]) = (s[5], s[9], s[13], s[1]);
		(s[2], s[6], s[10], s[14]) = (s[10], s[14], s[2], s[6]);
		(s[3], s[7], s[11], s[15]) = (s[15], s[3], s[7], s[11]);
	}

	private static void MixColumns( byte[] s )
	{
		for ( var c = 0; c < 4; c++ )
		{
			var i = c * 4;
			var a0 = s[i];
			var a1 = s[i + 1];
			var a2 = s[i + 2];
			var a3 = s[i + 3];
			s[i] = (byte)(Gmul2( a0 ) ^ Gmul3( a1 ) ^ a2 ^ a3);
			s[i + 1] = (byte)(a0 ^ Gmul2( a1 ) ^ Gmul3( a2 ) ^ a3);
			s[i + 2] = (byte)(a0 ^ a1 ^ Gmul2( a2 ) ^ Gmul3( a3 ));
			s[i + 3] = (byte)(Gmul3( a0 ) ^ a1 ^ a2 ^ Gmul2( a3 ));
		}
	}

	private static byte Gmul2( byte x ) => (byte)((x << 1) ^ (((x >> 7) & 1) * 0x1b));
	private static byte Gmul3( byte x ) => (byte)(Gmul2( x ) ^ x);

	private static readonly byte[] Rcon = {
		0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1b, 0x36
	};

	private static readonly byte[] SBox = {
		0x63,0x7c,0x77,0x7b,0xf2,0x6b,0x6f,0xc5,0x30,0x01,0x67,0x2b,0xfe,0xd7,0xab,0x76,
		0xca,0x82,0xc9,0x7d,0xfa,0x59,0x47,0xf0,0xad,0xd4,0xa2,0xaf,0x9c,0xa4,0x72,0xc0,
		0xb7,0xfd,0x93,0x26,0x36,0x3f,0xf7,0xcc,0x34,0xa5,0xe5,0xf1,0x71,0xd8,0x31,0x15,
		0x04,0xc7,0x23,0xc3,0x18,0x96,0x05,0x9a,0x07,0x12,0x80,0xe2,0xeb,0x27,0xb2,0x75,
		0x09,0x83,0x2c,0x1a,0x1b,0x6e,0x5a,0xa0,0x52,0x3b,0xd6,0xb3,0x29,0xe3,0x2f,0x84,
		0x53,0xd1,0x00,0xed,0x20,0xfc,0xb1,0x5b,0x6a,0xcb,0xbe,0x39,0x4a,0x4c,0x58,0xcf,
		0xd0,0xef,0xaa,0xfb,0x43,0x4d,0x33,0x85,0x45,0xf9,0x02,0x7f,0x50,0x3c,0x9f,0xa8,
		0x51,0xa3,0x40,0x8f,0x92,0x9d,0x38,0xf5,0xbc,0xb6,0xda,0x21,0x10,0xff,0xf3,0xd2,
		0xcd,0x0c,0x13,0xec,0x5f,0x97,0x44,0x17,0xc4,0xa7,0x7e,0x3d,0x64,0x5d,0x19,0x73,
		0x60,0x81,0x4f,0xdc,0x22,0x2a,0x90,0x88,0x46,0xee,0xb8,0x14,0xde,0x5e,0x0b,0xdb,
		0xe0,0x32,0x3a,0x0a,0x49,0x06,0x24,0x5c,0xc2,0xd3,0xac,0x62,0x91,0x95,0xe4,0x79,
		0xe7,0xc8,0x37,0x6d,0x8d,0xd5,0x4e,0xa9,0x6c,0x56,0xf4,0xea,0x65,0x7a,0xae,0x08,
		0xba,0x78,0x25,0x2e,0x1c,0xa6,0xb4,0xc6,0xe8,0xdd,0x74,0x1f,0x4b,0xbd,0x8b,0x8a,
		0x70,0x3e,0xb5,0x66,0x48,0x03,0xf6,0x0e,0x61,0x35,0x57,0xb9,0x86,0xc1,0x1d,0x9e,
		0xe1,0xf8,0x98,0x11,0x69,0xd9,0x8e,0x94,0x9b,0x1e,0x87,0xe9,0xce,0x55,0x28,0xdf,
		0x8c,0xa1,0x89,0x0d,0xbf,0xe6,0x42,0x68,0x41,0x99,0x2d,0x0f,0xb0,0x54,0xbb,0x16
	};
}
