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
			// A refused preflight answers HTTP 400 with the same findings, so script findings are read from that body too.
			if ( SyncToolApi.LastErrorBody is JsonElement refusalBody && BlockPushForEndpointScripts( refusalBody, endpointIds ) )
				return false;
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

		// Script findings block the endpoint they name, whatever their severity or the response's `ok`.
		if ( BlockPushForEndpointScripts( resp.Value, endpointIds ) )
			return false;
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
			if ( file != null && SyncToolConfig.TryLoadSourcePayloadResource( "endpoint", file, out var ep, includeDeprecated: true ) )
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

		// Warnings never block a push, but they are real problems the server will hit at run time
		// (for example MISSING_CHECK), so show them next to the preflight result.
		var seen = new HashSet<string>();
		foreach ( var diagnostic in EnumerateDiagnostics( preflight ) )
		{
			if ( diagnostic.ValueKind != JsonValueKind.Object ) continue;
			var severity = diagnostic.TryGetProperty( "severity", out var sev ) && sev.ValueKind == JsonValueKind.String ? sev.GetString() : null;
			if ( !string.Equals( severity, "warning", StringComparison.OrdinalIgnoreCase ) ) continue;

			var code = diagnostic.TryGetProperty( "code", out var c ) && c.ValueKind == JsonValueKind.String ? c.GetString() : "WARNING";
			var message = diagnostic.TryGetProperty( "message", out var m ) && m.ValueKind == JsonValueKind.String ? m.GetString() : "";
			var resource = diagnostic.TryGetProperty( "resourceId", out var r ) && r.ValueKind == JsonValueKind.String ? r.GetString() : "";
			var path = diagnostic.TryGetProperty( "sourcePath", out var p ) && p.ValueKind == JsonValueKind.String ? p.GetString() : "";
			// Script findings name the script file and the position inside it, not the envelope path.
			var isScript = IsEndpointScriptCode( code );
			var location = isScript ? ScriptDiagnosticLocation( resource, diagnostic ) : $"{resource}{path}";
			if ( !seen.Add( $"{resource}|{code}|{location}" ) ) continue;

			Log.Warning( $"[SyncTool] Preflight warning {code} in {location}: {message}" );
			var detail = isScript ? $"{code} at {location}: {message}" : $"{code}: {message}";
			_syncLog.Add( new SyncLogEntry { Name = string.IsNullOrEmpty( resource ) ? "Preflight warning" : resource, Type = "Validation", Ok = true, Warn = true, Detail = detail } );
		}
	}

	/// <summary>Server findings that mean an endpoint's script cannot run. They block the push whatever their severity.</summary>
	private static bool IsEndpointScriptCode( string code ) => code is "SCRIPT_SYNTAX" or "SCRIPT_TOO_LARGE" or "SCRIPT_MODULE_UNSUPPORTED"
		or "GLOBAL_CYCLE" or "GLOBAL_NOT_FOUND" or "GLOBAL_EXPORT_NOT_FOUND";

	/// <summary>
	/// Where an endpoint script finding points: the script file, then the server's line and column inside it,
	/// as "endpoints/slug.endpoint.js:12:5". The server parses the script text it was sent, which is the file's
	/// own text, so the position applies to the file unchanged. Falls back to the server's file name, then the resource.
	/// </summary>
	private string ScriptDiagnosticLocation( string resource, JsonElement diagnostic )
	{
		var envelope = _endpointFiles.FirstOrDefault( f => ResourceIdFromFile( f, "endpoint" ) == resource );
		var script = envelope == null ? null : SyncToolConfig.EndpointScriptFileFor( envelope );
		var where = script != null ? ProjectRelativePath( script ) : ( DiagnosticString( diagnostic, "file" ) ?? resource );

		var line = diagnostic.TryGetProperty( "line", out var l ) && l.TryGetInt32( out var lineNumber ) ? lineNumber : 0;
		if ( line <= 0 ) return where;
		var column = diagnostic.TryGetProperty( "column", out var col ) && col.TryGetInt32( out var columnNumber ) ? columnNumber : 0;
		return $"{where}:{line}:{column}";
	}

	/// <summary>
	/// Endpoint script findings in a preflight response, keyed by endpoint slug. Each is one line, for example
	/// "SCRIPT_SYNTAX at endpoints/hello.endpoint.js:5:37: message". Several findings for one endpoint are joined by "; ".
	/// A finding with no resource id is kept under "" so the batch still reports it.
	/// </summary>
	private Dictionary<string, string> EndpointScriptBlocksFromPreflight( JsonElement preflight )
	{
		var blocks = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var seen = new HashSet<string>();
		foreach ( var diagnostic in EnumerateDiagnostics( preflight ) )
		{
			if ( diagnostic.ValueKind != JsonValueKind.Object ) continue;
			var code = DiagnosticString( diagnostic, "code" );
			if ( !IsEndpointScriptCode( code ) ) continue;
			var kind = DiagnosticString( diagnostic, "resourceKind" );
			if ( kind != null && !string.Equals( kind, "endpoint", StringComparison.OrdinalIgnoreCase ) ) continue;

			var resource = DiagnosticString( diagnostic, "resourceId" ) ?? "";
			var text = SingleLine( $"{code} at {ScriptDiagnosticLocation( resource, diagnostic )}: {DiagnosticString( diagnostic, "message" )}" );
			if ( !seen.Add( $"{resource}\n{text}" ) ) continue;
			blocks[resource] = blocks.TryGetValue( resource, out var earlier ) ? $"{earlier}; {text}" : text;
		}
		return blocks;
	}

	/// <summary>
	/// Blocks the push when a preflight or refusal body names endpoint script problems. Returns true when it blocked.
	/// </summary>
	private bool BlockPushForEndpointScripts( JsonElement body, IEnumerable<string> endpointIds )
	{
		var blocks = EndpointScriptBlocksFromPreflight( body );
		if ( blocks.Count == 0 )
			return false;

		var singleSlug = endpointIds?.FirstOrDefault();
		ReportPushBlocked( blocks, singleSlug == null ? null : $"ep_{singleSlug}" );
		SetStatus( "Push blocked: endpoint script problem", true );
		return true;
	}

	/// <summary>
	/// Records a push refused before anything was sent. Each endpoint named in <paramref name="problems"/> fails with
	/// its own message; every other row reads "not pushed (blocked)". A non-null <paramref name="singleItemId"/> limits
	/// the rows to that one item; null means the whole push-all batch.
	/// </summary>
	private void ReportPushBlocked( IReadOnlyDictionary<string, string> problems, string singleItemId = null )
	{
		var batch = singleItemId == null;
		var summary = $"Push blocked: {problems.Count} endpoint script problem(s). Nothing was sent.";
		_syncLog.Add( new SyncLogEntry { Name = "Preflight", Type = "Validation", Ok = false, Detail = summary } );

		var endpointSlugs = batch
			? GetActiveEndpointFiles().Select( f => ResourceIdFromFile( f, "endpoint" ) ).ToList()
			: singleItemId.StartsWith( "ep_" ) ? new List<string> { singleItemId[3..] } : new List<string>();
		foreach ( var slug in endpointSlugs )
		{
			var named = problems.TryGetValue( slug, out var problem );
			if ( named || !batch )
			{
				SetItemState( $"ep_{slug}", result: "FAIL", remoteDiffers: false, diffSummary: "" );
				_syncLog.Add( new SyncLogEntry { Name = $"{slug}.endpoint.yml", Type = "Endpoint", Ok = false, Detail = named ? problem : summary } );
			}
			else
				_syncLog.Add( new SyncLogEntry { Name = $"{slug}.endpoint.yml", Type = "Endpoint", Ok = false, Warn = true, Detail = BlockedRowDetail } );
		}

		if ( !batch ) return;
		foreach ( var file in _collectionFiles )
			_syncLog.Add( new SyncLogEntry { Name = $"{ResourceIdFromFile( file, "collection" )}.collection.yml", Type = "Collection", Ok = false, Warn = true, Detail = BlockedRowDetail } );
		foreach ( var file in _workflowFiles )
			_syncLog.Add( new SyncLogEntry { Name = $"{ResourceIdFromFile( file, "workflow" )}.workflow.yml", Type = "Workflow", Ok = false, Warn = true, Detail = BlockedRowDetail } );
	}

	private const string BlockedRowDetail = "not pushed (blocked)";

	/// <summary>Joins a message's lines into one, so it fits a one-line row.</summary>
	private static string SingleLine( string text ) =>
		string.Join( " ", (text ?? "").Split( new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ).Select( s => s.Trim() ) );

	private static string DiagnosticString( JsonElement diagnostic, string name ) =>
		diagnostic.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static bool IsResourceSyncLog( SyncLogEntry entry ) => entry.Type == "Endpoint" || entry.Type == "Collection" || entry.Type == "Workflow";
}
