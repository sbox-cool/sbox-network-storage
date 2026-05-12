#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public partial class SyncToolWindow
{
	private async Task<bool> RunPreflightOrOfferFix( JsonElement payload, Action retryAfterFix, IEnumerable<string> endpointIds = null )
	{
		_busyItem = "preflight";
		_status = "Running preflight validation...";
		Update();

		var resp = await SyncToolApi.PreflightSync( payload, _publishTarget );
		if ( !resp.HasValue )
		{
			var errCode = SyncToolApi.LastErrorCode ?? "";
			var errMsg = SyncToolApi.LastErrorMessage ?? "";
			if ( IsBatchSyncEndpointUnavailable( errCode, errMsg ) )
			{
				_syncLog.Add( new SyncLogEntry { Name = "Preflight", Type = "Validation", Ok = true, Detail = "Skipped - backend preflight route missing" } );
				return true;
			}
			_syncLog.Add( new SyncLogEntry { Name = "Preflight", Type = "Validation", Ok = false, Detail = string.IsNullOrWhiteSpace( errMsg ) ? "Preflight failed" : errMsg } );
			return false;
		}

		var ok = !resp.Value.TryGetProperty( "ok", out var okProp ) || okProp.ValueKind == JsonValueKind.True;
		AppendPreflightLog( resp.Value );
		if ( ok ) return true;

		if ( HasMissingStepIdDiagnostics( resp.Value ) )
		{
			_status = "Some endpoint steps are missing IDs.";
			var plans = BuildStepIdFixPlans( endpointIds ).Where( p => p.HasChanges ).ToList();
			if ( plans.Count > 0 )
			{
				foreach ( var plan in plans )
					SetItemState( $"ep_{plan.ResourceId}", result: "FAIL", remoteDiffers: false, status: SyncStatus.LocalOnly, diffSummary: "Missing IDs - ready to auto-fix" );

				StepIdAutoFixWindow.Show( plans, () =>
				{
					RefreshFileList();
					retryAfterFix?.Invoke();
				} );
			}
			return false;
		}

		_status = "Preflight failed - fix validation errors before pushing";
		return false;
	}

	private JsonElement BuildPushAllPayload( out bool hasAny )
	{
		var localEps = SyncToolConfig.LoadSourcePayloadResources( "endpoint", includeDeprecated: false );
		var localCols = SyncToolConfig.LoadSourcePayloadResources( "collection" );
		var localWfs = SyncToolConfig.LoadSourcePayloadResources( "workflow" );
		hasAny = localEps.Count > 0 || localCols.Count > 0 || localWfs.Count > 0;
		var batchPayload = new Dictionary<string, object>();
		if ( localEps.Count > 0 ) batchPayload["endpoints"] = JsonSerializer.Deserialize<object>( JsonSerializer.Serialize( localEps ) );
		if ( localCols.Count > 0 ) batchPayload["collections"] = JsonSerializer.Deserialize<object>( JsonSerializer.Serialize( localCols ) );
		if ( localWfs.Count > 0 ) batchPayload["workflows"] = JsonSerializer.Deserialize<object>( JsonSerializer.Serialize( localWfs ) );
		return JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( batchPayload ) );
	}

	private JsonElement BuildSinglePushPayload( string id )
	{
		var batchPayload = new Dictionary<string, object>();
		if ( id.StartsWith( "ep_" ) )
		{
			var slug = id[3..];
			var file = _endpointFiles.FirstOrDefault( f => ResourceIdFromFile( f, "endpoint" ) == slug );
			if ( file != null && SyncToolConfig.TryLoadSourcePayloadResource( "endpoint", file, out var ep, includeDeprecated: false ) )
				batchPayload["endpoints"] = new[] { JsonSerializer.Deserialize<object>( ep.GetRawText() ) };
		}
		else if ( id.StartsWith( "col_" ) )
		{
			var name = id[4..];
			var file = _collectionFiles.FirstOrDefault( f => ResourceIdFromFile( f, "collection" ) == name );
			if ( file != null && SyncToolConfig.TryLoadSourcePayloadResource( "collection", file, out var col ) )
				batchPayload["collections"] = new[] { JsonSerializer.Deserialize<object>( col.GetRawText() ) };
		}
		else if ( id.StartsWith( "wf_" ) )
		{
			var name = id[3..];
			var file = _workflowFiles.FirstOrDefault( f => ResourceIdFromFile( f, "workflow" ) == name );
			if ( file != null && SyncToolConfig.TryLoadSourcePayloadResource( "workflow", file, out var wf ) )
				batchPayload["workflows"] = new[] { JsonSerializer.Deserialize<object>( wf.GetRawText() ) };
		}
		return JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( batchPayload ) );
	}

	private List<StepIdFixPlan> BuildStepIdFixPlans( IEnumerable<string> endpointIds = null )
	{
		var wanted = endpointIds?.ToHashSet( StringComparer.OrdinalIgnoreCase );
		var plans = new List<StepIdFixPlan>();
		foreach ( var file in GetActiveEndpointFiles() )
		{
			var slug = ResourceIdFromFile( file, "endpoint" );
			if ( wanted != null && !wanted.Contains( slug ) ) continue;
			plans.Add( StepIdAutoFixer.BuildPlan( file, slug ) );
		}
		return plans;
	}

	private static bool HasMissingStepIdDiagnostics( JsonElement payload )
	{
		foreach ( var diagnostic in EnumerateDiagnostics( payload ) )
		{
			var text = diagnostic.ToString();
			if ( text.Contains( "steps[", StringComparison.OrdinalIgnoreCase ) && text.Contains( ".id", StringComparison.OrdinalIgnoreCase ) ) return true;
			if ( text.Contains( "non-empty string", StringComparison.OrdinalIgnoreCase ) && text.Contains( "id", StringComparison.OrdinalIgnoreCase ) ) return true;
		}
		return false;
	}

	private static IEnumerable<JsonElement> EnumerateDiagnostics( JsonElement payload )
	{
		if ( payload.ValueKind != JsonValueKind.Object ) yield break;
		foreach ( var prop in payload.EnumerateObject() )
		{
			if ( prop.NameEquals( "diagnostics" ) && prop.Value.ValueKind == JsonValueKind.Array )
				foreach ( var d in prop.Value.EnumerateArray() ) yield return d;
			if ( prop.Value.ValueKind == JsonValueKind.Object )
				foreach ( var d in EnumerateDiagnostics( prop.Value ) ) yield return d;
			if ( prop.Value.ValueKind == JsonValueKind.Array )
				foreach ( var item in prop.Value.EnumerateArray() )
					foreach ( var d in EnumerateDiagnostics( item ) ) yield return d;
		}
	}

	private void AppendPreflightLog( JsonElement preflight )
	{
		if ( preflight.TryGetProperty( "summary", out var summary ) && summary.ValueKind == JsonValueKind.Object )
		{
			var failed = summary.TryGetProperty( "failed", out var f ) && f.TryGetInt32( out var n ) ? n : 0;
			var total = summary.TryGetProperty( "total", out var t ) && t.TryGetInt32( out var tn ) ? tn : 0;
			_syncLog.Add( new SyncLogEntry { Name = "Preflight", Type = "Validation", Ok = failed == 0, Detail = failed == 0 ? $"Ready to push ({total})" : $"{failed}/{total} failed" } );
		}
	}

	private static bool IsResourceSyncLog( SyncLogEntry entry ) => entry.Type == "Endpoint" || entry.Type == "Collection" || entry.Type == "Workflow";
}
