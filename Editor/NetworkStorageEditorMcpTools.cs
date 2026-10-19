using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Editor.Mcp;
using Sandbox;

/// <summary>
/// Editor-only Network Storage tools. They reuse the Sync Tool and Test window code paths without UI:
/// the same local loading, batch payload, readback verification and retry schedule.
/// </summary>
[McpToolset( "network_storage", "Editor-side Network Storage tools: Sync Tool status, batch preflight, push with readback verification, auto-test and saved test runs. Start with ns_editor_status." )]
public static class NetworkStorageEditorMcpTools
{
	private static readonly int[] EndpointReadbackDelaysMs = { 1000, 2000, 3000, 5000, 8000 };
	private static readonly int[] CollectionReadbackDelaysMs = { 1000, 2000, 3000, 5000, 8000, 13000 };
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
	private static readonly Regex SecretPattern = new( @"apiKey=[^&\s""']+|sbox_(sk|pk)_[A-Za-z0-9_\-]+", RegexOptions.Compiled );

	private const string NoSourcesMessage = "No endpoint, collection or workflow source files found; nothing to push.";
	private const string HttpHint = "Game code cannot reach this base URL. Use a domain on any port, or loopback on ports 80/443/8080/8443; IP-literal hosts are refused.";

	/// <summary>
	/// Shows Sync Tool configuration (never keys), local source counts per kind, the library version and whether game
	/// code may reach the configured API. With checkServer=true it also validates the key against the server.
	/// Next: ns_sync_preflight, then ns_sync_push.
	/// </summary>
	/// <param name="checkServer">When true, also calls the server to validate the key and returns the info it reports.</param>
	[McpTool.ReadOnly( "ns_editor_status" )]
	public static async Task<object> ns_editor_status( bool checkServer = false )
	{
		SyncToolConfig.Load();

		object server = null;
		if ( checkServer )
		{
			var resp = await SyncToolApi.Validate();
			server = new
			{
				ok = resp.HasValue,
				errorCode = resp.HasValue ? null : SyncToolApi.LastErrorCode,
				errorMessage = resp.HasValue ? null : Redact( SyncToolApi.LastErrorMessage ),
				info = resp.HasValue ? ScalarFields( resp.Value ) : null,
			};
		}

		var httpAllowed = HttpAllowedFor( SyncToolConfig.BaseUrl, SyncToolConfig.ApiVersion );

		return new
		{
			config = new
			{
				projectId = SyncToolConfig.ProjectId,
				baseUrl = Redact( SyncToolConfig.BaseUrl ),
				apiVersion = SyncToolConfig.ApiVersion,
				publishTarget = SyncToolConfig.PublishTarget,
				hasSecretKey = !string.IsNullOrEmpty( SyncToolConfig.SecretKey ),
				hasPublicKey = SyncToolConfig.HasPublicKey,
				isValid = SyncToolConfig.IsValid,
			},
			// The files Push sends (typed and plain .yml/.yaml/.json), not only the YAML ones.
			localSources = new
			{
				endpoints = SyncToolConfig.LoadSourcePayloadResources( "endpoint", includeDeprecated: false ).Count,
				collections = SyncToolConfig.LoadSourcePayloadResources( "collection" ).Count,
				workflows = SyncToolConfig.LoadSourcePayloadResources( "workflow" ).Count,
				tests = SyncToolConfig.LoadTests().Count(),
			},
			libraryVersion = NetworkStorage.LibraryVersion,
			httpAllowed,
			hint = httpAllowed == false ? HttpHint : null,
			server,
		};
	}

	/// <summary>
	/// Dry-run of the batch push: builds the same endpoints/collections/workflows payload the Sync Tool pushes and asks
	/// the server to validate it. Returns the server summary and diagnostics. Writes nothing.
	/// Next: ns_sync_push when ok is true.
	/// </summary>
	/// <param name="target">Publish target: "live" (default) or "next".</param>
	[McpTool.ReadOnly( "ns_sync_preflight" )]
	public static async Task<object> ns_sync_preflight( string target = "live" )
	{
		RequireConfigured();

		var set = LoadPushSet();
		if ( set.Total == 0 )
			throw new InvalidOperationException( NoSourcesMessage );

		var resp = await SyncToolApi.PreflightSync( set.Payload, target );
		if ( !resp.HasValue )
			return Failed( target, "preflight", null );

		return new
		{
			ok = PreflightPassed( resp.Value ),
			target,
			counts = set.Counts,
			summary = Child( resp.Value, "summary" ),
			diagnostics = Child( resp.Value, "diagnostics" ),
		};
	}

	/// <summary>
	/// Validates, preflights and pushes all endpoints, collections and workflows in one batch. With verify=true it re-reads
	/// the server and compares every resource to local source, re-pushing and polling mismatches on the same schedule as the
	/// Sync Tool's Push Live. Returns per-step and per-resource results. Next: ns_auto_test or ns_run_tests.
	/// </summary>
	/// <param name="target">Publish target: "live" (default) or "next".</param>
	/// <param name="verify">When true (default), read back and compare each resource after the push.</param>
	[McpTool( "ns_sync_push" )]
	public static async Task<object> ns_sync_push( string target = "live", bool verify = true )
	{
		RequireConfigured();
		var steps = new List<object>();

		var validate = await SyncToolApi.Validate();
		if ( !validate.HasValue )
			return Failed( target, "validate", null, steps );
		steps.Add( new { name = "validate", ok = true } );

		var set = LoadPushSet();
		if ( set.Total == 0 )
			throw new InvalidOperationException( NoSourcesMessage );

		var preflight = await SyncToolApi.PreflightSync( set.Payload, target );
		if ( !preflight.HasValue )
			return Failed( target, "preflight", null, steps );
		if ( !PreflightPassed( preflight.Value ) )
			return Failed( target, "preflight", "Preflight reported failing resources. Run ns_sync_preflight for per-resource diagnostics.", steps );
		steps.Add( new { name = "preflight", ok = true, summary = Child( preflight.Value, "summary" ) } );

		var push = await SyncToolApi.PushSync( set.Payload, target );
		if ( !push.HasValue )
			throw new InvalidOperationException( $"Batch push failed ({SyncToolApi.LastErrorCode ?? "no code"}): {Redact( SyncToolApi.LastErrorMessage ) ?? "no message"}. Nothing was verified. ns_sync_push does not fall back to per-resource pushes." );
		steps.Add( new { name = "push", ok = true, counts = set.Counts } );

		if ( !verify )
			return new { ok = true, target, counts = set.Counts, steps };

		var verification = await VerifyPushAsync( target );
		steps.Add( new { name = "verify", ok = verification.Ok } );

		return new { ok = verification.Ok, target, counts = set.Counts, steps, verification = verification.Report };
	}

	/// <summary>
	/// Runs auto-test on the server for one endpoint, or for all endpoints when slug is empty. Endpoint test failures are
	/// reported in body, not as errors. Next: ns_run_tests for the saved suite.
	/// </summary>
	/// <param name="slug">Endpoint slug to auto-test. Empty runs auto-test for every endpoint.</param>
	/// <param name="target">Publish target: "live" (default) or "next".</param>
	[McpTool( "ns_auto_test" )]
	public static async Task<object> ns_auto_test( string slug = "", string target = "live" )
	{
		RequireConfigured();
		var body = string.IsNullOrWhiteSpace( slug ) ? "{}" : JsonSerializer.Serialize( new { slug } );
		var resp = await SyncToolApi.AutoTest( JsonSerializer.Deserialize<JsonElement>( body ), target );
		return ServerResult( resp, target );
	}

	/// <summary>
	/// Runs the saved test suite on the server (the Test window's Run All Tests). Results are in body.results.
	/// Test failures are reported as data, not errors. Next: ns_auto_test to generate tests for one endpoint.
	/// </summary>
	/// <param name="target">Publish target: "live" (default) or "next".</param>
	[McpTool( "ns_run_tests" )]
	public static async Task<object> ns_run_tests( string target = "live" )
	{
		RequireConfigured();
		var resp = await SyncToolApi.RunAllTests( JsonSerializer.Deserialize<JsonElement>( "{}" ), target );
		return ServerResult( resp, target );
	}

	// ── Push payload ──

	private sealed class PushSet
	{
		public List<JsonElement> Endpoints;
		public List<JsonElement> Collections;
		public List<JsonElement> Workflows;
		public JsonElement Payload;

		public int Total => Endpoints.Count + Collections.Count + Workflows.Count;
		public object Counts => new { endpoints = Endpoints.Count, collections = Collections.Count, workflows = Workflows.Count };
	}

	// Same batch the Sync Tool sends: a kind is included only when it has resources.
	private static PushSet LoadPushSet()
	{
		var set = new PushSet
		{
			Endpoints = SyncToolConfig.LoadSourcePayloadResources( "endpoint", includeDeprecated: false ),
			Collections = SyncToolConfig.LoadSourcePayloadResources( "collection" ),
			Workflows = SyncToolConfig.LoadSourcePayloadResources( "workflow" ),
		};

		var batch = new Dictionary<string, object>();
		if ( set.Endpoints.Count > 0 ) batch["endpoints"] = set.Endpoints;
		if ( set.Collections.Count > 0 ) batch["collections"] = set.Collections;
		if ( set.Workflows.Count > 0 ) batch["workflows"] = set.Workflows;

		set.Payload = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( batch ) );
		return set;
	}

	// ── Readback verification (mirrors SyncToolWindow.VerifyPushResults and its retry schedule) ──

	private sealed class KindReport
	{
		public int Verified;
		public readonly List<string> RemoteAdditive = new();
		public readonly List<string> Mismatches = new();
		public readonly List<string> Missing = new();

		public bool Ok => Mismatches.Count == 0 && Missing.Count == 0;

		public object ToReport() => new
		{
			ok = Ok,
			verified = Verified,
			remoteAdditive = RemoteAdditive,
			mismatches = Mismatches,
			missing = Missing,
		};
	}

	private static async Task<(bool Ok, object Report)> VerifyPushAsync( string target )
	{
		// Local expectations, built the same way as the Sync Tool before a push.
		var epNorm = new Dictionary<string, string>();
		var epSource = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var ep in SyncToolConfig.LoadEndpoints() )
		{
			var slug = StringField( ep, "slug" );
			if ( string.IsNullOrEmpty( slug ) ) continue;
			epNorm[slug] = SyncToolVerification.NormalizeJson( JsonSerializer.Serialize( SyncToolTransforms.ServerEndpointToLocal( ep ) ) );
			if ( SyncToolTransforms.TryGetSourceText( ep, out var src ) )
				epSource[slug] = SyncToolVerification.NormalizeSourceTextForVerification( src );
		}

		var colNorm = new Dictionary<string, string>();
		var colSource = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (name, data) in SyncToolConfig.LoadCollections() )
		{
			var stripped = SyncToolTransforms.StripServerManagedFields( data );
			colNorm[name] = SyncToolVerification.NormalizeJson( JsonSerializer.Serialize( stripped, Indented ) );
			if ( data.TryGetValue( "sourceText", out var st ) && st is string source && !string.IsNullOrWhiteSpace( source ) )
				colSource[name] = SyncToolVerification.NormalizeSourceTextForVerification( source );
		}

		var wfNorm = new Dictionary<string, string>();
		var wfSource = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var wf in SyncToolConfig.LoadWorkflows() )
		{
			var id = StringField( wf, "id" );
			if ( string.IsNullOrEmpty( id ) ) continue;
			wfNorm[id] = SyncToolVerification.NormalizeJson( JsonSerializer.Serialize( SyncToolTransforms.ServerWorkflowToLocal( wf ) ) );
			if ( SyncToolTransforms.TryGetSourceText( wf, out var src ) )
				wfSource[id] = SyncToolVerification.NormalizeSourceTextForVerification( src );
		}

		var epRows = Rows( await SyncToolApi.GetEndpointsForPublishTarget( target ) );
		var colRows = Rows( await SyncToolApi.GetCollectionsForPublishTarget( target ) );
		var wfRows = Rows( await SyncToolApi.GetWorkflows() );
		if ( epRows.ValueKind != JsonValueKind.Array || colRows.ValueKind != JsonValueKind.Array || wfRows.ValueKind != JsonValueKind.Array )
		{
			return (false, new
			{
				readbackAvailable = false,
				errorCode = SyncToolApi.LastErrorCode,
				errorMessage = Redact( SyncToolApi.LastErrorMessage ),
			});
		}

		// Endpoints: first pass, then re-push and poll the mismatches.
		var epReport = new KindReport();
		foreach ( var slug in epNorm.Keys )
		{
			var row = FindRow( epRows, slug, "slug" );
			if ( row.ValueKind != JsonValueKind.Object ) { epReport.Missing.Add( slug ); continue; }
			Record( epReport, slug, SyncToolVerification.MatchEndpoint( slug, row, epNorm, epSource ), epNorm[slug], EndpointNorm( row ), generated: false );
		}

		if ( epReport.Mismatches.Count > 0 )
		{
			var pending = epReport.Mismatches.ToList();
			epReport.Mismatches.Clear();
			await RePushEndpoints( pending, target );
			await ReadbackRetry( epReport, pending, EndpointReadbackDelaysMs,
				() => SyncToolApi.GetEndpointsForPublishTarget( target ), "slug",
				( slug, row ) => SyncToolVerification.MatchEndpoint( slug, row, epNorm, epSource ) != SyncToolVerification.Match.None );
		}

		// Collections: first pass, then poll the mismatches.
		var colReport = new KindReport();
		foreach ( var name in colNorm.Keys )
		{
			var row = FindRow( colRows, name, "name" );
			if ( row.ValueKind != JsonValueKind.Object ) { colReport.Missing.Add( name ); continue; }
			Record( colReport, name, SyncToolVerification.MatchCollection( name, row, colNorm, colSource ), colNorm[name], CollectionNorm( row ),
				generated: SyncToolVerification.IsGeneratedMappedCollectionId( name ) );
		}

		if ( colReport.Mismatches.Count > 0 )
		{
			var pending = colReport.Mismatches.ToList();
			colReport.Mismatches.Clear();
			await ReadbackRetry( colReport, pending, CollectionReadbackDelaysMs,
				() => SyncToolApi.GetCollectionsForPublishTarget( target ), "name",
				( name, row ) => SyncToolVerification.MatchCollection( name, row, colNorm, colSource ) != SyncToolVerification.Match.None );
		}

		// Workflows: single readback, no retry (matches the Sync Tool).
		var wfReport = new KindReport();
		foreach ( var id in wfNorm.Keys )
		{
			var row = FindRow( wfRows, id, "id" );
			if ( row.ValueKind != JsonValueKind.Object ) { wfReport.Missing.Add( id ); continue; }
			var remoteNorm = WorkflowNorm( row );
			var remoteSource = SyncToolTransforms.TryGetSourceText( row, out var rs ) ? rs : null;
			var match = SyncToolVerification.CompareResource( wfNorm[id], wfSource.GetValueOrDefault( id ), remoteNorm, remoteSource, collectionSemantics: false );
			Record( wfReport, id, match, wfNorm[id], remoteNorm, generated: false );
		}

		var ok = epReport.Ok && colReport.Ok && wfReport.Ok;
		return (ok, new
		{
			endpoints = epReport.ToReport(),
			collections = colReport.ToReport(),
			workflows = wfReport.ToReport(),
		});
	}

	private static void Record( KindReport report, string name, SyncToolVerification.Match match, string localNorm, string remoteNorm, bool generated )
	{
		if ( match != SyncToolVerification.Match.None )
		{
			report.Verified++;
			return;
		}

		// Remote-only additions are not a failed push. Generated collections never fail on them.
		var analysis = JsonDiffUtilities.Analyze( localNorm, remoteNorm );
		if ( analysis.IsRemoteAdditiveOnly )
		{
			if ( generated ) report.Verified++;
			else report.RemoteAdditive.Add( name );
			return;
		}

		report.Mismatches.Add( name );
	}

	private static async Task RePushEndpoints( List<string> slugs, string target )
	{
		var scriptProblems = SyncToolConfig.EndpointScriptProblems();
		if ( scriptProblems.Count > 0 )
		{
			Log.Warning( $"[NetworkStorage MCP] Verification re-push skipped: {scriptProblems[0]}" );
			return;
		}

		var payloadBySlug = SyncToolConfig.LoadSourcePayloadResources( "endpoint", includeDeprecated: false )
			.Where( e => StringField( e, "slug" ) != null )
			.ToDictionary( e => StringField( e, "slug" ), StringComparer.OrdinalIgnoreCase );

		foreach ( var slug in slugs )
		{
			if ( !payloadBySlug.TryGetValue( slug, out var localPayload ) ) continue;
			try
			{
				var dict = JsonSerializer.Deserialize<Dictionary<string, object>>( localPayload.GetRawText() );
				var patch = await SyncToolApi.PatchEndpoint( JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( new { endpoint = dict } ) ), target );
				if ( patch.HasValue ) continue;
				await SyncToolApi.PushEndpoints( JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( new[] { dict } ) ), target );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[NetworkStorage MCP] Verification re-push for {slug} failed: {ex.Message}" );
			}
		}
	}

	private static async Task ReadbackRetry( KindReport report, List<string> pending, int[] delaysMs, Func<Task<JsonElement?>> fetch, string keyProp, Func<string, JsonElement, bool> matches )
	{
		foreach ( var delayMs in delaysMs )
		{
			if ( pending.Count == 0 ) return;

			await Task.Delay( delayMs );

			var rows = Rows( await fetch() );
			if ( rows.ValueKind != JsonValueKind.Array ) continue;

			var remaining = new List<string>();
			foreach ( var name in pending )
			{
				var row = FindRow( rows, name, keyProp );
				if ( row.ValueKind == JsonValueKind.Object && matches( name, row ) ) report.Verified++;
				else remaining.Add( name );
			}
			pending = remaining;
		}

		report.Mismatches.AddRange( pending );
	}

	private static string EndpointNorm( JsonElement row ) => SyncToolVerification.NormalizeJson( JsonSerializer.Serialize( SyncToolTransforms.ServerEndpointToLocal( row ) ) );
	private static string CollectionNorm( JsonElement row ) => SyncToolVerification.NormalizeJson( JsonSerializer.Serialize( SyncToolTransforms.ServerCollectionToLocal( row ) ) );
	private static string WorkflowNorm( JsonElement row ) => SyncToolVerification.NormalizeJson( JsonSerializer.Serialize( SyncToolTransforms.ServerWorkflowToLocal( row ) ) );

	// ── Helpers ──

	private static void RequireConfigured()
	{
		SyncToolConfig.Load();
		if ( !SyncToolConfig.IsValid )
			throw new InvalidOperationException( "Sync Tool is not configured: set the project ID and secret key in the Sync Tool setup, then retry. ns_editor_status shows what is missing." );
	}

	private static bool PreflightPassed( JsonElement preflight )
	{
		if ( preflight.ValueKind == JsonValueKind.Object
			&& preflight.TryGetProperty( "summary", out var summary )
			&& summary.ValueKind == JsonValueKind.Object
			&& summary.TryGetProperty( "failed", out var failed )
			&& failed.TryGetInt32( out var failedCount ) )
			return failedCount == 0;

		return !( preflight.ValueKind == JsonValueKind.Object && preflight.TryGetProperty( "ok", out var ok ) && ok.ValueKind == JsonValueKind.False );
	}

	private static object Failed( string target, string step, string message, List<object> steps = null ) => new
	{
		ok = false,
		target,
		step,
		errorCode = SyncToolApi.LastErrorCode,
		errorMessage = Redact( message ?? SyncToolApi.LastErrorMessage ),
		steps,
	};

	private static object ServerResult( JsonElement? resp, string target )
	{
		if ( resp.HasValue )
			return new { ok = true, target, body = JsonNode.Parse( resp.Value.GetRawText() ) };

		return new
		{
			ok = false,
			target,
			errorCode = SyncToolApi.LastErrorCode,
			errorMessage = Redact( SyncToolApi.LastErrorMessage ),
		};
	}

	private static JsonElement Rows( JsonElement? resp )
	{
		if ( !resp.HasValue ) return default;
		var data = resp.Value;
		if ( data.ValueKind == JsonValueKind.Object && data.TryGetProperty( "data", out var inner ) ) data = inner;
		return data;
	}

	private static JsonElement FindRow( JsonElement rows, string key, string keyProp )
	{
		foreach ( var row in rows.EnumerateArray() )
		{
			if ( string.Equals( StringField( row, keyProp ), key, StringComparison.OrdinalIgnoreCase ) )
				return row;
		}
		return default;
	}

	private static string StringField( JsonElement el, string prop ) =>
		el.ValueKind == JsonValueKind.Object && el.TryGetProperty( prop, out var v ) && v.ValueKind == JsonValueKind.String
			? v.GetString()
			: null;

	private static JsonNode Child( JsonElement el, string prop ) =>
		el.ValueKind == JsonValueKind.Object && el.TryGetProperty( prop, out var v )
			? JsonNode.Parse( v.GetRawText() )
			: null;

	private static bool? HttpAllowedFor( string baseUrl, string apiVersion )
	{
		try
		{
			return Http.IsAllowed( new Uri( $"{baseUrl}/{apiVersion}/server-info" ) );
		}
		catch ( Exception )
		{
			return null;
		}
	}

	private static Dictionary<string, object> ScalarFields( JsonElement el )
	{
		var result = new Dictionary<string, object>();
		if ( el.ValueKind != JsonValueKind.Object ) return result;

		foreach ( var p in el.EnumerateObject() )
		{
			if ( Regex.IsMatch( p.Name, "key|secret|token|password", RegexOptions.IgnoreCase ) ) continue;

			if ( p.Value.ValueKind == JsonValueKind.String ) result[p.Name] = Redact( p.Value.GetString() );
			else if ( p.Value.ValueKind == JsonValueKind.Number ) result[p.Name] = p.Value.GetDouble();
			else if ( p.Value.ValueKind == JsonValueKind.True || p.Value.ValueKind == JsonValueKind.False ) result[p.Name] = p.Value.GetBoolean();
		}
		return result;
	}

	private static string Redact( string text ) =>
		text == null ? null : SecretPattern.Replace( text, m => m.Value.StartsWith( "apiKey=", StringComparison.Ordinal ) ? "apiKey=REDACTED" : "sbox_REDACTED" );
}
