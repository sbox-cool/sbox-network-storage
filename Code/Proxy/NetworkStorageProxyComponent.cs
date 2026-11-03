using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Bridges NetworkStorage proxy delegates to s&amp;box RPCs.
///
/// When proxy mode is enabled (Editor → Network Storage → Settings):
/// - Non-host clients send API calls through this component as [Rpc.Host] requests, which reach only the host
/// - The host rejects a request unless its SteamID matches the sending connection, then calls ReadEndpointAs / ReadDocumentAs
/// - The host sends each reply as an envelope to the requesting connection only (Rpc.FilterInclude), and the client accepts it only from the host it asked
/// - The client awaits the result via a TaskCompletionSource keyed by request ID
///
/// Security: The client's auth token is forwarded to the host, which includes it in the
/// HMAC proxy signature sent to the backend. This proves client consent without requiring
/// the backend to verify the client's token against Facepunch (which fails for non-host clients).
///
/// Attach to each player's GameObject via PlayerSpawner.
/// </summary>
public sealed class NetworkStorageProxyComponent : Component
{
	// Pending requests awaiting a response from the host (client-side only)
	private static readonly Dictionary<string, PendingRequest> _pending = new();

	/// <summary>
	/// Maximum wait for a host response, so a lost RPC cannot leave a game save pending indefinitely.
	/// </summary>
	private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds( 30 );

	/// <summary>A request awaiting the host's reply, with the component that sent it and the host it was sent to.</summary>
	private sealed record PendingRequest( TaskCompletionSource<string> Reply, NetworkStorageProxyComponent Owner, Connection Host );

	protected override void OnEnabled()
	{
		if ( IsProxy ) return;

		// Always register delegates on the local player — ProxyEnabled is checked
		// at call time in CallEndpoint/GetDocument, after AutoConfigure has run.
		// Registering here unconditionally avoids a race where OnEnabled fires
		// before credentials are loaded and ProxyEnabled is still its default false.
		NetworkStorage.RequestProxy = ProxyEndpointRequest;
		NetworkStorage.DocumentProxy = ProxyDocumentRequest;

		if ( NetworkStorageLogConfig.LogProxy )
		{
			if ( Networking.IsHost )
				Log.Info( "[NSProxy] Running as host — will handle proxy requests from clients" );
			else
				Log.Info( "[NSProxy] Proxy delegates registered (non-host client)" );
		}
	}

	protected override void OnUpdate()
	{
		var host = Networking.IsActive ? Connection.Host : null;
		lock ( _pending )
		{
			foreach ( var entry in _pending )
				if ( ReferenceEquals( entry.Value.Owner, this ) && !ReferenceEquals( entry.Value.Host, host ) )
					entry.Value.Reply.TrySetException( new InvalidOperationException( "The host changed or disconnected before it replied." ) );
		}
	}

	protected override void OnDestroy()
	{
		if ( NetworkStorage.RequestProxy == ProxyEndpointRequest ) NetworkStorage.RequestProxy = null;
		if ( NetworkStorage.DocumentProxy == ProxyDocumentRequest ) NetworkStorage.DocumentProxy = null;
		lock ( _pending )
		{
			foreach ( var entry in _pending )
				if ( ReferenceEquals( entry.Value.Owner, this ) )
					entry.Value.Reply.TrySetException( new InvalidOperationException( "The proxy component was destroyed before the host replied." ) );
		}
	}

	// ── Client-side: send requests to host ──

	private async Task<string> ProxyEndpointRequest( string steamId, string clientToken, string slug, string inputJson )
	{
		var requestId = Guid.NewGuid().ToString( "N" );
		var pending = TrackRequest( requestId );

		try
		{
			if ( NetworkStorageLogConfig.LogProxy )
				Log.Info( $"[NSProxy] Sending endpoint proxy: {slug} for {steamId} (req={requestId})" );
			RpcRequestEndpoint( requestId, steamId, clientToken ?? "", slug, inputJson ?? "" );
			return await pending.Reply.Task.WaitAsync( ReplyTimeout );
		}
		finally
		{
			lock ( _pending ) _pending.Remove( requestId );
		}
	}

	private async Task<string> ProxyDocumentRequest( string steamId, string clientToken, string collectionId, string documentId )
	{
		var requestId = Guid.NewGuid().ToString( "N" );
		var pending = TrackRequest( requestId );

		try
		{
			if ( NetworkStorageLogConfig.LogProxy )
				Log.Info( $"[NSProxy] Sending document proxy: {collectionId}/{documentId} for {steamId} (req={requestId})" );
			RpcRequestDocument( requestId, steamId, clientToken ?? "", collectionId, documentId );
			return await pending.Reply.Task.WaitAsync( ReplyTimeout );
		}
		finally
		{
			lock ( _pending ) _pending.Remove( requestId );
		}
	}

	/// <summary>Registers a request that waits for the host's reply.</summary>
	private PendingRequest TrackRequest( string requestId )
	{
		var host = Networking.IsActive ? Connection.Host : null;
		if ( host is null ) throw new InvalidOperationException( "No host connection is available for the proxy request." );
		var pending = new PendingRequest( new TaskCompletionSource<string>( TaskCreationOptions.RunContinuationsAsynchronously ), this, host );
		lock ( _pending ) _pending[requestId] = pending;
		return pending;
	}


	// ── RPCs: client → host (request) ──

	// [Rpc.Host] delivers the request only to the host, so the client token and request never reach other peers.
	[Rpc.Host]
	private void RpcRequestEndpoint( string requestId, string steamId, string clientToken, string slug, string inputJson )
	{
		// Only the host processes proxy requests
		if ( !Networking.IsHost ) return;

		// Rpc.Caller is only valid during this call, so read it before any await.
		var caller = Rpc.Caller;
		if ( !IsFromCaller( steamId, caller ) )
		{
			RejectRequest( caller, requestId, NetworkStorageProxyEnvelope.EndpointKind, steamId );
			return;
		}

		_ = HandleEndpointRequest( caller, requestId, steamId, clientToken, slug, inputJson );
	}

	[Rpc.Host]
	private void RpcRequestDocument( string requestId, string steamId, string clientToken, string collectionId, string documentId )
	{
		if ( !Networking.IsHost ) return;

		var caller = Rpc.Caller;
		if ( !IsFromCaller( steamId, caller ) )
		{
			RejectRequest( caller, requestId, NetworkStorageProxyEnvelope.DocumentKind, steamId );
			return;
		}

		_ = HandleDocumentRequest( caller, requestId, steamId, clientToken, collectionId, documentId );
	}

	/// <summary>True when the request's claimed SteamID is the SteamID of the connection that sent it.</summary>
	private static bool IsFromCaller( string steamId, Connection caller )
		=> caller is not null && SteamIdMatches( steamId, caller.SteamId.ToString() );

	/// <summary>Compares a request's claimed SteamID with its sender's. An empty claim never matches.</summary>
	internal static bool SteamIdMatches( string requestSteamId, string callerSteamId )
		=> !string.IsNullOrEmpty( requestSteamId ) && requestSteamId == callerSteamId;

	// ── Host-side: process and respond ──

	private async Task HandleEndpointRequest( Connection caller, string requestId, string steamId, string clientToken, string slug, string inputJson )
	{
		if ( NetworkStorageLogConfig.LogProxy )
			Log.Info( $"[NSProxy] Host processing endpoint: {slug} for {steamId} (req={requestId})" );

		try
		{
			object input = null;
			if ( !string.IsNullOrEmpty( inputJson ) )
				input = JsonSerializer.Deserialize<JsonElement>( inputJson );

			var read = await NetworkStorage.ReadEndpointAs( steamId, clientToken, slug, input );
			var envelope = read.Body is not null
				? NetworkStorageProxyEnvelope.Success( NetworkStorageProxyEnvelope.EndpointKind, read.Body )
				: NetworkStorageProxyEnvelope.Failure( NetworkStorageProxyEnvelope.EndpointKind, read.ErrorCode ?? "PROXY_FAILED", read.ErrorMessage ?? "Host returned no result." );

			RespondToCaller( caller, requestId, envelope );
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
				Log.Warning( $"[NSProxy] Host endpoint error: {slug} for {steamId} — {ex.Message}" );
			RespondToCaller( caller, requestId, NetworkStorageProxyEnvelope.Failure( NetworkStorageProxyEnvelope.EndpointKind, "PROXY_FAILED", "The host could not process the request." ) );
		}
	}

	private async Task HandleDocumentRequest( Connection caller, string requestId, string steamId, string clientToken, string collectionId, string documentId )
	{
		if ( NetworkStorageLogConfig.LogProxy )
			Log.Info( $"[NSProxy] Host processing document: {collectionId}/{documentId} for {steamId} (req={requestId})" );

		try
		{
			var read = await NetworkStorage.ReadDocumentAs( steamId, clientToken, collectionId, documentId );
			var envelope = read.DocumentJson is not null
				? NetworkStorageProxyEnvelope.Success( NetworkStorageProxyEnvelope.DocumentKind, read.DocumentJson )
				: NetworkStorageProxyEnvelope.Failure( NetworkStorageProxyEnvelope.DocumentKind, read.ErrorCode ?? "PROXY_FAILED", read.ErrorMessage ?? "Host returned no document." );

			RespondToCaller( caller, requestId, envelope );
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
				Log.Warning( $"[NSProxy] Host document error: {collectionId}/{documentId} for {steamId} — {ex.Message}" );
			RespondToCaller( caller, requestId, NetworkStorageProxyEnvelope.Failure( NetworkStorageProxyEnvelope.DocumentKind, "PROXY_FAILED", "The host could not process the request." ) );
		}
	}

	/// <summary>Refuses a request whose claimed SteamID does not match its sender, and tells that sender why.</summary>
	private void RejectRequest( Connection caller, string requestId, string kind, string steamId )
	{
		if ( NetworkStorageLogConfig.LogErrors )
			Log.Warning( $"[NSProxy] Rejected {kind} request {requestId}: claimed steamId {steamId} does not match its sender." );
		RespondToCaller( caller, requestId, NetworkStorageProxyEnvelope.Failure( kind, "PROXY_REJECTED", "The request's steamId does not match the sending connection." ) );
	}

	// ── RPC: host → client (response) ──

	/// <summary>Sends an envelope to the requesting connection only. With no known caller the reply is dropped and the requester times out.</summary>
	private void RespondToCaller( Connection caller, string requestId, string envelope )
	{
		if ( caller is null ) return;

		using ( Rpc.FilterInclude( caller ) )
		{
			RpcRespondToClient( requestId, envelope );
		}
	}

	// Broadcast so the host can address it with Rpc.FilterInclude; the host's filter limits recipients to the requester.
	[Rpc.Broadcast]
	private void RpcRespondToClient( string requestId, string envelope )
	{
		lock ( _pending )
		{
			if ( !_pending.TryGetValue( requestId, out var pending ) ) return;

			// Only the host this request was sent to may answer it.
			if ( pending.Host.Id != Rpc.CallerId ) return;

			pending.Reply.TrySetResult( envelope );
		}
	}
}
