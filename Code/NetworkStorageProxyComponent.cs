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
	private static readonly Dictionary<string, TaskCompletionSource<string>> _pending = new();

	protected override void OnEnabled()
	{
		if ( IsProxy ) return;

		// Always register delegates on the local player — ProxyEnabled is checked
		// at call time in CallEndpoint/GetDocument, after AutoConfigure has run.
		// Registering here unconditionally avoids a race where OnEnabled fires
		// before credentials are loaded and ProxyEnabled is still its default false.
		NetworkStorage.RequestProxy = ProxyEndpointRequest;
		NetworkStorage.DocumentProxy = ProxyDocumentRequest;

		if ( Networking.IsHost )
			Log.Info( "[NSProxy] Running as host — will handle proxy requests from clients" );
		else
			Log.Info( "[NSProxy] Proxy delegates registered (non-host client)" );
	}

	// ── Client-side: send requests to host ──

	private async Task<string> ProxyEndpointRequest( string steamId, string clientToken, string slug, string inputJson )
	{
		var requestId = Guid.NewGuid().ToString( "N" );
		var tcs = new TaskCompletionSource<string>();
		_pending[requestId] = tcs;

		Log.Info( $"[NSProxy] Sending endpoint proxy: {slug} for {steamId} (req={requestId})" );
		RpcRequestEndpoint( requestId, steamId, clientToken ?? "", slug, inputJson ?? "" );

		var result = await tcs.Task;

		_pending.Remove( requestId );
		return result;
	}

	private async Task<string> ProxyDocumentRequest( string steamId, string clientToken, string collectionId, string documentId )
	{
		var requestId = Guid.NewGuid().ToString( "N" );
		var tcs = new TaskCompletionSource<string>();
		_pending[requestId] = tcs;

		Log.Info( $"[NSProxy] Sending document proxy: {collectionId}/{documentId} for {steamId} (req={requestId})" );
		RpcRequestDocument( requestId, steamId, clientToken ?? "", collectionId, documentId );

		var result = await tcs.Task;

		_pending.Remove( requestId );
		return result;
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
			Log.Warning( $"[NSProxy] Host endpoint error: {slug} for {steamId} — {ex.Message}" );
			RpcRespondToClient( requestId, "" );
		}
	}

	private async Task HandleDocumentRequest( string requestId, string steamId, string clientToken, string collectionId, string documentId )
	{
		Log.Info( $"[NSProxy] Host processing document: {collectionId}/{documentId} for {steamId} (req={requestId})" );

		try
		{
			var result = await NetworkStorage.GetDocumentAs( steamId, clientToken, collectionId, documentId );
			var resultJson = result.HasValue ? result.Value.ToString() : "";

			RpcRespondToClient( requestId, resultJson );
		}
		catch ( Exception ex )
		{
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
		if ( _pending.TryGetValue( requestId, out var tcs ) )
		{
			var response = string.IsNullOrEmpty( resultJson ) ? null : resultJson;
			tcs.TrySetResult( response );
		}
	}
}
