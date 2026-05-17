using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public partial class SyncToolWindow
{
	private async Task RetryCollectionVerificationMismatches(
		Dictionary<string, string> localColByName,
		Dictionary<string, string> localColSourceTextByName,
		string publishTarget )
	{
		var collectionMismatches = _syncLog
			.Where( e => e.Type == "Collection" && e.Detail != null && e.Detail.Contains( "Mismatch" ) )
			.Select( e => e.Name.EndsWith( ".collection.yml", StringComparison.OrdinalIgnoreCase )
				? e.Name[..^".collection.yml".Length]
				: e.Name )
			.Where( name => !string.IsNullOrWhiteSpace( name ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToList();

		if ( collectionMismatches.Count == 0 )
			return;

		var delaysMs = new[] { 1000, 2000, 3000, 5000, 8000, 13000 };
		foreach ( var delayMs in delaysMs )
		{
			_status = $"Waiting for remote readback ({collectionMismatches.Count} collection mismatch(es))...";
			Update();
			await Task.Delay( delayMs );

			var remoteCols = await SyncToolApi.GetCollectionsForPublishTarget( publishTarget );
			if ( !remoteCols.HasValue )
				continue;

			var data = remoteCols.Value;
			if ( data.TryGetProperty( "data", out var d ) ) data = d;
			if ( data.ValueKind != JsonValueKind.Array )
				continue;

			var remaining = new List<string>();
			foreach ( var colName in collectionMismatches )
			{
				var remoteCollection = data.EnumerateArray().FirstOrDefault( col => string.Equals( GetRemoteCollectionName( col ), colName, StringComparison.OrdinalIgnoreCase ) );
				if ( remoteCollection.ValueKind != JsonValueKind.Object )
				{
					remaining.Add( colName );
					continue;
				}

				if ( CollectionVerificationMatches( colName, remoteCollection, localColByName, localColSourceTextByName ) )
				{
					MarkCollectionMismatchVerified( colName );
				}
				else
				{
					remaining.Add( colName );
				}
			}

			collectionMismatches = remaining;
			if ( collectionMismatches.Count == 0 )
				return;
		}
	}

	private static string GetRemoteCollectionName( JsonElement collection )
	{
		return collection.ValueKind == JsonValueKind.Object && collection.TryGetProperty( "name", out var name )
			? name.GetString()
			: null;
	}

	private static bool CollectionVerificationMatches(
		string colName,
		JsonElement remoteCollection,
		Dictionary<string, string> localColByName,
		Dictionary<string, string> localColSourceTextByName )
	{
		var sourceTextMatches = localColSourceTextByName.TryGetValue( colName, out var localSourceText )
			&& SyncToolTransforms.TryGetSourceText( remoteCollection, out var remoteSourceText )
			&& localSourceText == NormalizeSourceTextForVerification( remoteSourceText );
		if ( sourceTextMatches )
			return true;

		var remoteLocal = SyncToolTransforms.ServerCollectionToLocal( remoteCollection );
		var remoteNorm = NormalizeJson( JsonSerializer.Serialize( remoteLocal ) );
		return localColByName.TryGetValue( colName, out var localNorm )
			&& (localNorm == remoteNorm || CollectionSemanticsMatch( localNorm, remoteNorm ));
	}

	private void MarkCollectionMismatchVerified( string colName )
	{
		var logIdx = _syncLog.FindIndex( e => e.Name == $"{colName}.collection.yml" && e.Type == "Collection" );
		if ( logIdx >= 0 )
		{
			var entry = _syncLog[logIdx];
			entry.Ok = true;
			entry.Detail = "Verified source ✓ (after readback retry)";
			_syncLog[logIdx] = entry;
		}

		SetItemState( $"col_{colName}", remoteDiffers: false, diffSummary: "", status: SyncStatus.InSync );
	}
}
