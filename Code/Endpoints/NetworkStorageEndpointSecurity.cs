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
		public bool Encrypted { get; init; }
		public EncryptedResponseContext ResponseContext { get; init; }
		public string RoutePath { get; init; }
		public string RouteLabel { get; init; }
		public Func<EndpointSecurityRequest> Reseal { get; init; }
	}

	private sealed class RuntimeAuthSession
	{
		public string Token { get; init; }
		public string Id { get; init; }
		public DateTimeOffset ExpiresAt { get; init; }
	}

	private static RuntimeAuthSession _runtimeAuthSession;

	private static async Task<EndpointSecurityRequest> BuildEndpointSecurityRequest( string slug, object input, bool allowAuthSession = true, bool useDedicatedServerSecret = false )
	{
		await EnsureRuntimeSecurityConfigAsync( "endpoint" );
		var refusal = EndpointSecurityRefusal();
		if ( refusal is not null )
		{
			RecordEndpointError( slug, refusal.Code, refusal.Message );
			return null;
		}

		var session = RuntimeEnableAuthSessions && allowAuthSession && !useDedicatedServerSecret
			? await TryEnsureRuntimeAuthSessionAsync()
			: null;
		if ( !RuntimeEnableAuthSessions )
			_runtimeAuthSession = null;

		var mode = useDedicatedServerSecret
			? RuntimeEnableEncryptedRequests ? "encrypted" : "legacy"
			: RuntimeEnableEncryptedRequests && session is null
				? "encrypted"
				: RuntimeSecurityClientMode;
		var headers = useDedicatedServerSecret
			? BuildPublicHeaders( mode )
			: await BuildAuthHeaders( session?.Token, mode );
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
				RoutePath = $"/endpoints/{ProjectId}/{EscapeRouteSegment( slug )}",
				RouteLabel = $"/endpoints/{ProjectId}/{slug}"
			};
		}

		var routePath = $"/endpoints/{ProjectId}/{EscapeRouteSegment( slug )}";
		var routeLabel = $"/endpoints/{ProjectId}/{slug}";
		payload["_endpointSlug"] = slug;

		// Every attempt seals a fresh copy with a fresh encryptedRequestId. A retry therefore never replays an id
		// the server already consumed. Retries call Reseal() instead of reusing Body.
		EndpointSecurityRequest SealRequest()
		{
			var attemptPayload = new Dictionary<string, object>( payload );
			var encryptedRequestId = CreateEncryptedRequestId();
			attemptPayload["encryptedRequestId"] = encryptedRequestId;
			if ( NetworkStorageLogConfig.LogRequests )
				Log.Info( $"[NetworkStorage] {slug} encrypted request id={encryptedRequestId} mode={mode}" );

			if ( _runtimeSecurityConfig?.EncryptionKey is { } encryptionKey )
			{
				attemptPayload.Remove( "security" );
				var v2Envelope = SealEndpointRequestV2( encryptionKey, slug, attemptPayload, session, out var responseContext );
				return new EndpointSecurityRequest
				{
					Body = new Dictionary<string, object>
					{
						["security"] = BuildClientSecurityContext( mode ),
						["encrypted"] = true,
						["envelope"] = v2Envelope
					},
					Headers = headers,
					Mode = mode,
					Encrypted = true,
					ResponseContext = responseContext,
					RoutePath = routePath,
					RouteLabel = routeLabel,
					Reseal = SealRequest
				};
			}

			var envelope = CreateEncryptedEndpointEnvelope( slug, attemptPayload, session );
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
				Encrypted = true,
				RoutePath = routePath,
				RouteLabel = routeLabel,
				Reseal = SealRequest
			};
		}

		return SealRequest();
	}

	private static Dictionary<string, object> BuildClientSecurityContext( string mode ) => new()
	{
		["configVersion"] = RuntimeSecurityConfigVersion ?? "",
		["clientMode"] = mode,
		["authSessions"] = RuntimeEnableAuthSessions ? "enabled" : "disabled",
		["encryptedRequests"] = RuntimeEnableEncryptedRequests ? "required" : "disabled",
		["revisionId"] = NetworkStoragePackageInfo.RuntimeRevisionId ?? 0,
		["revisionOutdated"] = NetworkStoragePackageInfo.IsOutdatedRevision,
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
		if ( IsDedicatedServerProcess )
		{
			LogDedicatedPlayerAuthSuppressedOnce();
			throw new InvalidOperationException( DedicatedServerAuthUnavailableMessage() );
		}

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
		AddRuntimeTargetHeaders( headers );
		var body = new Dictionary<string, object> { ["steamId"] = steamId };
		var raw = await RequestStringAsync(url, "POST", () => Http.CreateJsonContent( body ), headers);
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

}
