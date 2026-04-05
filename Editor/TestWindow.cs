using Sandbox;
using Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

/// <summary>
/// Editor window for testing Network Storage endpoints via dry-run.
/// Proper dropdown for endpoint selection, smart input generation from game values.
/// </summary>
[Dock( "Editor", "Network Storage Endpoint Tests", "bug_report" )]
public class TestWindow : DockWindow
{
	private bool _busy;
	private string _status = "";
	private float _scrollY;
	private float _scrollAreaTop;
	private float _contentHeight;
	private Vector2 _mousePos;
	private List<ClickRegion> _buttons = new();

	private List<JsonElement> _endpoints = new();
	private List<JsonElement> _tests = new();
	private int _selectedEndpointIdx = -1;
	private string _selectedTestId;
	private Rect _epDropdownScreenRect;

	private TextEdit _inputJson;
	private bool _skipWebhooks = true;

	private JsonElement? _lastResult;
	private string _lastError;
	private string _resultFilter = "all"; // "all", "failed", "passed"
	private bool _runAllFinished;
	private bool _reportReady;
	private string _reportPath;
	private List<JsonElement> _liveResults = new();
	private int _livePassedCount;
	private int _liveFailedCount;
	private CancellationTokenSource _cts;

	// Game values for smart input generation
	private JsonElement? _gameValuesRaw;

	private struct ClickRegion
	{
		public Rect Rect;
		public string Id;
		public Action OnClick;
	}

	public TestWindow()
	{
		Title = "Endpoint Tester";
		Size = new Vector2( 420, 640 );
		MinimumSize = new Vector2( 350, 400 );
		LoadData();
	}

	// TODO: WIP — re-enable menu entry when Endpoint Tester UI is ready
	// [Menu( "Editor", "Network Storage/Endpoint Tests" )]
	public static void OpenWindow()
	{
		var window = new TestWindow();
		window.Show();
	}

	private void LoadData()
	{
		try
		{
			_endpoints = SyncToolConfig.LoadEndpoints();
			_tests = SyncToolConfig.LoadTests();
			_gameValuesRaw = LoadGameValues();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TestWindow] Load failed: {ex.Message}" );
		}

		if ( _inputJson == null )
		{
			_inputJson = new TextEdit( this );
			_inputJson.PlaceholderText = "Select an endpoint to auto-generate input";
			_inputJson.PlainText = "{}";
		}

		Update();
	}

	/// <summary>Load game_values.json to resolve IDs for smart input generation.</summary>
	private JsonElement? LoadGameValues()
	{
		var collections = SyncToolConfig.LoadCollections();
		foreach ( var (name, data) in collections )
		{
			if ( name != "game_values" ) continue;
			// Re-serialize Dictionary<string,object> → JsonElement
			var json = JsonSerializer.Serialize( data );
			return JsonSerializer.Deserialize<JsonElement>( json );
		}
		return null;
	}

	/// <summary>Get rows from a table in game_values.</summary>
	private List<JsonElement> GetTableRows( string tableId )
	{
		if ( !_gameValuesRaw.HasValue ) return new();

		var gv = _gameValuesRaw.Value;
		// Check tables array
		if ( gv.TryGetProperty( "tables", out var tables ) && tables.ValueKind == JsonValueKind.Array )
		{
			foreach ( var table in tables.EnumerateArray() )
			{
				var id = table.TryGetProperty( "id", out var tid ) ? tid.GetString() : "";
				if ( id != tableId ) continue;
				if ( table.TryGetProperty( "rows", out var rows ) && rows.ValueKind == JsonValueKind.Array )
					return rows.EnumerateArray().ToList();
			}
		}
		return new();
	}

	/// <summary>Try to find a real ID value for a field name from game values tables.</summary>
	private string FindIdValue( string fieldName )
	{
		var clean = fieldName.Replace( "_id", "" ).Replace( "Id", "" );
		// Try common table name patterns
		var tableGuesses = new[] { clean + "_types", clean + "s", clean };

		foreach ( var tableName in tableGuesses )
		{
			var rows = GetTableRows( tableName );
			if ( rows.Count == 0 ) continue;
			var first = rows[0];
			// Try common ID field patterns
			foreach ( var idField in new[] { fieldName, clean + "Id", clean + "_id", "id", clean } )
			{
				if ( first.TryGetProperty( idField, out var val ) && val.ValueKind == JsonValueKind.String )
					return val.GetString();
			}
		}

		// Special: factions
		if ( fieldName.Contains( "faction" ) )
		{
			var rows = GetTableRows( "factions" );
			if ( rows.Count > 0 )
			{
				var first = rows[0];
				if ( first.TryGetProperty( "factionId", out var fid ) ) return fid.GetString();
				if ( first.TryGetProperty( "id", out var id ) ) return id.GetString();
			}
		}

		return null;
	}

	/// <summary>Generate smart input JSON for an endpoint using schema + game values + condition scanning.</summary>
	private string GenerateSmartInput( JsonElement endpoint )
	{
		if ( !endpoint.TryGetProperty( "input", out var inputSchema ) ) return "{}";
		if ( !inputSchema.TryGetProperty( "properties", out var props ) ) return "{}";

		var input = new Dictionary<string, object>();
		foreach ( var prop in props.EnumerateObject() )
		{
			var type = prop.Value.TryGetProperty( "type", out var t ) ? t.GetString() : "string";

			if ( type == "string" )
			{
				var resolved = FindIdValue( prop.Name );
				if ( resolved != null )
					input[prop.Name] = resolved;
				else if ( prop.Value.TryGetProperty( "default", out var def ) )
					input[prop.Name] = def.GetString();
				else if ( prop.Value.TryGetProperty( "enum", out var enumVals ) && enumVals.ValueKind == JsonValueKind.Array )
				{
					var first = enumVals.EnumerateArray().FirstOrDefault();
					input[prop.Name] = first.ValueKind == JsonValueKind.String ? first.GetString() : "";
				}
				else
					input[prop.Name] = prop.Name;
			}
			else if ( type == "number" )
			{
				if ( prop.Value.TryGetProperty( "default", out var def ) )
					input[prop.Name] = def.GetDouble();
				else if ( prop.Value.TryGetProperty( "min", out var min ) )
					input[prop.Name] = Math.Max( min.GetDouble(), 1.0 );
				else
					input[prop.Name] = 5.0;
			}
			else if ( type == "boolean" )
			{
				input[prop.Name] = prop.Value.TryGetProperty( "default", out var def ) && def.GetBoolean();
			}
			else if ( type == "array" )
			{
				input[prop.Name] = Array.Empty<object>();
			}
		}

		// Scan condition steps for input value constraints (e.g., any: [input.ore_id == "unidentified_t1"])
		if ( endpoint.TryGetProperty( "steps", out var steps ) && steps.ValueKind == JsonValueKind.Array )
		{
			foreach ( var step in steps.EnumerateArray() )
			{
				if ( !step.TryGetProperty( "type", out var stepType ) || stepType.GetString() != "condition" ) continue;
				if ( !step.TryGetProperty( "check", out var check ) ) continue;
				ScanCheckForConstraints( check, input );
			}
		}

		return JsonSerializer.Serialize( input, new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Scan a condition check for input value constraints (any: [input.X == "literal"]).</summary>
	private void ScanCheckForConstraints( JsonElement check, Dictionary<string, object> input )
	{
		if ( check.TryGetProperty( "any", out var any ) && any.ValueKind == JsonValueKind.Array )
		{
			foreach ( var sub in any.EnumerateArray() )
			{
				var field = sub.TryGetProperty( "field", out var f ) ? f.GetString() : "";
				var op = sub.TryGetProperty( "op", out var o ) ? o.GetString() : "";
				if ( op != "==" && op != "eq" ) continue;
				if ( !sub.TryGetProperty( "value", out var val ) || val.ValueKind != JsonValueKind.String ) continue;
				var valStr = val.GetString();
				if ( string.IsNullOrEmpty( field ) || string.IsNullOrEmpty( valStr ) || valStr.Contains( "{{" ) ) continue;

				if ( field.StartsWith( "input." ) )
				{
					var inputField = field.Substring( 6 );
					if ( input.ContainsKey( inputField ) )
					{
						input[inputField] = valStr;
						return;
					}
				}
			}
		}
		if ( check.TryGetProperty( "all", out var all ) && all.ValueKind == JsonValueKind.Array )
		{
			foreach ( var sub in all.EnumerateArray() )
				ScanCheckForConstraints( sub, input );
		}
	}

	// ──────────────────────────────────────────────────────
	//  Paint
	// ──────────────────────────────────────────────────────

	protected override void OnPaint()
	{
		base.OnPaint();
		_buttons.Clear();
		var pad = 20f;
		var w = Width - pad * 2;
		var y = 38f;

		// ── Title ──
		Paint.SetDefaultFont( size: 14, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w, 24 ), "Endpoint Tester", TextFlag.LeftCenter );
		y += 32;

		if ( !SyncToolConfig.IsValid )
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( new Color( 1f, 0.4f, 0.4f ) );
			Paint.DrawText( new Rect( pad, y, w, 16 ), "Configure credentials in Network Storage Setup first.", TextFlag.LeftCenter );
			_contentHeight = y + 20;
			return;
		}

		_scrollAreaTop = y;
		y -= _scrollY;

		// ── QUICK TEST ──
		DrawSectionHeader( ref y, pad, w, "QUICK TEST" );

		// Endpoint dropdown button
		Paint.SetDefaultFont( size: 10 );
		Paint.SetPen( Color.White.WithAlpha( 0.6f ) );
		Paint.DrawText( new Rect( pad, y, 70, 20 ), "Endpoint:", TextFlag.LeftCenter );

		var epName = _selectedEndpointIdx >= 0 && _selectedEndpointIdx < _endpoints.Count
			? GetEpLabel( _endpoints[_selectedEndpointIdx] )
			: "Select endpoint...";

		var epRect = new Rect( pad + 72, y, w - 72, 22 );
		var epHovered = epRect.IsInside( _mousePos );
		Paint.SetBrush( Color.White.WithAlpha( epHovered ? 0.12f : 0.06f ) );
		Paint.SetPen( Color.Cyan.WithAlpha( epHovered ? 0.5f : 0.25f ) );
		Paint.DrawRect( epRect, 3 );
		Paint.SetDefaultFont( size: 10 );
		Paint.SetPen( _selectedEndpointIdx >= 0 ? Color.Cyan : Color.White.WithAlpha( 0.4f ) );
		Paint.DrawText( new Rect( epRect.Left + 8, epRect.Top, epRect.Width - 24, epRect.Height ), epName, TextFlag.LeftCenter );
		// Dropdown arrow
		Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
		Paint.DrawText( new Rect( epRect.Right - 20, epRect.Top, 16, epRect.Height ), "v", TextFlag.Center );
		// Store screen-space position for OpenAt
		_epDropdownScreenRect = new Rect( epRect.Left, epRect.Bottom + _scrollY, epRect.Width, 0 );
		_buttons.Add( new ClickRegion { Rect = epRect, Id = "dropdown_ep", OnClick = ShowEndpointDropdown } );
		y += 28;

		// Input JSON
		Paint.SetPen( Color.White.WithAlpha( 0.6f ) );
		Paint.DrawText( new Rect( pad, y, w, 16 ), "Input JSON:", TextFlag.LeftCenter );
		y += 18;

		// Position TextEdit at the visual y (already scroll-adjusted)
		var inputScreenY = y;
		var inputH = 54f; // 3 lines tall
		_inputJson.Position = new Vector2( pad, inputScreenY );
		_inputJson.Size = new Vector2( w, inputH );
		_inputJson.Visible = inputScreenY > _scrollAreaTop && (inputScreenY + inputH) < Height;
		y += inputH + 6;

		// Skip webhooks
		DrawCheckbox( ref y, pad, w, "Skip Webhooks", _skipWebhooks, () => { _skipWebhooks = !_skipWebhooks; Update(); } );
		y += 6;

		// Run button
		DrawButton( ref y, pad, w, "Run Quick Test", Color.Cyan, "run_quick", _busy ? null : RunQuickTest );
		y += 8;

		DrawSeparator( ref y, w, pad );

		// ── SAVED TESTS ──
		DrawSectionHeader( ref y, pad, w, $"SAVED TESTS ({_tests.Count})" );

		if ( _tests.Count > 0 )
		{
			foreach ( var test in _tests )
			{
				DrawTestRow( ref y, pad, w, test );
			}

			y += 4;
			DrawButton( ref y, pad, w, "Run All Tests", new Color( 0.2f, 0.8f, 0.4f ), "run_all", _busy ? null : RunAllTests );
		}
		else
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.3f ) );
			Paint.DrawText( new Rect( pad + 8, y, w, 16 ), "No test files in tests/", TextFlag.LeftCenter );
			y += 22;
		}

		y += 4;
		DrawButton( ref y, pad, w, "Run All + Auto-Generate Preset Tests", new Color( 0.6f, 0.4f, 1f ), "run_all_auto", _busy ? null : RunAllWithAutoGenerate );

		y += 8;
		DrawSeparator( ref y, w, pad );

		// ── RESULTS ──
		if ( _lastResult.HasValue || !string.IsNullOrEmpty( _lastError ) || _liveResults.Count > 0 )
		{
			DrawSectionHeader( ref y, pad, w, "RESULTS" );
			DrawResults( ref y, pad, w );
		}

		if ( !string.IsNullOrEmpty( _status ) )
		{
			Paint.SetDefaultFont( size: 9 );
			Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
			Paint.DrawText( new Rect( pad, y, w, 14 ), _status, TextFlag.LeftCenter );
			y += 20;
		}

		_contentHeight = y + _scrollY + 80;

		// Fixed header redraw
		Paint.SetBrush( new Color( 0.133f, 0.133f, 0.133f ) );
		Paint.ClearPen();
		Paint.DrawRect( new Rect( 0, 0, Width, _scrollAreaTop ) );
		Paint.SetDefaultFont( size: 14, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, 38, w, 24 ), "Endpoint Tester", TextFlag.LeftCenter );
	}

	private void DrawTestRow( ref float y, float pad, float w, JsonElement test )
	{
		var testId = test.TryGetProperty( "id", out var tid ) ? tid.GetString() : "";
		var testName = test.TryGetProperty( "name", out var tn ) ? tn.GetString() : testId;
		var endpoint = test.TryGetProperty( "endpoint", out var ep ) ? ep.GetString() : "";
		var expect = test.TryGetProperty( "expect", out var ex ) && ex.TryGetProperty( "outcome", out var oc ) ? oc.GetString() : "pass";

		var rowRect = new Rect( pad, y, w, 34 );
		var rowHovered = rowRect.IsInside( _mousePos );
		var isSelected = testId == _selectedTestId;

		Paint.SetBrush( isSelected ? Color.Cyan.WithAlpha( 0.08f ) : rowHovered ? Color.White.WithAlpha( 0.04f ) : Color.Transparent );
		Paint.SetPen( isSelected ? Color.Cyan.WithAlpha( 0.3f ) : Color.White.WithAlpha( 0.05f ) );
		Paint.DrawRect( rowRect, 3 );

		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad + 6, y + 2, w - 80, 16 ), testName ?? "", TextFlag.LeftCenter );

		Paint.SetDefaultFont( size: 8 );
		Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
		Paint.DrawText( new Rect( pad + 6, y + 17, w - 80, 14 ), endpoint, TextFlag.LeftCenter );

		var badgeColor = expect == "fail" ? new Color( 1f, 0.3f, 0.3f ) : expect == "any" ? new Color( 1f, 0.7f, 0.2f ) : new Color( 0.2f, 0.8f, 0.4f );
		Paint.SetBrush( badgeColor.WithAlpha( 0.15f ) );
		Paint.SetPen( badgeColor );
		var badgeRect = new Rect( pad + w - 70, y + 8, 60, 16 );
		Paint.DrawRect( badgeRect, 8 );
		Paint.SetDefaultFont( size: 7 );
		Paint.DrawText( badgeRect, expect, TextFlag.Center );

		_buttons.Add( new ClickRegion { Rect = rowRect, Id = $"run_test_{testId}", OnClick = () => RunSavedTest( testId ) } );
		y += 38;
	}

	private void DrawCheckbox( ref float y, float pad, float w, string label, bool value, Action toggle )
	{
		var boxRect = new Rect( pad, y, 16, 16 );
		Paint.SetBrush( value ? Color.Cyan.WithAlpha( 0.3f ) : Color.White.WithAlpha( 0.05f ) );
		Paint.SetPen( value ? Color.Cyan : Color.White.WithAlpha( 0.3f ) );
		Paint.DrawRect( boxRect, 2 );
		if ( value )
		{
			Paint.SetPen( Color.Cyan, 2f );
			Paint.DrawLine( new Vector2( pad + 3, y + 8 ), new Vector2( pad + 7, y + 12 ) );
			Paint.DrawLine( new Vector2( pad + 7, y + 12 ), new Vector2( pad + 13, y + 4 ) );
		}
		_buttons.Add( new ClickRegion { Rect = new Rect( pad, y, w, 16 ), Id = $"chk_{label}", OnClick = toggle } );
		Paint.SetDefaultFont( size: 10 );
		Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad + 22, y, w, 16 ), label, TextFlag.LeftCenter );
		y += 22;
	}

	private void DrawResults( ref float y, float pad, float w )
	{
		if ( !string.IsNullOrEmpty( _lastError ) )
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( new Color( 1f, 0.3f, 0.3f ) );
			Paint.DrawText( new Rect( pad + 4, y, w - 8, 14 ), $"Error: {_lastError}", TextFlag.LeftCenter );
			y += 20;
			return;
		}

		// ── Run-All results (live or finished) ──
		if ( _liveResults.Count > 0 )
		{
			var total = _liveResults.Count;
			var passedCount = _livePassedCount;
			var failedCount = _liveFailedCount;

			// Summary line
			Paint.SetDefaultFont( size: 11, weight: 700 );
			Paint.SetPen( Color.White );
			var titleText = _runAllFinished ? $"Results: {total} tests" : $"Running... {total} tests so far";
			Paint.DrawText( new Rect( pad, y, w, 18 ), titleText, TextFlag.LeftCenter );
			y += 22;

			// Passed / Failed badges
			Paint.SetDefaultFont( size: 10, weight: 600 );
			Paint.SetBrush( new Color( 0.2f, 0.8f, 0.4f ).WithAlpha( 0.15f ) );
			Paint.SetPen( new Color( 0.2f, 0.8f, 0.4f ) );
			Paint.DrawRect( new Rect( pad, y, 80, 20 ), 4 );
			Paint.DrawText( new Rect( pad, y, 80, 20 ), $"Passed: {passedCount}", TextFlag.Center );

			Paint.SetBrush( new Color( 1f, 0.3f, 0.3f ).WithAlpha( 0.15f ) );
			Paint.SetPen( new Color( 1f, 0.3f, 0.3f ) );
			Paint.DrawRect( new Rect( pad + 88, y, 80, 20 ), 4 );
			Paint.DrawText( new Rect( pad + 88, y, 80, 20 ), $"Failed: {failedCount}", TextFlag.Center );

			if ( !_runAllFinished )
			{
				Paint.SetDefaultFont( size: 9 );
				Paint.SetPen( Color.Yellow.WithAlpha( 0.7f ) );
				Paint.DrawText( new Rect( pad + 180, y, 100, 20 ), "Running...", TextFlag.LeftCenter );
			}
			y += 28;

			// Filter buttons + Report button
			var filterBtnW = 70f;
			DrawFilterButton( ref y, pad, filterBtnW, "All", "all", total );
			DrawFilterButton( ref y, pad + filterBtnW + 4, filterBtnW, "Failed", "failed", failedCount );
			DrawFilterButton( ref y, pad + (filterBtnW + 4) * 2, filterBtnW, "Passed", "passed", passedCount );

			// Report button — shows state
			var reportRect = new Rect( pad + w - 110, y, 106, 18 );
			if ( _reportReady )
			{
				DrawSmallBtn( reportRect, "Open Report", Color.Cyan, "open_report", () => {
					if ( !string.IsNullOrEmpty( _reportPath ) ) EditorUtility.OpenFile( _reportPath );
				} );
			}
			else
			{
				Paint.SetBrush( Color.White.WithAlpha( 0.03f ) );
				Paint.SetPen( Color.White.WithAlpha( 0.15f ) );
				Paint.DrawRect( reportRect, 4 );
				Paint.SetDefaultFont( size: 9 );
				Paint.SetPen( Color.White.WithAlpha( 0.3f ) );
				Paint.DrawText( reportRect, _runAllFinished ? "Generating..." : "Running report...", TextFlag.Center );
			}
			y += 28;

			// Live test list
			foreach ( var test in _liveResults )
			{
				var testPassed = test.TryGetProperty( "passed", out var tp ) && tp.GetBoolean();
				if ( _resultFilter == "failed" && testPassed ) continue;
				if ( _resultFilter == "passed" && !testPassed ) continue;

				var testName = test.TryGetProperty( "name", out var tn ) ? tn.GetString() : "?";
				var testReason = test.TryGetProperty( "reason", out var tr ) ? tr.GetString() : null;
				var testTiming = test.TryGetProperty( "timing", out var ttm ) && ttm.TryGetProperty( "total", out var ttt ) ? ttt.GetDouble() : 0;

				var iconColor = testPassed ? new Color( 0.2f, 0.8f, 0.4f ) : new Color( 1f, 0.3f, 0.3f );
				Paint.SetDefaultFont( size: 9, weight: 700 );
				Paint.SetPen( iconColor );
				Paint.DrawText( new Rect( pad + 2, y, 12, 14 ), testPassed ? "+" : "x", TextFlag.Center );

				Paint.SetDefaultFont( size: 9, weight: 600 );
				Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
				Paint.DrawText( new Rect( pad + 16, y, w - 80, 14 ), testName, TextFlag.LeftCenter );

				Paint.SetDefaultFont( size: 8 );
				Paint.SetPen( Color.White.WithAlpha( 0.3f ) );
				Paint.DrawText( new Rect( pad + w - 50, y, 46, 14 ), $"{testTiming}ms", TextFlag.RightCenter );

				y += 16;

				if ( !string.IsNullOrEmpty( testReason ) )
				{
					Paint.SetDefaultFont( size: 8 );
					Paint.SetPen( new Color( 1f, 0.3f, 0.3f ).WithAlpha( 0.8f ) );
					var reasonH = Math.Max( 12, (int)Math.Ceiling( testReason.Length / 50.0 ) * 11 );
					Paint.DrawText( new Rect( pad + 16, y, w - 20, reasonH ), testReason, TextFlag.LeftTop | TextFlag.WordWrap );
					y += reasonH + 4;
				}
				else
				{
					y += 4;
				}
			}
			return;
		}

		// ── Single test result ──
		if ( !_lastResult.HasValue ) return;
		var result = _lastResult.Value;

		// Expectation badge
		if ( result.TryGetProperty( "expectation", out var exp ) && exp.ValueKind == JsonValueKind.Object )
		{
			var passed = exp.TryGetProperty( "passed", out var p ) && p.GetBoolean();
			var badgeColor = passed ? new Color( 0.2f, 0.8f, 0.4f ) : new Color( 1f, 0.3f, 0.3f );
			Paint.SetBrush( badgeColor.WithAlpha( 0.15f ) );
			Paint.SetPen( badgeColor );
			Paint.DrawRect( new Rect( pad, y, 60, 18 ), 8 );
			Paint.SetDefaultFont( size: 9, weight: 700 );
			Paint.DrawText( new Rect( pad, y, 60, 18 ), passed ? "PASSED" : "FAILED", TextFlag.Center );
			if ( !passed && exp.TryGetProperty( "reason", out var reason ) )
			{
				Paint.SetDefaultFont( size: 9 );
				Paint.SetPen( new Color( 1f, 0.3f, 0.3f ) );
				Paint.DrawText( new Rect( pad + 66, y, w - 66, 18 ), reason.GetString() ?? "", TextFlag.LeftCenter );
			}
			y += 24;
		}

		// Warnings
		if ( result.TryGetProperty( "warnings", out var warnings ) && warnings.ValueKind == JsonValueKind.Array )
		{
			foreach ( var warn in warnings.EnumerateArray() )
			{
				Paint.SetDefaultFont( size: 9 );
				Paint.SetPen( new Color( 1f, 0.7f, 0.2f ) );
				var warnText = warn.GetString() ?? "";
				var warnH = Math.Max( 14, (int)Math.Ceiling( warnText.Length / 55.0 ) * 13 );
				Paint.DrawText( new Rect( pad + 4, y, w - 8, warnH ), $"! {warnText}", TextFlag.LeftTop | TextFlag.WordWrap );
				y += warnH + 4;
			}
		}

		// Steps
		if ( result.TryGetProperty( "steps", out var steps ) && steps.ValueKind == JsonValueKind.Array )
		{
			foreach ( var step in steps.EnumerateArray() )
			{
				var stepId = step.TryGetProperty( "id", out var sid ) ? sid.GetString() : "?";
				var stepType = step.TryGetProperty( "type", out var st ) ? st.GetString() : "?";
				var hasWarning = step.TryGetProperty( "warning", out _ );
				var passed = !step.TryGetProperty( "passed", out var sp ) || sp.ValueKind != JsonValueKind.False;

				var iconColor = hasWarning ? new Color( 1f, 0.7f, 0.2f ) : !passed ? new Color( 1f, 0.3f, 0.3f ) : new Color( 0.2f, 0.8f, 0.4f );
				Paint.SetDefaultFont( size: 9, weight: 700 );
				Paint.SetPen( iconColor );
				Paint.DrawText( new Rect( pad + 2, y, 12, 14 ), hasWarning ? "!" : !passed ? "x" : "+", TextFlag.Center );

				Paint.SetDefaultFont( size: 9 );
				Paint.SetPen( Color.White.WithAlpha( 0.8f ) );
				Paint.DrawText( new Rect( pad + 16, y, 100, 14 ), stepId, TextFlag.LeftCenter );
				Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
				Paint.DrawText( new Rect( pad + 120, y, 70, 14 ), stepType, TextFlag.LeftCenter );

				if ( step.TryGetProperty( "result", out var res ) && res.ValueKind != JsonValueKind.Null )
				{
					if ( res.ValueKind == JsonValueKind.Object )
					{
						// For read steps (objects like player data), show key fields inline
						var keyValues = ExtractKeyFields( res );
						if ( !string.IsNullOrEmpty( keyValues ) )
						{
							Paint.SetPen( Color.Cyan.WithAlpha( 0.5f ) );
							Paint.SetDefaultFont( size: 8 );
							Paint.DrawText( new Rect( pad + 194, y, w - 194, 14 ), keyValues, TextFlag.LeftCenter );
						}
					}
					else if ( res.ValueKind != JsonValueKind.Array )
					{
						Paint.SetPen( Color.Cyan.WithAlpha( 0.7f ) );
						var resText = res.ToString();
						if ( resText.Length > 25 ) resText = resText[..25] + "...";
						Paint.DrawText( new Rect( pad + 194, y, w - 194, 14 ), $"= {resText}", TextFlag.LeftCenter );
					}
				}
				y += 22;

				// For read steps with object results, show important fields on separate lines
				if ( step.TryGetProperty( "result", out var readRes ) && readRes.ValueKind == JsonValueKind.Object && stepType == "read" )
				{
					var fields = GetRelevantReadFields( readRes );
					foreach ( var (fieldName, fieldValue) in fields )
					{
						Paint.SetDefaultFont( size: 8 );
						Paint.SetPen( Color.White.WithAlpha( 0.35f ) );
						Paint.DrawText( new Rect( pad + 28, y, 80, 12 ), fieldName, TextFlag.LeftCenter );
						Paint.SetPen( Color.Cyan.WithAlpha( 0.5f ) );
						var valText = fieldValue.Length > 40 ? fieldValue[..40] + "..." : fieldValue;
						Paint.DrawText( new Rect( pad + 110, y, w - 114, 12 ), valText, TextFlag.LeftCenter );
						y += 14;
					}
				}
			}
		}

		if ( result.TryGetProperty( "pendingWrites", out var pw ) && pw.ValueKind == JsonValueKind.Array && pw.GetArrayLength() > 0 )
		{
			Paint.SetDefaultFont( size: 9 );
			Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
			Paint.DrawText( new Rect( pad + 4, y, w, 14 ), $"{pw.GetArrayLength()} write operation(s) would execute", TextFlag.LeftCenter );
			y += 18;
		}

		// Open Output button
		y += 4;
		var copyRect = new Rect( pad, y, 100, 20 );
		DrawSmallBtn( copyRect, "Open Output", Color.White, "copy_output", CopyResultToClipboard );
		y += 26;
	}

	private void DrawFilterButton( ref float y, float x, float btnW, string label, string filter, int count )
	{
		var isActive = _resultFilter == filter;
		var rect = new Rect( x, y, btnW, 18 );
		var hovered = rect.IsInside( _mousePos );
		var color = isActive ? Color.Cyan : Color.White;

		Paint.SetBrush( color.WithAlpha( isActive ? 0.15f : hovered ? 0.08f : 0.03f ) );
		Paint.SetPen( color.WithAlpha( isActive ? 0.6f : 0.25f ) );
		Paint.DrawRect( rect, 3 );
		Paint.SetDefaultFont( size: 8, weight: isActive ? 700 : 400 );
		Paint.SetPen( color.WithAlpha( isActive ? 1f : 0.5f ) );
		Paint.DrawText( rect, $"{label} ({count})", TextFlag.Center );

		if ( !isActive )
			_buttons.Add( new ClickRegion { Rect = rect, Id = $"filter_{filter}", OnClick = () => { _resultFilter = filter; Update(); } } );
	}

	private void DrawSmallBtn( Rect rect, string label, Color color, string id, Action onClick )
	{
		var hovered = rect.IsInside( _mousePos );
		Paint.SetBrush( color.WithAlpha( hovered ? 0.1f : 0.04f ) );
		Paint.SetPen( color.WithAlpha( hovered ? 0.4f : 0.2f ) );
		Paint.DrawRect( rect, 3 );
		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( color.WithAlpha( 0.6f ) );
		Paint.DrawText( rect, $"  {label}", TextFlag.LeftCenter );
		_buttons.Add( new ClickRegion { Rect = rect, Id = id, OnClick = onClick } );
	}

	// Report generation moved to GenerateReportFile() called from RunAllTests

	private static void AppendTestMd( System.Text.StringBuilder sb, JsonElement test )
	{
		var name = test.TryGetProperty( "name", out var n ) ? n.GetString() : "?";
		var ep = test.TryGetProperty( "endpoint", out var e ) ? e.GetString() : "?";
		var method = test.TryGetProperty( "method", out var m ) ? m.GetString() : "POST";
		var ok = test.TryGetProperty( "passed", out var p ) && p.GetBoolean();
		var reason = test.TryGetProperty( "reason", out var r ) ? r.GetString() : null;

		sb.AppendLine( $"### {( ok ? "PASS" : "FAIL" )} - {name}" );
		sb.AppendLine( $"- **Endpoint:** `{method} {ep}`" );
		if ( !string.IsNullOrEmpty( reason ) ) sb.AppendLine( $"- **Reason:** {reason}" );
		if ( test.TryGetProperty( "input", out var inp ) )
		{
			sb.AppendLine( "- **Input:**" );
			sb.AppendLine( "```json" );
			sb.AppendLine( JsonSerializer.Serialize( inp, new JsonSerializerOptions { WriteIndented = true } ) );
			sb.AppendLine( "```" );
		}
		if ( test.TryGetProperty( "expect", out var exp ) )
			sb.AppendLine( $"- **Expected:** `{JsonSerializer.Serialize( exp )}`" );
		if ( test.TryGetProperty( "result", out var res ) )
		{
			sb.AppendLine( $"- **Result:** ok={( res.TryGetProperty( "ok", out var rok ) && rok.GetBoolean() )}, status={( res.TryGetProperty( "status", out var rs ) ? rs.GetInt32() : 0 )}" );
			if ( res.TryGetProperty( "body", out var body ) )
			{
				sb.AppendLine( "```json" );
				sb.AppendLine( JsonSerializer.Serialize( body, new JsonSerializerOptions { WriteIndented = true } ) );
				sb.AppendLine( "```" );
			}
		}
		if ( test.TryGetProperty( "warnings", out var ws ) && ws.ValueKind == JsonValueKind.Array && ws.GetArrayLength() > 0 )
		{
			sb.AppendLine( "- **Warnings:**" );
			foreach ( var w in ws.EnumerateArray() ) sb.AppendLine( $"  - {w.GetString()}" );
		}
		sb.AppendLine();
	}

	private void CopyResultToClipboard()
	{
		if ( !_lastResult.HasValue && string.IsNullOrEmpty( _lastError ) ) return;

		string text;
		if ( !string.IsNullOrEmpty( _lastError ) )
		{
			text = $"Error: {_lastError}";
		}
		else
		{
			text = JsonSerializer.Serialize( _lastResult.Value, new JsonSerializerOptions { WriteIndented = true } );
		}

		// Write to temp file and open it — s&box doesn't expose a clipboard API
		var tmpPath = Path.Combine( Path.GetTempPath(), "ns_test_output.json" );
		File.WriteAllText( tmpPath, text );
		EditorUtility.OpenFile( tmpPath );
		_status = $"Output saved to {tmpPath}";
		Update();
	}

	// ──────────────────────────────────────────────────────
	//  Drawing helpers
	// ──────────────────────────────────────────────────────

	private void DrawSectionHeader( ref float y, float pad, float w, string text )
	{
		Paint.SetDefaultFont( size: 9, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
		Paint.DrawText( new Rect( pad, y, w, 14 ), text, TextFlag.LeftCenter );
		y += 20;
	}

	private void DrawSeparator( ref float y, float w, float pad )
	{
		Paint.SetPen( Color.White.WithAlpha( 0.08f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 8;
	}

	private void DrawButton( ref float y, float pad, float w, string label, Color color, string id, Action onClick )
	{
		var rect = new Rect( pad, y, w, 26 );
		var hovered = rect.IsInside( _mousePos ) && onClick != null;
		Paint.SetBrush( color.WithAlpha( hovered ? 0.2f : 0.1f ) );
		Paint.SetPen( color.WithAlpha( hovered ? 0.6f : 0.3f ) );
		Paint.DrawRect( rect, 4 );
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( onClick != null ? color : color.WithAlpha( 0.3f ) );
		Paint.DrawText( rect, _busy ? "Running..." : label, TextFlag.Center );
		if ( onClick != null )
			_buttons.Add( new ClickRegion { Rect = rect, Id = id, OnClick = onClick } );
		y += 30;
	}

	// ──────────────────────────────────────────────────────
	//  Endpoint Dropdown
	// ──────────────────────────────────────────────────────

	private void ShowEndpointDropdown()
	{
		if ( _endpoints.Count == 0 ) return;

		var menu = new Menu( this );
		for ( var i = 0; i < _endpoints.Count; i++ )
		{
			var idx = i;
			var ep = _endpoints[i];
			var label = GetEpLabel( ep );
			menu.AddOption( label, "signpost_split", () => SelectEndpoint( idx ) );
		}

		// Open below the dropdown button
		var screenPos = ScreenPosition + new Vector2( _epDropdownScreenRect.Left, _epDropdownScreenRect.Top );
		menu.OpenAt( screenPos );
	}

	private void SelectEndpoint( int idx )
	{
		_selectedEndpointIdx = idx;
		if ( idx >= 0 && idx < _endpoints.Count )
		{
			_inputJson.PlainText = GenerateSmartInput( _endpoints[idx] );
		}
		Update();
	}

	// ──────────────────────────────────────────────────────
	//  Actions
	// ──────────────────────────────────────────────────────

	private async void RunQuickTest()
	{
		if ( _busy || _selectedEndpointIdx < 0 || _selectedEndpointIdx >= _endpoints.Count ) return;

		var ep = _endpoints[_selectedEndpointIdx];
		var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
		if ( string.IsNullOrEmpty( slug ) ) return;

		_busy = true;
		_status = "Running quick test...";
		_lastResult = null;
		_lastError = null;
		Update();

		try
		{
			var inputObj = new Dictionary<string, object>
			{
				["slug"] = slug,
				["skipWebhooks"] = _skipWebhooks,
			};

			try
			{
				var parsed = JsonSerializer.Deserialize<JsonElement>( _inputJson.PlainText ?? "{}" );
				inputObj["input"] = parsed;
			}
			catch
			{
				_lastError = "Invalid JSON in input field.";
				return;
			}

			var body = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( inputObj ) );
			var resp = await SyncToolApi.RunTest( body );
			if ( resp.HasValue )
				_lastResult = resp.Value;
			else
				_lastError = SyncToolApi.LastErrorMessage ?? "Request failed.";
		}
		catch ( Exception ex ) { _lastError = ex.Message; }
		finally { _busy = false; _status = ""; Update(); }
	}

	private async void RunSavedTest( string testId )
	{
		if ( _busy ) return;
		_busy = true;
		_selectedTestId = testId;
		_status = $"Running test...";
		_lastResult = null;
		_lastError = null;
		Update();

		try
		{
			var body = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( new { testId } ) );
			var resp = await SyncToolApi.RunTest( body );
			if ( resp.HasValue ) _lastResult = resp.Value;
			else _lastError = SyncToolApi.LastErrorMessage ?? "Request failed.";
		}
		catch ( Exception ex ) { _lastError = ex.Message; }
		finally { _busy = false; _status = ""; Update(); }
	}

	private async void RunAllTests()
	{
		if ( _busy ) return;
		_cts?.Cancel();
		_cts = new CancellationTokenSource();
		var token = _cts.Token;

		_busy = true;
		_runAllFinished = false;
		_reportReady = false;
		_reportPath = null;
		_liveResults.Clear();
		_livePassedCount = 0;
		_liveFailedCount = 0;
		_lastResult = null;
		_lastError = null;
		_status = "Running tests...";
		Update();

		try
		{
			// First try running saved tests (server-side)
			_status = "Running saved tests...";
			Update();

			var allBody = JsonSerializer.Deserialize<JsonElement>( "{}" );
			var allResp = await SyncToolApi.RunAllTests( allBody );

			if ( token.IsCancellationRequested ) { _busy = false; return; }

			if ( allResp.HasValue && allResp.Value.TryGetProperty( "results", out var savedResults ) && savedResults.ValueKind == JsonValueKind.Array )
			{
				foreach ( var test in savedResults.EnumerateArray() )
				{
					_liveResults.Add( test.Clone() );
					var passed = test.TryGetProperty( "passed", out var p ) && p.GetBoolean();
					if ( passed ) _livePassedCount++;
					else _liveFailedCount++;
				}
				Update();
			}

			// If no saved tests found, run a quick dry-run per endpoint
			if ( _liveResults.Count == 0 )
			{
				foreach ( var ep in _endpoints )
				{
					if ( token.IsCancellationRequested ) break;

					var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
					if ( string.IsNullOrEmpty( slug ) ) continue;

					_status = $"Testing {slug}...";
					Update();

					var input = JsonSerializer.Deserialize<JsonElement>( GenerateSmartInput( ep ) );
					var body = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( new { slug, input, skipWebhooks = true } ) );
					var resp = await SyncToolApi.RunTest( body );

					if ( token.IsCancellationRequested ) break;

					if ( resp.HasValue )
					{
						var r = resp.Value;
						var hasRes = r.TryGetProperty( "result", out var qRes );
						var ok = hasRes && qRes.TryGetProperty( "ok", out var rok ) && rok.GetBoolean();
						var epName = ep.TryGetProperty( "name", out var n ) ? n.GetString() : slug;
						var epMethod = ep.TryGetProperty( "method", out var mm ) ? mm.GetString() : "POST";

						// Extract reason
						string reason = null;
						if ( !ok && hasRes && qRes.TryGetProperty( "body", out var qBody ) && qBody.TryGetProperty( "error", out var qErr ) && qErr.TryGetProperty( "message", out var qMsg ) )
							reason = qMsg.GetString();

						// Extract timing and warnings
						object timingObj = hasRes && qRes.TryGetProperty( "timing", out var qTiming ) ? (object)qTiming : null;
						object warningsObj = r.TryGetProperty( "warnings", out var qWarnings ) ? (object)qWarnings : null;

						var entry = new Dictionary<string, object>
						{
							["name"] = $"{epName} — Quick Test",
							["endpoint"] = slug,
							["method"] = epMethod,
							["passed"] = ok,
							["reason"] = reason,
							["timing"] = timingObj,
							["warnings"] = warningsObj,
						};
						var entryEl = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( entry ) );
						_liveResults.Add( entryEl );
						if ( ok ) _livePassedCount++; else _liveFailedCount++;
						Update();
					}
				}
			}
		}
		catch ( Exception ex ) { _lastError = ex.Message; }

		_runAllFinished = true;
		_status = "Generating report...";
		Update();

		// Generate report in background
		try
		{
			if ( _liveResults.Count > 0 )
			{
				GenerateReportFile();
				_reportReady = true;
			}
		}
		catch { }

		_busy = false;
		_status = "";
		Update();
	}

	private void GenerateReportFile()
	{
		if ( _liveResults.Count == 0 ) return;

		var sb = new System.Text.StringBuilder();
		sb.AppendLine( "# Endpoint Test Report" );
		sb.AppendLine( $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}" );
		sb.AppendLine();

		sb.AppendLine( "## Summary" );
		sb.AppendLine( $"- **Total:** {_liveResults.Count}" );
		sb.AppendLine( $"- **Passed:** {_livePassedCount}" );
		sb.AppendLine( $"- **Failed:** {_liveFailedCount}" );
		sb.AppendLine();

		var failed = _liveResults.Where( r => r.TryGetProperty( "passed", out var p ) && !p.GetBoolean() ).ToList();
		var passed = _liveResults.Where( r => r.TryGetProperty( "passed", out var p ) && p.GetBoolean() ).ToList();

		if ( failed.Count > 0 )
		{
			sb.AppendLine( $"## Failed ({failed.Count})" );
			sb.AppendLine();
			foreach ( var test in failed ) AppendTestMd( sb, test );
		}
		if ( passed.Count > 0 )
		{
			sb.AppendLine( $"## Passed ({passed.Count})" );
			sb.AppendLine();
			foreach ( var test in passed ) AppendTestMd( sb, test );
		}

		_reportPath = Path.Combine( Path.GetTempPath(), "ns_test_report.md" );
		File.WriteAllText( _reportPath, sb.ToString() );
	}

	private async void RunAllWithAutoGenerate()
	{
		if ( _busy ) return;
		_busy = true;
		_status = "Auto-generating tests for all endpoints...";
		_lastResult = null;
		_lastError = null;
		Update();

		try
		{
			// Step 1: Suggest tests for every endpoint
			var allSuggestions = new List<Dictionary<string, object>>();
			foreach ( var ep in _endpoints )
			{
				var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
				if ( string.IsNullOrEmpty( slug ) ) continue;

				_status = $"Generating tests for {slug}...";
				Update();

				var suggestBody = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( new { slug } ) );
				var resp = await SyncToolApi.SuggestTests( suggestBody );
				if ( resp.HasValue && resp.Value.TryGetProperty( "suggestions", out var sug ) && sug.ValueKind == JsonValueKind.Array )
				{
					foreach ( var s2 in sug.EnumerateArray() )
					{
						var dict = JsonSerializer.Deserialize<Dictionary<string, object>>( s2.GetRawText() );
						if ( dict != null ) allSuggestions.Add( dict );
					}
				}
			}

			// Step 2: Save all suggested tests
			if ( allSuggestions.Count > 0 )
			{
				_status = $"Saving {allSuggestions.Count} auto-generated tests...";
				Update();

				var saveBody = JsonSerializer.Deserialize<JsonElement>(
					JsonSerializer.Serialize( new { action = "save-tests-bulk", suggestions = allSuggestions } ) );
				// Use the save-tests-bulk via the management API
				var saveFmt = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( allSuggestions ) );
				var existing = await SyncToolApi.GetTests();
				// Push the suggestions as new tests
				var merged = new List<object>();
				if ( existing.HasValue )
				{
					var data = existing.Value;
					if ( data.TryGetProperty( "data", out var d ) ) data = d;
					if ( data.ValueKind == JsonValueKind.Array )
						foreach ( var t in data.EnumerateArray() )
							merged.Add( JsonSerializer.Deserialize<object>( t.GetRawText() ) );
				}
				merged.AddRange( allSuggestions );
				var pushBody = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( merged ) );
				await SyncToolApi.PushTests( pushBody );

				// Reload local data
				LoadData();
			}

			// Step 3: Run all tests
			_status = "Running all tests...";
			Update();

			var runBody = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( new { } ) );
			var runResp = await SyncToolApi.RunAllTests( runBody );
			if ( runResp.HasValue ) _lastResult = runResp.Value;
			else _lastError = SyncToolApi.LastErrorMessage ?? "Request failed.";
		}
		catch ( Exception ex ) { _lastError = ex.Message; }
		finally { _busy = false; _status = ""; Update(); }
	}

	// ──────────────────────────────────────────────────────
	//  Input
	// ──────────────────────────────────────────────────────

	protected override void OnMousePress( MouseEvent e )
	{
		foreach ( var btn in _buttons )
		{
			if ( btn.Rect.IsInside( e.LocalPosition ) )
			{
				btn.OnClick?.Invoke();
				return;
			}
		}
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		_mousePos = e.LocalPosition;
		Update();
	}

	protected override void OnWheel( WheelEvent e )
	{
		var maxScroll = Math.Max( 0, _contentHeight - (Height - _scrollAreaTop) );
		_scrollY = Math.Clamp( _scrollY + (e.Delta > 0 ? -40 : 40), 0, maxScroll );
		Update();
	}

	protected override bool OnClose()
	{
		_cts?.Cancel();
		return base.OnClose();
	}

	private string GetEpLabel( JsonElement ep )
	{
		var method = ep.TryGetProperty( "method", out var m ) ? m.GetString() : "POST";
		var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "?";
		return $"{method} {slug}";
	}

	/// <summary>Extract a compact summary of key fields from a read result for the inline display.</summary>
	private static string ExtractKeyFields( JsonElement obj )
	{
		var parts = new List<string>();
		// Show the most important game-relevant fields
		string[] priorityFields = { "currency", "xp", "level", "rank", "currentOreKg", "backpackCapacity" };
		foreach ( var field in priorityFields )
		{
			if ( obj.TryGetProperty( field, out var val ) && val.ValueKind == JsonValueKind.Number )
				parts.Add( $"{field}={val}" );
			if ( parts.Count >= 3 ) break;
		}
		return parts.Count > 0 ? string.Join( ", ", parts ) : "";
	}

	/// <summary>Get relevant fields from a read step result to show as sub-rows.</summary>
	private static List<(string Name, string Value)> GetRelevantReadFields( JsonElement obj )
	{
		var fields = new List<(string, string)>();
		// Important scalar fields
		string[] show = { "currency", "xp", "currentOreKg", "backpackCapacity", "rank", "playerName" };
		foreach ( var key in show )
		{
			if ( !obj.TryGetProperty( key, out var val ) ) continue;
			if ( val.ValueKind == JsonValueKind.Number )
				fields.Add( (key, val.ToString()) );
			else if ( val.ValueKind == JsonValueKind.String )
			{
				var s = val.GetString();
				if ( !string.IsNullOrEmpty( s ) ) fields.Add( (key, s) );
			}
		}
		// Show ores summary if present
		if ( obj.TryGetProperty( "ores", out var ores ) && ores.ValueKind == JsonValueKind.Object )
		{
			var oreCount = 0;
			var totalKg = 0.0;
			foreach ( var ore in ores.EnumerateObject() )
			{
				if ( ore.Value.ValueKind == JsonValueKind.Number )
				{
					var kg = ore.Value.GetDouble();
					if ( kg > 0 ) { oreCount++; totalKg += kg; }
				}
			}
			if ( oreCount > 0 )
				fields.Add( ("ores", $"{oreCount} types, {totalKg:F1}kg total") );
		}
		// Show owned vehicles count
		if ( obj.TryGetProperty( "ownedVehicles", out var vehicles ) && vehicles.ValueKind == JsonValueKind.Array )
			fields.Add( ("ownedVehicles", $"{vehicles.GetArrayLength()} owned") );
		// Show faction rep summary
		if ( obj.TryGetProperty( "factionRep", out var rep ) && rep.ValueKind == JsonValueKind.Object )
		{
			var repCount = 0;
			foreach ( var r in rep.EnumerateObject() )
				if ( r.Value.ValueKind == JsonValueKind.Number && r.Value.GetDouble() > 0 ) repCount++;
			if ( repCount > 0 )
				fields.Add( ("factionRep", $"{repCount} factions with rep") );
		}
		return fields;
	}
}
