using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Bridges NetworkStorage proxy delegates to s&amp;box RPCs.
///
/// When proxy mode is enabled (Editor → Network Storage → Settings):
/// - Non-host clients route API calls through this component via [Rpc.Broadcast]
/// - The host receives the request, calls CallEndpointAs / GetDocumentAs, and sends the result back
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

	[Rpc.Broadcast]
	private void RpcRequestEndpoint( string requestId, string steamId, string clientToken, string slug, string inputJson )
	{
		// Only the host processes proxy requests
		if ( !Networking.IsHost ) return;

		_ = HandleEndpointRequest( requestId, steamId, clientToken, slug, inputJson );
	}

	[Rpc.Broadcast]
	private void RpcRequestDocument( string requestId, string steamId, string clientToken, string collectionId, string documentId )
	{
		if ( !Networking.IsHost ) return;

		_ = HandleDocumentRequest( requestId, steamId, clientToken, collectionId, documentId );
	}

	// ── Host-side: process and respond ──

	private async Task HandleEndpointRequest( string requestId, string steamId, string clientToken, string slug, string inputJson )
	{
		if ( NetworkStorageLogConfig.LogProxy )
			Log.Info( $"[NSProxy] Host processing endpoint: {slug} for {steamId} (req={requestId})" );

		try
		{
			object input = null;
			if ( !string.IsNullOrEmpty( inputJson ) )
				input = JsonSerializer.Deserialize<JsonElement>( inputJson );

			var result = await NetworkStorage.CallEndpointAs( steamId, clientToken, slug, input );
			var resultJson = result.HasValue ? result.Value.ToString() : CreateEndpointErrorJson( slug );

			RpcRespondToClient( requestId, resultJson );
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
				Log.Warning( $"[NSProxy] Host endpoint error: {slug} for {steamId} — {ex.Message}" );
			RpcRespondToClient( requestId, "" );
		}
	}

	private async Task HandleDocumentRequest( string requestId, string steamId, string clientToken, string collectionId, string documentId )
	{
		if ( NetworkStorageLogConfig.LogProxy )
			Log.Info( $"[NSProxy] Host processing document: {collectionId}/{documentId} for {steamId} (req={requestId})" );

		try
		{
			var result = await NetworkStorage.GetDocumentAs( steamId, clientToken, collectionId, documentId );
			var resultJson = result.HasValue ? result.Value.ToString() : "";

			RpcRespondToClient( requestId, resultJson );
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
				Log.Warning( $"[NSProxy] Host document error: {collectionId}/{documentId} for {steamId} — {ex.Message}" );
			RpcRespondToClient( requestId, "" );
		}
	}

	// ── RPC: host → client (response) ──

	private static string CreateEndpointErrorJson( string slug )
	{
		if ( !NetworkStorage.TryGetLastEndpointError( slug, out var code, out var message ) )
			return "";

		return JsonSerializer.Serialize( new
		{
			ok = false,
			error = new
			{
				code,
				message
			}
		} );
	}

	[Rpc.Broadcast]
	private void RpcRespondToClient( string requestId, string resultJson )
	{
		// All clients receive this, but only the one with the matching pending request processes it
		lock ( _pending )
		{
			if ( _pending.TryGetValue( requestId, out var pending ) )
			{
				var response = string.IsNullOrEmpty( resultJson ) ? null : resultJson;
				pending.Reply.TrySetResult( response );
			}
		}
	}
}
