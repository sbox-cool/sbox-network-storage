using Sandbox;
using Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

/// <summary>
/// Modal window showing live test results as they come in.
/// Shows each endpoint with pass/fail status, and a "View All Logs" button when done.
/// </summary>
public class TestResultsWindow : DockWindow
{
	private List<TestEntry> _entries = new();
	private bool _finished;
	private string _reportPath;
	private CancellationTokenSource _cts = new();
	private float _scrollY;
	private float _contentHeight;
	private float _scrollAreaTop;
	private Vector2 _mousePos;
	private List<ClickRegion> _buttons = new();

	private struct ClickRegion
	{
		public Rect Rect;
		public string Id;
		public Action OnClick;
	}

	public struct TestEntry
	{
		public string Name;
		public string Endpoint;
		public string Method;
		public bool Passed;
		public bool Deprecated;
		public string Reason;
		public double TimingMs;
		public string[] Warnings;
		public JsonElement? FullData;
	}

	public int TotalCount => _entries.Count;
	public int PassedCount => _entries.Count( e => e.Passed );
	public int FailedCount => _entries.Count( e => !e.Passed );

	public TestResultsWindow()
	{
		Title = "Test Results";
		Size = new Vector2( 620, 500 );
		MinimumSize = new Vector2( 450, 300 );
	}

	/// <summary>Open a new results window and run all tests, showing results as they arrive.</summary>
	public static TestResultsWindow OpenAndRun( string endpointFilter = null )
	{
		var window = new TestResultsWindow();
		window.Title = endpointFilter != null ? $"Testing: {endpointFilter}" : "Test Results";
		window.Show();
		_ = window.RunTests( endpointFilter );
		return window;
	}

	protected override bool OnClose()
	{
		_cts?.Cancel();
		return base.OnClose();
	}

	private async System.Threading.Tasks.Task RunTests( string endpointFilter )
	{
		var token = _cts.Token;
		try
		{
			if ( token.IsCancellationRequested ) return;

			// Use auto-test endpoint -- no saved tests needed, generates and runs inline
			var body = endpointFilter != null
				? JsonSerializer.Serialize( new { slug = endpointFilter } )
				: "{}";
			var resp = await SyncToolApi.AutoTest( JsonSerializer.Deserialize<JsonElement>( body ) );
			if ( token.IsCancellationRequested ) return;

			if ( !resp.HasValue )
			{
				_entries.Add( new TestEntry { Name = "Request Failed", Endpoint = "", Passed = false, Reason = SyncToolApi.LastErrorMessage ?? "Unknown error" } );
				_finished = true;
				Update();
				return;
			}

			var result = resp.Value;

			// Single endpoint response has no "results" array -- wrap it
			if ( !result.TryGetProperty( "results", out var results ) )
			{
				ParseAutoTestEntry( result );
			}
			else if ( results.ValueKind == JsonValueKind.Array )
			{
				foreach ( var test in results.EnumerateArray() )
					ParseAutoTestEntry( test );
			}

			// Generate report file
			_reportPath = GenerateReport();
		}
		catch ( Exception ex )
		{
			_entries.Add( new TestEntry { Name = "Error", Endpoint = "", Passed = false, Reason = ex.Message } );
		}

		_finished = true;
		Title = $"Test Results -- {PassedCount}/{TotalCount} passed";
		Update();
	}

	private void ParseAutoTestEntry( JsonElement test )
	{
		var entry = new TestEntry
		{
			Name = test.TryGetProperty( "name", out var n ) ? n.GetString() : "?",
			Endpoint = test.TryGetProperty( "endpoint", out var ep ) ? ep.GetString() : "",
			Method = test.TryGetProperty( "method", out var m ) ? m.GetString() : "POST",
			Passed = test.TryGetProperty( "passed", out var p ) && p.GetBoolean(),
			Deprecated = test.TryGetProperty( "deprecated", out var dep ) && dep.GetBoolean(),
			FullData = test,
		};

		// Collect errors and warnings as the reason string
		var reasons = new List<string>();
		if ( test.TryGetProperty( "errors", out var errs ) && errs.ValueKind == JsonValueKind.Array )
			foreach ( var e in errs.EnumerateArray() )
				reasons.Add( e.GetString() );
		entry.Reason = reasons.Count > 0 ? string.Join( "; ", reasons ) : null;

		if ( test.TryGetProperty( "warnings", out var ws ) && ws.ValueKind == JsonValueKind.Array )
			entry.Warnings = ws.EnumerateArray().Select( w => w.GetString() ).ToArray();

		if ( test.TryGetProperty( "result", out var res ) && res.TryGetProperty( "timing", out var tm ) && tm.TryGetProperty( "total", out var tt ) )
			entry.TimingMs = tt.GetDouble();

		_entries.Add( entry );
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

		// Title
		Paint.SetDefaultFont( size: 14, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w * 0.5f, 24 ), "Test Results", TextFlag.LeftCenter );

		// View All Logs button (disabled until finished)
		var logBtnW = 110f;
		var logBtnRect = new Rect( pad + w - logBtnW, y, logBtnW, 24 );
		if ( _finished )
		{
			DrawBtn( logBtnRect, "View All Logs", Color.Cyan, "view_logs", () => OpenReport() );
		}
		else
		{
			// Disabled state
			Paint.SetBrush( Color.White.WithAlpha( 0.03f ) );
			Paint.SetPen( Color.White.WithAlpha( 0.15f ) );
			Paint.DrawRect( logBtnRect, 4 );
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.25f ) );
			Paint.DrawText( logBtnRect, "View All Logs", TextFlag.Center );
		}
		y += 32;

		// Summary badges
		if ( _entries.Count > 0 )
		{
			Paint.SetDefaultFont( size: 11, weight: 700 );

			Paint.SetBrush( new Color( 0.2f, 0.8f, 0.4f ).WithAlpha( 0.15f ) );
			Paint.SetPen( new Color( 0.2f, 0.8f, 0.4f ) );
			Paint.DrawRect( new Rect( pad, y, 90, 22 ), 4 );
			Paint.DrawText( new Rect( pad, y, 90, 22 ), $"Passed: {PassedCount}", TextFlag.Center );

			Paint.SetBrush( new Color( 1f, 0.3f, 0.3f ).WithAlpha( 0.15f ) );
			Paint.SetPen( new Color( 1f, 0.3f, 0.3f ) );
			Paint.DrawRect( new Rect( pad + 98, y, 90, 22 ), 4 );
			Paint.DrawText( new Rect( pad + 98, y, 90, 22 ), $"Failed: {FailedCount}", TextFlag.Center );

			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
			Paint.DrawText( new Rect( pad + 200, y, 100, 22 ), $"{TotalCount} total", TextFlag.LeftCenter );

			if ( !_finished )
			{
				Paint.SetPen( Color.Yellow.WithAlpha( 0.7f ) );
				Paint.DrawText( new Rect( pad + w - 100, y, 100, 22 ), "Running...", TextFlag.RightCenter );
			}
			y += 28;

			// "View all failed test results" button
			if ( FailedCount > 0 )
			{
				var failBtnW = 180f;
				var failBtnRect = new Rect( pad, y, failBtnW, 22 );
				if ( _finished )
				{
					DrawBtn( failBtnRect, $"View Failed Results ({FailedCount})", new Color( 1f, 0.3f, 0.3f ), "view_failed", OpenFailedReport );
				}
				else
				{
					Paint.SetBrush( Color.White.WithAlpha( 0.03f ) );
					Paint.SetPen( Color.White.WithAlpha( 0.15f ) );
					Paint.DrawRect( failBtnRect, 4 );
					Paint.SetDefaultFont( size: 9 );
					Paint.SetPen( Color.White.WithAlpha( 0.25f ) );
					Paint.DrawText( failBtnRect, $"View Failed ({FailedCount})...", TextFlag.Center );
				}
				y += 28;
			}
		}
		else if ( !_finished )
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
			Paint.DrawText( new Rect( pad, y, w, 16 ), "Running tests...", TextFlag.LeftCenter );
			y += 24;
		}

		_scrollAreaTop = y;
		y -= _scrollY;

		// Separator
		Paint.SetPen( Color.White.WithAlpha( 0.08f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 8;

		// Test entries
		foreach ( var entry in _entries )
		{
			if ( y > Height + 20 ) break; // skip offscreen
			if ( y + 40 > _scrollAreaTop ) // only draw visible
			{
				DrawTestEntry( ref y, pad, w, entry );
			}
			else
			{
				y += entry.Reason != null ? 46 : 30;
			}
		}

		_contentHeight = y + _scrollY + 40;

		// Redraw fixed header
		Paint.SetBrush( new Color( 0.133f, 0.133f, 0.133f ) );
		Paint.ClearPen();
		Paint.DrawRect( new Rect( 0, 0, Width, _scrollAreaTop ) );

		// Re-draw header elements
		y = 38f;
		Paint.SetDefaultFont( size: 14, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w * 0.5f, 24 ), "Test Results", TextFlag.LeftCenter );

		if ( _finished )
			DrawBtn( logBtnRect, "View All Logs", Color.Cyan, "view_logs", () => OpenReport() );
		else
		{
			Paint.SetBrush( Color.White.WithAlpha( 0.03f ) );
			Paint.SetPen( Color.White.WithAlpha( 0.15f ) );
			Paint.DrawRect( logBtnRect, 4 );
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.25f ) );
			Paint.DrawText( logBtnRect, "View All Logs", TextFlag.Center );
		}

		y += 32;
		if ( _entries.Count > 0 )
		{
			Paint.SetDefaultFont( size: 11, weight: 700 );
			Paint.SetBrush( new Color( 0.2f, 0.8f, 0.4f ).WithAlpha( 0.15f ) );
			Paint.SetPen( new Color( 0.2f, 0.8f, 0.4f ) );
			Paint.DrawRect( new Rect( pad, y, 90, 22 ), 4 );
			Paint.DrawText( new Rect( pad, y, 90, 22 ), $"Passed: {PassedCount}", TextFlag.Center );
			Paint.SetBrush( new Color( 1f, 0.3f, 0.3f ).WithAlpha( 0.15f ) );
			Paint.SetPen( new Color( 1f, 0.3f, 0.3f ) );
			Paint.DrawRect( new Rect( pad + 98, y, 90, 22 ), 4 );
			Paint.DrawText( new Rect( pad + 98, y, 90, 22 ), $"Failed: {FailedCount}", TextFlag.Center );
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
			Paint.DrawText( new Rect( pad + 200, y, 100, 22 ), $"{TotalCount} total", TextFlag.LeftCenter );
			if ( !_finished )
			{
				Paint.SetPen( Color.Yellow.WithAlpha( 0.7f ) );
				Paint.DrawText( new Rect( pad + w - 100, y, 100, 22 ), "Running...", TextFlag.RightCenter );
			}
			y += 28;
			if ( FailedCount > 0 && _finished )
			{
				var failBtnRect2 = new Rect( pad, y, 180, 22 );
				DrawBtn( failBtnRect2, $"View Failed Results ({FailedCount})", new Color( 1f, 0.3f, 0.3f ), "view_failed", OpenFailedReport );
			}
		}
	}

	private void DrawTestEntry( ref float y, float pad, float w, TestEntry entry )
	{
		var rowH = 28f;

		// Icon
		if ( entry.Deprecated )
		{
			Paint.SetDefaultFont( size: 10, weight: 700 );
			Paint.SetPen( Color.White.WithAlpha( 0.25f ) );
			Paint.DrawText( new Rect( pad, y, 16, rowH ), "-", TextFlag.Center );
		}
		else
		{
			var iconColor = entry.Passed ? new Color( 0.2f, 0.8f, 0.4f ) : new Color( 1f, 0.3f, 0.3f );
			Paint.SetDefaultFont( size: 10, weight: 700 );
			Paint.SetPen( iconColor );
			Paint.DrawText( new Rect( pad, y, 16, rowH ), entry.Passed ? "+" : "x", TextFlag.Center );
		}

		// Name
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( entry.Deprecated ? Color.White.WithAlpha( 0.35f ) : Color.White.WithAlpha( 0.9f ) );
		var nameText = entry.Name;
		Paint.DrawText( new Rect( pad + 20, y, w - 170, rowH ), nameText ?? "", TextFlag.LeftCenter );

		// Deprecated badge
		if ( entry.Deprecated )
		{
			var nameW = Paint.MeasureText( nameText ?? "" ).x;
			var depX = pad + 20 + nameW + 6;
			Paint.SetDefaultFont( size: 7 );
			var depText = "deprecated";
			var depTextW = Paint.MeasureText( depText ).x + 8;
			var depRect = new Rect( depX, y + ( rowH - 14 ) / 2, depTextW, 14 );
			Paint.SetBrush( new Color( 0.96f, 0.62f, 0.04f, 0.12f ) );
			Paint.SetPen( new Color( 0.96f, 0.62f, 0.04f, 0.25f ) );
			Paint.DrawRect( depRect, 3 );
			Paint.SetPen( new Color( 0.96f, 0.62f, 0.04f, 0.6f ) );
			Paint.DrawText( depRect, depText, TextFlag.Center );
		}

		// Endpoint badge
		Paint.SetDefaultFont( size: 8 );
		Paint.SetPen( Color.White.WithAlpha( entry.Deprecated ? 0.15f : 0.3f ) );
		Paint.DrawText( new Rect( pad + w - 150, y, 90, rowH ), entry.Endpoint, TextFlag.LeftCenter );

		// Timing
		Paint.DrawText( new Rect( pad + w - 55, y, 50, rowH ), entry.Deprecated ? "skipped" : $"{entry.TimingMs}ms", TextFlag.RightCenter );

		// Download log button (per entry)
		if ( entry.FullData.HasValue )
		{
			var dlRect = new Rect( pad + w - 2, y + 4, 14, 18 );
			var dlHovered = dlRect.IsInside( _mousePos );
			Paint.SetPen( Color.White.WithAlpha( dlHovered ? 0.6f : 0.25f ) );
			Paint.SetDefaultFont( size: 10 );
			Paint.DrawText( dlRect, ">", TextFlag.Center );
			var captured = entry;
			_buttons.Add( new ClickRegion { Rect = dlRect, Id = $"dl_{entry.Name}", OnClick = () => OpenSingleLog( captured ) } );
		}

		y += rowH;

		// Failure reason
		if ( !string.IsNullOrEmpty( entry.Reason ) )
		{
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( new Color( 1f, 0.3f, 0.3f ).WithAlpha( 0.7f ) );
			var reasonH = Math.Max( 12, (int)Math.Ceiling( entry.Reason.Length / 60.0 ) * 11 );
			Paint.DrawText( new Rect( pad + 20, y, w - 24, reasonH ), entry.Reason, TextFlag.LeftTop | TextFlag.WordWrap );
			y += reasonH + 4;
		}

		// Warnings count
		if ( entry.Warnings != null && entry.Warnings.Length > 0 )
		{
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( new Color( 1f, 0.7f, 0.2f ).WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad + 20, y, w, 12 ), $"{entry.Warnings.Length} warning(s)", TextFlag.LeftCenter );
			y += 14;
		}

		y += 2;
	}

	private void DrawBtn( Rect rect, string label, Color color, string id, Action onClick )
	{
		var hovered = rect.IsInside( _mousePos );
		Paint.SetBrush( color.WithAlpha( hovered ? 0.2f : 0.1f ) );
		Paint.SetPen( color.WithAlpha( hovered ? 0.6f : 0.3f ) );
		Paint.DrawRect( rect, 4 );
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( color );
		Paint.DrawText( rect, label, TextFlag.Center );
		_buttons.Add( new ClickRegion { Rect = rect, Id = id, OnClick = onClick } );
	}

	// ──────────────────────────────────────────────────────
	//  Report generation
	// ──────────────────────────────────────────────────────

	private string GenerateReport()
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine( "# Endpoint Test Report" );
		sb.AppendLine( $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}" );
		sb.AppendLine();
		sb.AppendLine( "## Summary" );
		sb.AppendLine( $"- **Total:** {TotalCount}" );
		sb.AppendLine( $"- **Passed:** {PassedCount}" );
		sb.AppendLine( $"- **Failed:** {FailedCount}" );
		sb.AppendLine();

		var failed = _entries.Where( e => !e.Passed ).ToList();
		var passed = _entries.Where( e => e.Passed ).ToList();

		if ( failed.Count > 0 )
		{
			sb.AppendLine( $"## Failed ({failed.Count})" );
			sb.AppendLine();
			foreach ( var e in failed ) AppendEntryMd( sb, e );
		}

		if ( passed.Count > 0 )
		{
			sb.AppendLine( $"## Passed ({passed.Count})" );
			sb.AppendLine();
			foreach ( var e in passed ) AppendEntryMd( sb, e );
		}

		var path = Path.Combine( Path.GetTempPath(), "ns_test_report.md" );
		File.WriteAllText( path, sb.ToString() );
		return path;
	}

	private void AppendEntryMd( System.Text.StringBuilder sb, TestEntry entry )
	{
		sb.AppendLine( $"### {( entry.Passed ? "PASS" : "FAIL" )} -- {entry.Name}" );
		sb.AppendLine( $"- **Endpoint:** `{entry.Method} {entry.Endpoint}`" );
		sb.AppendLine( $"- **Timing:** {entry.TimingMs}ms" );
		if ( !string.IsNullOrEmpty( entry.Reason ) )
			sb.AppendLine( $"- **Reason:** {entry.Reason}" );

		if ( entry.FullData.HasValue )
		{
			var test = entry.FullData.Value;
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
				var resOk = res.TryGetProperty( "ok", out var rok ) && rok.GetBoolean();
				var resStatus = res.TryGetProperty( "status", out var rs ) ? rs.GetInt32() : 0;
				sb.AppendLine( $"- **Result:** ok={resOk}, status={resStatus}" );
				if ( res.TryGetProperty( "body", out var body ) )
				{
					sb.AppendLine( "```json" );
					sb.AppendLine( JsonSerializer.Serialize( body, new JsonSerializerOptions { WriteIndented = true } ) );
					sb.AppendLine( "```" );
				}
			}
			if ( test.TryGetProperty( "steps", out var steps ) && steps.ValueKind == JsonValueKind.Array && steps.GetArrayLength() > 0 )
			{
				sb.AppendLine( "- **Steps:**" );
				foreach ( var step in steps.EnumerateArray() )
				{
					var sid = step.TryGetProperty( "id", out var si ) ? si.GetString() : "?";
					var stype = step.TryGetProperty( "type", out var st ) ? st.GetString() : "?";
					var spass = !step.TryGetProperty( "passed", out var sp ) || sp.ValueKind != JsonValueKind.False;
					sb.AppendLine( $"  - {( spass ? "+" : "x" )} `{sid}` ({stype})" );
				}
			}
		}

		if ( entry.Warnings != null && entry.Warnings.Length > 0 )
		{
			sb.AppendLine( "- **Warnings:**" );
			foreach ( var w in entry.Warnings )
				sb.AppendLine( $"  - {w}" );
		}
		sb.AppendLine();
	}

	private void OpenReport()
	{
		if ( string.IsNullOrEmpty( _reportPath ) )
			_reportPath = GenerateReport();
		EditorUtility.OpenFile( _reportPath );
	}

	private void OpenFailedReport()
	{
		var failed = _entries.Where( e => !e.Passed ).ToList();
		if ( failed.Count == 0 ) return;

		var sb = new System.Text.StringBuilder();
		sb.AppendLine( "# Failed Test Results -- Full Context" );
		sb.AppendLine( $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}" );
		sb.AppendLine( $"Failed: {failed.Count} / {_entries.Count} total" );
		sb.AppendLine();

		foreach ( var entry in failed )
		{
			sb.AppendLine( "—-" );
			sb.AppendLine();
			sb.AppendLine( $"## FAIL -- {entry.Name}" );
			sb.AppendLine();
			sb.AppendLine( $"| Field | Value |" );
			sb.AppendLine( $"|———-|———-|" );
			sb.AppendLine( $"| **Endpoint** | `{entry.Method} {entry.Endpoint}` |" );
			sb.AppendLine( $"| **Timing** | {entry.TimingMs}ms |" );
			sb.AppendLine( $"| **Reason** | {entry.Reason ?? "—"} |" );
			sb.AppendLine();

			if ( entry.FullData.HasValue )
			{
				var test = entry.FullData.Value;

				// Test ID
				if ( test.TryGetProperty( "testId", out var tid ) )
					sb.AppendLine( $"**Test ID:** `{tid}`" );

				// Input
				if ( test.TryGetProperty( "input", out var inp ) )
				{
					sb.AppendLine();
					sb.AppendLine( "### Input" );
					sb.AppendLine( "```json" );
					sb.AppendLine( JsonSerializer.Serialize( inp, new JsonSerializerOptions { WriteIndented = true } ) );
					sb.AppendLine( "```" );
				}

				// Expected
				if ( test.TryGetProperty( "expect", out var exp ) )
				{
					sb.AppendLine();
					sb.AppendLine( "### Expected Outcome" );
					sb.AppendLine( "```json" );
					sb.AppendLine( JsonSerializer.Serialize( exp, new JsonSerializerOptions { WriteIndented = true } ) );
					sb.AppendLine( "```" );
				}

				// Mock Data
				if ( test.TryGetProperty( "mockData", out var mock ) && mock.ValueKind == JsonValueKind.Object )
				{
					sb.AppendLine();
					sb.AppendLine( "### Mock Player Data" );
					sb.AppendLine( "```json" );
					sb.AppendLine( JsonSerializer.Serialize( mock, new JsonSerializerOptions { WriteIndented = true } ) );
					sb.AppendLine( "```" );
				}

				// Result
				if ( test.TryGetProperty( "result", out var res ) )
				{
					sb.AppendLine();
					sb.AppendLine( "### Actual Result" );
					var resOk = res.TryGetProperty( "ok", out var rok ) && rok.GetBoolean();
					var resStatus = res.TryGetProperty( "status", out var rs ) ? rs.GetInt32() : 0;
					sb.AppendLine( $"- **ok:** {resOk}" );
					sb.AppendLine( $"- **status:** {resStatus}" );
					if ( res.TryGetProperty( "body", out var body ) )
					{
						sb.AppendLine( "- **body:**" );
						sb.AppendLine( "```json" );
						sb.AppendLine( JsonSerializer.Serialize( body, new JsonSerializerOptions { WriteIndented = true } ) );
						sb.AppendLine( "```" );
					}
				}

				// Steps
				if ( test.TryGetProperty( "steps", out var steps ) && steps.ValueKind == JsonValueKind.Array && steps.GetArrayLength() > 0 )
				{
					sb.AppendLine();
					sb.AppendLine( "### Step Trace" );
					sb.AppendLine( "| Step | Type | Status | Result |" );
					sb.AppendLine( "|———|———|————|————|" );
					foreach ( var step in steps.EnumerateArray() )
					{
						var sid = step.TryGetProperty( "id", out var si ) ? si.GetString() : "?";
						var stype = step.TryGetProperty( "type", out var st ) ? st.GetString() : "?";
						var spass = !step.TryGetProperty( "passed", out var sp ) || sp.ValueKind != JsonValueKind.False;
						var swarn = step.TryGetProperty( "warning", out var sw ) ? sw.GetString() : null;
						var status = swarn != null ? "Warning" : spass ? "OK" : "FAIL";
						var sresult = "";
						if ( step.TryGetProperty( "result", out var sr ) )
						{
							if ( sr.ValueKind == JsonValueKind.Number || sr.ValueKind == JsonValueKind.String || sr.ValueKind == JsonValueKind.True || sr.ValueKind == JsonValueKind.False )
								sresult = sr.ToString();
							else if ( sr.ValueKind == JsonValueKind.Null )
								sresult = "null";
							else if ( sr.ValueKind == JsonValueKind.Object )
								sresult = $"(object, {sr.EnumerateObject().Count()} fields)";
						}
						sb.AppendLine( $"| `{sid}` | {stype} | {status} | {sresult} |" );
					}
				}

				// Pending Writes
				if ( test.TryGetProperty( "pendingWrites", out var pw ) && pw.ValueKind == JsonValueKind.Array && pw.GetArrayLength() > 0 )
				{
					sb.AppendLine();
					sb.AppendLine( "### Pending Writes (would execute)" );
					sb.AppendLine( "```json" );
					sb.AppendLine( JsonSerializer.Serialize( pw, new JsonSerializerOptions { WriteIndented = true } ) );
					sb.AppendLine( "```" );
				}
			}

			// Warnings
			if ( entry.Warnings != null && entry.Warnings.Length > 0 )
			{
				sb.AppendLine();
				sb.AppendLine( "### Warnings" );
				foreach ( var w in entry.Warnings )
					sb.AppendLine( $"- {w}" );
			}

			// Full raw JSON
			if ( entry.FullData.HasValue )
			{
				sb.AppendLine();
				sb.AppendLine( "<details><summary>Full Raw JSON</summary>" );
				sb.AppendLine();
				sb.AppendLine( "```json" );
				sb.AppendLine( JsonSerializer.Serialize( entry.FullData.Value, new JsonSerializerOptions { WriteIndented = true } ) );
				sb.AppendLine( "```" );
				sb.AppendLine( "</details>" );
			}

			sb.AppendLine();
		}

		var path = Path.Combine( Path.GetTempPath(), "ns_failed_tests.md" );
		File.WriteAllText( path, sb.ToString() );
		EditorUtility.OpenFile( path );
	}

	private void OpenSingleLog( TestEntry entry )
	{
		if ( !entry.FullData.HasValue ) return;
		var json = JsonSerializer.Serialize( entry.FullData.Value, new JsonSerializerOptions { WriteIndented = true } );
		var safeName = (entry.Name ?? "test").Replace( " ", "_" ).Replace( "/", "_" );
		var path = Path.Combine( Path.GetTempPath(), $"ns_test_{safeName}.json" );
		File.WriteAllText( path, json );
		EditorUtility.OpenFile( path );
	}

	// ──────────────────────────────────────────────────────
	//  Input
	// ──────────────────────────────────────────────────────

	protected override void OnMousePress( MouseEvent e )
	{
		foreach ( var btn in _buttons )
			if ( btn.Rect.IsInside( e.LocalPosition ) )
			{
				btn.OnClick?.Invoke();
				return;
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
}
