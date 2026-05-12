#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

public partial class SyncToolWindow
{
	private void PullAllChangedResources()
	{
		if ( _busy || !SyncToolConfig.IsValid ) return;
		var ids = GetPullableResourceIds();
		if ( ids.Length == 0 )
		{
			_status = "No resources are ready to pull. Run Check for Updates first.";
			Update();
			return;
		}

		ConfirmDialog.Show(
			"Pull All",
			$"This will pull {ids.Length} resource(s) from the selected target ({(_publishTarget == "next" ? "Staged/Main" : "Live")}). Each local file gets a .bak backup before overwrite.",
			() => _ = PullAllChangedResourcesAsync( ids ),
			detail: string.Join( "\n", ids.Select( DescribeSyncItem ) ) );
	}

	private async System.Threading.Tasks.Task PullAllChangedResourcesAsync( string[] ids )
	{
		foreach ( var id in ids )
		{
			await DoPullItem( id );
		}
		_status = $"Pulled {ids.Length} resource(s)";
		Update();
	}

	private string[] GetPullableResourceIds()
	{
		return _items
			.Where( kv => kv.Value.RemoteDiffers || kv.Value.Status == SyncStatus.RemoteOnly || kv.Value.Status == SyncStatus.Differs || kv.Value.Status == SyncStatus.MergeAvailable )
			.Select( kv => kv.Key )
			.Where( id => id.StartsWith( "ep_" ) || id.StartsWith( "col_" ) || id.StartsWith( "wf_" ) )
			.OrderBy( id => id, StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	private bool IsLocalChangedSinceCached( string id, ItemState cached )
	{
		if ( string.IsNullOrWhiteSpace( cached.LocalYaml ) ) return false;
		try
		{
			if ( id.StartsWith( "ep_" ) )
			{
				var file = _endpointFiles.FirstOrDefault( f => ResourceIdFromFile( f, "endpoint" ) == id[3..] );
				return CurrentYamlDiffers( file, "endpoint", cached.LocalYaml );
			}
			if ( id.StartsWith( "col_" ) )
			{
				var file = _collectionFiles.FirstOrDefault( f => ResourceIdFromFile( f, "collection" ) == id[4..] );
				return CurrentYamlDiffers( file, "collection", cached.LocalYaml );
			}
			if ( id.StartsWith( "wf_" ) )
			{
				var file = _workflowFiles.FirstOrDefault( f => ResourceIdFromFile( f, "workflow" ) == id[3..] );
				return CurrentYamlDiffers( file, "workflow", cached.LocalYaml );
			}
		}
		catch { }
		return false;
	}

	private bool CurrentYamlDiffers( string file, string kind, string cachedYaml )
	{
		if ( string.IsNullOrWhiteSpace( file ) || !File.Exists( file ) ) return false;
		if ( !TryReadLocalResourceFile( file, kind, out var resource ) ) return false;
		var current = SyncToolYamlRenderer.RenderFromJson( JsonSerializer.Serialize( resource, new JsonSerializerOptions { WriteIndented = true } ) );
		return !string.Equals( NormalizeLineEndings( current ).Trim(), NormalizeLineEndings( cachedYaml ).Trim(), StringComparison.Ordinal );
	}

	private static string NormalizeLineEndings( string text ) => (text ?? "").Replace( "\r\n", "\n" ).Replace( '\r', '\n' );

	private void BackupLocalFileForPull( string id )
	{
		var file = GetLocalFileForItem( id );
		if ( string.IsNullOrWhiteSpace( file ) || !File.Exists( file ) ) return;
		var backup = $"{file}.bak.{DateTime.Now:yyyyMMddHHmmss}";
		File.Copy( file, backup, overwrite: false );
	}

	private string GetLocalFileForItem( string id )
	{
		if ( id.StartsWith( "ep_" ) ) return _endpointFiles.FirstOrDefault( f => ResourceIdFromFile( f, "endpoint" ) == id[3..] );
		if ( id.StartsWith( "col_" ) ) return _collectionFiles.FirstOrDefault( f => ResourceIdFromFile( f, "collection" ) == id[4..] );
		if ( id.StartsWith( "wf_" ) ) return _workflowFiles.FirstOrDefault( f => ResourceIdFromFile( f, "workflow" ) == id[3..] );
		return null;
	}
}
