using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	private const string PlayerCrudSmokeTestCollection = "players";
	private const string PlayerCrudSmokeTestDocumentId = "testplayer";

	/// <summary>
	/// Dedicated-server diagnostics: create, edit, read, delete, and read a test row in players/testplayer.
	/// Logs every result without exposing secrets.
	/// </summary>
	public static async Task RunPlayerCollectionCrudSmokeTestAsync( string reason = "manual" )
	{
		if ( !Application.IsDedicatedServer )
			return;

		if ( !HasDedicatedServerSecretKey )
		{
			Log.Warning( "[NetworkStorage][CRUD smoke] skipped: dedicated server secret key is not available." );
			return;
		}

		Log.Info( $"[NetworkStorage][CRUD smoke] start reason={reason} collection={PlayerCrudSmokeTestCollection} id={PlayerCrudSmokeTestDocumentId}" );

		var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		var created = await SaveDocument( PlayerCrudSmokeTestCollection, PlayerCrudSmokeTestDocumentId, new
		{
			playerName = "Network Storage Test Player",
			playerMode = "OnFoot",
			gold = 1,
			lastSeenUnixSeconds = now
		} );
		LogCrudSmokeResult( "create", "storage-save", created );

		var edited = await UpdateDocument(
			PlayerCrudSmokeTestCollection,
			PlayerCrudSmokeTestDocumentId,
			NetworkStorageOperation.Set( "playerName", "Network Storage Test Player Edited" ),
			NetworkStorageOperation.Increment( "gold", 41, source: "crud-smoke", reason: "dedicated secret smoke test" ),
			NetworkStorageOperation.Set( "lastSeenUnixSeconds", DateTimeOffset.UtcNow.ToUnixTimeSeconds() ) );
		LogCrudSmokeResult( "edit", "storage-update", edited );

		var readAfterEdit = await GetDocument( PlayerCrudSmokeTestCollection, PlayerCrudSmokeTestDocumentId );
		LogCrudSmokeResult( "read-after-edit", "storage", readAfterEdit );

		var deleted = await DeleteDocument( PlayerCrudSmokeTestCollection, PlayerCrudSmokeTestDocumentId );
		LogCrudSmokeResult( "remove", "storage-delete", deleted );

		var readAfterDelete = await GetDocument( PlayerCrudSmokeTestCollection, PlayerCrudSmokeTestDocumentId );
		LogCrudSmokeMissingResult( "read-after-remove", "storage", readAfterDelete );

		Log.Info( "[NetworkStorage][CRUD smoke] done" );
	}

	private static void LogCrudSmokeMissingResult( string step, string errorTag, JsonElement? result )
	{
		if ( result.HasValue )
		{
			Log.Warning( $"[NetworkStorage][CRUD smoke] {step}: FAIL row still exists {TruncateJson( result.Value )}" );
			return;
		}

		if ( TryGetLastEndpointError( errorTag, out var code, out var message ) && IsMissingAfterDelete( code, message ) )
		{
			Log.Info( $"[NetworkStorage][CRUD smoke] {step}: OK row missing after delete" );
			return;
		}

		LogCrudSmokeResult( step, errorTag, result );
	}

	private static void LogCrudSmokeResult( string step, string errorTag, JsonElement? result )
	{
		if ( result.HasValue )
		{
			Log.Info( $"[NetworkStorage][CRUD smoke] {step}: OK {TruncateJson( result.Value )}" );
			return;
		}

		if ( TryGetLastEndpointError( errorTag, out var code, out var message ) )
		{
			Log.Warning( $"[NetworkStorage][CRUD smoke] {step}: FAIL {code}: {message}" );
			return;
		}

		Log.Warning( $"[NetworkStorage][CRUD smoke] {step}: FAIL no response" );
	}

	private static bool IsMissingAfterDelete( string code, string message )
	{
		return string.Equals( code, "NOT_FOUND", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( code, "RECORD_NOT_FOUND", StringComparison.OrdinalIgnoreCase )
			|| (string.Equals( code, "REQUEST_FAILED", StringComparison.OrdinalIgnoreCase ) && (message?.Contains( "404", StringComparison.OrdinalIgnoreCase ) ?? false));
	}
}
