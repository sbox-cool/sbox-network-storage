using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	/// <summary>
	/// Save or replace a collection document. Defaults to the current player's Steam ID.
	/// On dedicated servers, a configured secret key is sent automatically so endpoint-only
	/// collections can be edited when the key has collection execute permission.
	/// </summary>
	public static Task<JsonElement?> SaveDocument( string collectionId, string documentId, object data )
		=> SendStorageRequest( "storage-save", "POST", StorageDocumentPath( collectionId, documentId ), data ?? new { } );

	/// <summary>Alias for SaveDocument.</summary>
	public static Task<JsonElement?> SetDocument( string collectionId, string documentId, object data )
		=> SaveDocument( collectionId, documentId, data );

	/// <summary>
	/// Apply server-side operations to a collection document. This avoids sending the full
	/// document and lets the backend validate increments, ledgers, and rate limits.
	/// </summary>
	public static Task<JsonElement?> UpdateDocument( string collectionId, string documentId, IEnumerable<NetworkStorageOperation> operations )
	{
		var ops = operations?.Where( op => op is not null ).ToArray() ?? Array.Empty<NetworkStorageOperation>();
		return SendStorageRequest( "storage-update", "POST", StorageDocumentPath( collectionId, documentId ), new { ops } );
	}

	/// <summary>Apply server-side operations to a collection document.</summary>
	public static Task<JsonElement?> UpdateDocument( string collectionId, string documentId, params NetworkStorageOperation[] operations )
		=> UpdateDocument( collectionId, documentId, (IEnumerable<NetworkStorageOperation>)operations );

	/// <summary>Alias for UpdateDocument.</summary>
	public static Task<JsonElement?> PatchDocument( string collectionId, string documentId, params NetworkStorageOperation[] operations )
		=> UpdateDocument( collectionId, documentId, operations );

	/// <summary>
	/// Delete a collection document. The backend must have allowRecordDelete enabled for the collection.
	/// Defaults to the current player's Steam ID.
	/// </summary>
	public static Task<JsonElement?> DeleteDocument( string collectionId, string documentId = null )
		=> SendStorageRequest( "storage-delete", "DELETE", StorageDocumentPath( collectionId, documentId ) );

	/// <summary>List save-record metadata for a player in a multi-record collection.</summary>
	public static Task<JsonElement?> ListRecords( string collectionId, string steamId = null )
		=> SendStorageRequest( "storage-records", "GET", StorageRecordIndexPath( collectionId, steamId ) );

	/// <summary>Create a save-record entry for a player in a multi-record collection.</summary>
	public static Task<JsonElement?> CreateRecord( string collectionId, string steamId = null, string recordName = null )
	{
		object body = string.IsNullOrWhiteSpace( recordName ) ? new { } : new { recordName };
		return SendStorageRequest( "storage-record-create", "POST", StorageRecordIndexPath( collectionId, steamId ), body );
	}

	/// <summary>Delete a save-record entry and its stored document data.</summary>
	public static Task<JsonElement?> DeleteRecord( string collectionId, string recordId, string steamId = null )
		=> SendStorageRequest( "storage-record-delete", "DELETE", $"{StorageRecordIndexPath( collectionId, steamId )}/{EscapeRouteSegment( recordId )}" );

	/// <summary>Rename a save-record entry.</summary>
	public static Task<JsonElement?> RenameRecord( string collectionId, string recordId, string recordName, string steamId = null )
		=> SendStorageRequest( "storage-record-rename", "PATCH", $"{StorageRecordIndexPath( collectionId, steamId )}/{EscapeRouteSegment( recordId )}", new { recordName } );

	private static async Task<JsonElement?> SendStorageRequest( string tag, string method, string path, object body = null )
	{
		EnsureConfigured();
		ClearLastEndpointError( tag );

		string url = null;
		string bodyJson = null;
		try
		{
			var usesDedicatedSecret = TryBuildDedicatedStorageHeaders( out var headers );
			if ( !usesDedicatedSecret )
			{
				if ( TryRejectDedicatedServerPlayerAuth( tag ) )
					return null;
				headers = await BuildAuthHeaders();
			}
			url = BuildUrl( path );

			HttpContent content = null;
			if ( body is not null )
			{
				bodyJson = JsonSerializer.Serialize( body );
				content = Http.CreateJsonContent( body );
			}

			if ( NetworkStorageLogConfig.LogRequests )
			{
				var suffix = usesDedicatedSecret ? " secret-key-header" : "";
				NetLog.Request( tag, $"{method}{suffix} {path}" );
				Log.Info( $"[NetworkStorage] {tag} request: {method} {ApiRoot}{path}{suffix}" );
			}

			var raw = await Http.RequestStringAsync( url, method, content, headers );
			if ( NetworkStorageLogConfig.LogResponses )
				Log.Info( $"[NetworkStorage] {tag} → {TruncateJson( raw, 300 )}" );

			var parsed = ParseResponse( tag, raw );
			if ( parsed.HasValue && NetworkStorageLogConfig.LogResponses )
				NetLog.Response( tag, TruncateJson( parsed.Value ) );
			return parsed;
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
			{
				Log.Warning( $"[NetworkStorage] {tag} FAILED — {ex.Message}" );
				Log.Warning( $"[NetworkStorage]   URL: {url ?? $"{ApiRoot}{path}"}" );
				Log.Warning( $"[NetworkStorage]   Method: {method}" );
				if ( bodyJson != null )
					Log.Warning( $"[NetworkStorage]   Body: {bodyJson}" );
				NetLog.Error( tag, ex.Message );
			}
			RecordEndpointError( tag, "REQUEST_FAILED", ex.Message );
			return null;
		}
	}

	private static string StorageDocumentPath( string collectionId, string documentId )
		=> $"/storage/{EscapeRouteSegment( ProjectId )}/{EscapeRouteSegment( collectionId )}/{EscapeRouteSegment( ResolveStorageDocumentId( documentId ) )}";

	private static string StorageRecordIndexPath( string collectionId, string steamId )
		=> $"/storage/{EscapeRouteSegment( ProjectId )}/{EscapeRouteSegment( collectionId )}/{EscapeRouteSegment( ResolveStorageDocumentId( steamId ) )}/records";

	private static string ResolveStorageDocumentId( string documentId )
		=> string.IsNullOrWhiteSpace( documentId ) ? Game.SteamId.ToString() : documentId;

	private static string EscapeRouteSegment( string value )
		=> Uri.EscapeDataString( value ?? "" );
}
