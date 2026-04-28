#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Editor;

/// <summary>
/// Editor window for Network Storage sync operations.
/// Checks remote for newer versions, shows per-item Push/Pull buttons.
/// Push warns if remote is newer. Pull buttons only appear when remote differs.
/// After push or pull, re-checks and clears stale state.
/// </summary>
[Dock( "Editor", "Network Storage Sync", "cloud" )]
public partial class SyncToolWindow : DockWindow
{
	private static readonly JsonSerializerOptions _readOptions = new()
	{
		AllowTrailingCommas = true,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};

	private string _status = "Ready";
	private bool _statusIsError;
	private bool _busy;
	private string _busyItem;
	private Dictionary<string, ItemState> _items = new();
	private List<ClickRegion> _buttons = new();
	private Vector2 _mousePos;
	private bool _hasCheckedRemote;

	// Generate state
	private bool _generateBusy;

	// Cached file lists
	private string[] _endpointFiles = Array.Empty<string>();
	private string[] _collectionFiles = Array.Empty<string>(); // collections/{name}.collection.yml
	private string[] _workflowFiles = Array.Empty<string>(); // workflows/{id}.workflow.yml

	// Remote data cache (from last check)
	private JsonElement? _remoteEndpoints;
	private JsonElement? _remoteCollections;
	private JsonElement? _remoteWorkflows;

	// ── Scroll state ──
	private float _scrollY;
	private float _scrollAreaTop;
	private float _contentHeight;
	private const float RowH = 29f;

	// ── Sync log (shown after push/pull) ──
	private List<SyncLogEntry> _syncLog = new();

	private struct SyncLogEntry
	{
		public string Name;
		public string Type;   // "Endpoint", "Collection", "Workflow"
		public bool Ok;
		public string Detail; // "Pushed", "Created (new)", "Failed", "Verified ✓", "Mismatch — see diff", etc.
	}

	private float MaxScroll => !IsValid ? 0 : Math.Max( 0, _contentHeight - ( Height - _scrollAreaTop ) + 60 );

	private struct ClickRegion
	{
		public Rect Rect;
		public string Id;
		public Action OnClick;
	}

	private enum SyncStatus { Unknown, InSync, LocalOnly, RemoteOnly, Differs, MergeAvailable }

	private struct ItemState
	{
		public string SyncResult;
		public bool RemoteDiffers;
		public SyncStatus Status;
		public string DiffSummary;
		public string LocalJson;
		public string RemoteJson;
	}

	public SyncToolWindow()
	{
		Title = "Network Storage Sync";
		Size = new Vector2( 720, 620 );
		MinimumSize = new Vector2( 550, 400 );
		SyncToolConfig.Load();
		RefreshFileList();
	}

	[Menu( "Editor", "Network Storage/Sync Tool" )]
	public static void OpenWindow()
	{
		var window = new SyncToolWindow();
		window.Show();
	}

	private void SetStatus( string message, bool isError = false )
	{
		_status = message;
		_statusIsError = isError;
	}

	private void ShowGenerateFailure( string message, string detail = null )
	{
		SetStatus( message, isError: true );
		MessageDialog.Show( "Generate Failed", message, detail );
	}

	private static string ExtractGenerateFailureSummary( string stdout, string stderr, int exitCode )
	{
		foreach ( var source in new[] { stderr, stdout } )
		{
			if ( string.IsNullOrWhiteSpace( source ) )
				continue;

			var lines = source
				.Replace( "\r", "" )
				.Split( '\n' )
				.Select( x => x.Trim() )
				.Where( x => !string.IsNullOrWhiteSpace( x ) )
				.ToArray();

			var explicitError = lines.FirstOrDefault( x => x.StartsWith( "ERROR:", StringComparison.OrdinalIgnoreCase ) );
			if ( !string.IsNullOrWhiteSpace( explicitError ) )
				return explicitError["ERROR:".Length..].Trim();

			var usefulLine = lines.FirstOrDefault( x =>
				!x.StartsWith( "===", StringComparison.Ordinal ) &&
				!x.StartsWith( "Project root:", StringComparison.OrdinalIgnoreCase ) &&
				!x.StartsWith( "Data dir:", StringComparison.OrdinalIgnoreCase ) );

			if ( !string.IsNullOrWhiteSpace( usefulLine ) )
				return usefulLine;
		}

		return $"Generation failed (exit {exitCode})";
	}

	private static string BuildGenerateFailureDetail( string stdout, string stderr )
	{
		var detail = !string.IsNullOrWhiteSpace( stderr ) ? stderr.Trim() : stdout.Trim();
		if ( string.IsNullOrWhiteSpace( detail ) )
			return null;

		const int maxLength = 4000;
		if ( detail.Length > maxLength )
			detail = detail[..maxLength] + "\n...";

		return detail;
	}

	private void RefreshFileList()
	{
		var epDir = SyncToolConfig.Abs( SyncToolConfig.EndpointsPath );
		_endpointFiles = Directory.Exists( epDir )
			? FindResourceFiles( epDir, "endpoint" )
			: Array.Empty<string>();

		var colDir = SyncToolConfig.Abs( SyncToolConfig.CollectionsPath );
		_collectionFiles = Directory.Exists( colDir )
			? FindResourceFiles( colDir, "collection" )
			: Array.Empty<string>();

		var wfDir = SyncToolConfig.Abs( SyncToolConfig.WorkflowsPath );
		_workflowFiles = Directory.Exists( wfDir )
			? FindResourceFiles( wfDir, "workflow" )
			: Array.Empty<string>();
	}

	private static string[] FindResourceFiles( string directory, string kind )
	{
		var sourceFiles = Directory.GetFiles( directory, $"*.{kind}.yml" )
			.Concat( Directory.GetFiles( directory, $"*.{kind}.yaml" ) )
			.OrderBy( f => f, StringComparer.OrdinalIgnoreCase )
			.ToArray();
		return sourceFiles.Length > 0
			? sourceFiles
			: Directory.GetFiles( directory, "*.json" )
				.OrderBy( f => f, StringComparer.OrdinalIgnoreCase )
				.ToArray();
	}

	private static string ResourceIdFromFile( string filePath, string kind )
		=> SyncToolConfig.ResourceIdFromFilePath( filePath, kind );

	// ──────────────────────────────────────────────────────
	//  Rendering
	// ──────────────────────────────────────────────────────

	protected override void OnPaint()
	{
		base.OnPaint();
		_buttons.Clear();

		var y = 38f;
		var pad = 16f;
		var w = Width - pad * 2;

		// ── Header + Push All button ──
		Paint.SetDefaultFont( size: 13, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w * 0.55f, 22 ), "Network Storage Sync", TextFlag.LeftCenter );

		// Push All + Test All buttons
		if ( SyncToolConfig.IsValid )
		{
			var btnW2 = 70f;
			var testAllW = 65f;
			var pushAllRect = new Rect( pad + w - btnW2, y, btnW2, 22 );
			var testAllRect = new Rect( pad + w - btnW2 - 4 - testAllW, y, testAllW, 22 );
			DrawSmallButton( pushAllRect, "Push All", Color.Green, "push_all", () => _ = PushAll() );
			DrawSmallButton( testAllRect, "Test All", Color.Cyan, "test_all", () => TestResultsWindow.OpenAndRun() );
		}
		y += 30;

		// ── Config status ──
		if ( !SyncToolConfig.IsValid )
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.Red );
			Paint.DrawText( new Rect( pad, y, w, 16 ), "Not configured — click Setup to enter your keys", TextFlag.LeftCenter );
			y += 24;
		}
		else
		{
			Paint.SetDefaultFont( size: 9 );
			Paint.SetPen( Color.Green.WithAlpha( 0.8f ) );
			Paint.DrawText( new Rect( pad, y, w, 14 ), $"Connected — {SyncToolConfig.ProjectId}", TextFlag.LeftCenter );
			y += 18;

			Paint.SetDefaultFont( size: 9 );
			Paint.SetPen( Color.White.WithAlpha( 0.72f ) );
			Paint.DrawText( new Rect( pad, y, w, 14 ),
				$"Auth Sessions: {SyncToolConfig.AuthSessionsLabel}   Encrypted Requests: {SyncToolConfig.EncryptedRequestsLabel}",
				TextFlag.LeftCenter );
			y += 20;
		}

		// ── Check for Updates / Pull from Web button ──
		if ( SyncToolConfig.IsValid )
		{
			var checkBtnH = 26f;
			var checkLabel = _hasCheckedRemote ? "Pull from Web (re-check)" : "Check for Updates";
			var checkRect = new Rect( pad, y, w, checkBtnH );
			DrawWideButton( checkRect, checkLabel, Color.Cyan, "check_updates", () => _ = CheckForUpdates() );
			y += checkBtnH + 8;

			var remoteSemanticsCount = GetRemoteSemanticsCount();
			if ( remoteSemanticsCount > 0 )
			{
				var semanticsLabel = remoteSemanticsCount == 1
					? "Pull All (1 semantic item)"
					: $"Pull All ({remoteSemanticsCount} semantic items)";
				var semanticsRect = new Rect( pad, y, w, checkBtnH );
				DrawWideButton( semanticsRect, semanticsLabel, Color.Green, "pull_remote_semantics_all",
					() => PullAllRemoteSemantics() );
				y += checkBtnH + 8;
			}
		}

		DrawSeparator( ref y, w, pad );

		// ── Begin scrollable content ──
		_scrollAreaTop = y;
		y -= _scrollY;

		// ── Data Sources ──
		if ( SyncToolConfig.SyncMappings.Count > 0 )
		{
			DrawDataSourcesSection( ref y, pad, w );
			DrawSeparator( ref y, w, pad );
		}

		// ── Build item sets for all sections ──
		var remoteEpSlugs = GetRemoteEndpointSlugs();
		var allSlugs = new HashSet<string>();
		foreach ( var f in _endpointFiles ) allSlugs.Add( ResourceIdFromFile( f, "endpoint" ) );
		foreach ( var s in remoteEpSlugs ) allSlugs.Add( s );

		var localColNames = _collectionFiles.Select( f => ResourceIdFromFile( f, "collection" ) ).ToHashSet();
		var remoteColNames = GetRemoteCollectionNames();
		var allColNames = new HashSet<string>( localColNames );
		foreach ( var n in remoteColNames ) allColNames.Add( n );

		var localWfIds = _workflowFiles.Select( f => ResourceIdFromFile( f, "workflow" ) ).ToHashSet();
		var remoteWfIds = GetRemoteWorkflowIds();
		var allWfIds = new HashSet<string>( localWfIds );
		foreach ( var id2 in remoteWfIds ) allWfIds.Add( id2 );

		// ── CHANGES section (only after checking remote) ──
		if ( _hasCheckedRemote )
		{
			var changedIds = _items
				.Where( kv => kv.Value.Status != SyncStatus.Unknown && kv.Value.Status != SyncStatus.InSync )
				.OrderBy( kv => kv.Key )
				.Select( kv => kv.Key )
				.ToList();

			if ( changedIds.Count > 0 )
			{
				DrawSectionHeader( ref y, pad, w, $"CHANGES ({changedIds.Count})" );
				foreach ( var changedId in changedIds )
					DrawChangedItemRow( ref y, pad, w, changedId );
				DrawSeparator( ref y, w, pad );
			}
		}

		// ── Endpoints ──
		var syncedEpSlugs = allSlugs
			.Where( s => !_hasCheckedRemote || GetItemStatus( $"ep_{s}" ) == SyncStatus.InSync || GetItemStatus( $"ep_{s}" ) == SyncStatus.Unknown )
			.OrderBy( s => s ).ToList();

		DrawSectionHeader( ref y, pad, w, $"ENDPOINTS ({allSlugs.Count})" );
		if ( syncedEpSlugs.Count > 0 )
		{
			foreach ( var slug in syncedEpSlugs )
			{
				var id = $"ep_{slug}";
				var localFile = _endpointFiles.FirstOrDefault( f => ResourceIdFromFile( f, "endpoint" ) == slug );
				var hasLocal = localFile != null;
				var deprecated = hasLocal && IsEndpointDeprecated( localFile );
				var info = deprecated ? $"{GetEndpointInfo( localFile )} - deprecated, ignored by sync" : hasLocal ? GetEndpointInfo( localFile ) : "remote only";
				var capturedSlug = slug;
				DrawResourceRow( ref y, pad, w, $"{slug}.endpoint.yml", info, id,
					hasLocal && !deprecated ? () => PushItem( id ) : null,
					deprecated ? null : () => PullItem( id ),
					deprecated ? null : () => TestResultsWindow.OpenAndRun( capturedSlug ),
					deprecated );
			}
		}
		else if ( allSlugs.Count == 0 )
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.3f ) );
			Paint.DrawText( new Rect( pad + 8, y, w, 16 ), "No endpoint files found", TextFlag.LeftCenter );
			y += 22;
		}

		DrawSeparator( ref y, w, pad );

		// ── Collections ──
		var syncedColNames = allColNames
			.Where( n => !_hasCheckedRemote || GetItemStatus( $"col_{n}" ) == SyncStatus.InSync || GetItemStatus( $"col_{n}" ) == SyncStatus.Unknown )
			.OrderBy( n => n ).ToList();

		DrawSectionHeader( ref y, pad, w, $"COLLECTIONS ({allColNames.Count})" );
		if ( syncedColNames.Count > 0 )
		{
			foreach ( var colName in syncedColNames )
			{
				var id = $"col_{colName}";
				var hasLocal = localColNames.Contains( colName );
				DrawResourceRow( ref y, pad, w, $"{colName}.collection.yml", hasLocal ? "schema" : "remote only", id,
					hasLocal ? () => PushItem( id ) : null,
					() => PullItem( id ) );
			}
		}
		else if ( allColNames.Count == 0 )
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.3f ) );
			Paint.DrawText( new Rect( pad + 8, y, w, 16 ), "No collections found", TextFlag.LeftCenter );
			y += 22;
		}

		DrawSeparator( ref y, w, pad );

		// ── Workflows ──
		var syncedWfIds = allWfIds
			.Where( id2 => !_hasCheckedRemote || GetItemStatus( $"wf_{id2}" ) == SyncStatus.InSync || GetItemStatus( $"wf_{id2}" ) == SyncStatus.Unknown )
			.OrderBy( n => n ).ToList();

		DrawSectionHeader( ref y, pad, w, $"WORKFLOWS ({allWfIds.Count})" );
		if ( syncedWfIds.Count > 0 )
		{
			foreach ( var wfId in syncedWfIds )
			{
				var itemId = $"wf_{wfId}";
				var hasLocal = localWfIds.Contains( wfId );
				var info = hasLocal ? GetWorkflowInfo( SyncToolConfig.FindWorkflowFileById( wfId ) ) : "remote only";
				DrawResourceRow( ref y, pad, w, $"{wfId}.workflow.yml", info, itemId,
					hasLocal ? () => PushItem( itemId ) : null,
					() => PullItem( itemId ) );
			}
		}
		else if ( allWfIds.Count == 0 )
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.3f ) );
			Paint.DrawText( new Rect( pad + 8, y, w, 16 ), "No workflow files found", TextFlag.LeftCenter );
			y += 22;
		}

		y += 8;
		DrawSeparator( ref y, w, pad );

		// ── Status bar ──
		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( _busy ? Color.Yellow : _statusIsError ? Color.Red.WithAlpha( 0.9f ) : Color.White.WithAlpha( 0.4f ) );
		Paint.DrawText( new Rect( pad, y, w, 16 ), _status, TextFlag.LeftCenter );
		y += 22;

		// ── Sync log (shown after push/pull) ──
		if ( _syncLog.Count > 0 )
		{
			DrawSeparator( ref y, w, pad );
			DrawSectionHeader( ref y, pad, w, "SYNC RESULTS" );

			var okCount = _syncLog.Count( e => e.Ok );
			var failCount = _syncLog.Count( e => !e.Ok );
			var verifiedCount = _syncLog.Count( e => e.Detail != null && e.Detail.Contains( "Verified" ) );
			var mismatchCount = _syncLog.Count( e => e.Detail != null && e.Detail.Contains( "Mismatch" ) );

			// Summary counts
			Paint.SetDefaultFont( size: 9, weight: 600 );
			var sx = pad + 8f;
			if ( okCount > 0 )
			{
				Paint.SetPen( Color.Green.WithAlpha( 0.8f ) );
				Paint.DrawText( new Rect( sx, y, 90, 16 ), $"✓ {okCount} pushed", TextFlag.LeftCenter );
				sx += 80;
			}
			if ( verifiedCount > 0 )
			{
				Paint.SetPen( Color.Cyan.WithAlpha( 0.8f ) );
				Paint.DrawText( new Rect( sx, y, 90, 16 ), $"● {verifiedCount} verified", TextFlag.LeftCenter );
				sx += 85;
			}
			if ( mismatchCount > 0 )
			{
				Paint.SetPen( Color.Orange.WithAlpha( 0.9f ) );
				Paint.DrawText( new Rect( sx, y, 100, 16 ), $"▲ {mismatchCount} mismatch", TextFlag.LeftCenter );
				sx += 90;
			}
			var mergeLogCount = _syncLog.Count( e => e.Detail != null && e.Detail.Contains( "Remote semantics available" ) );
			if ( mergeLogCount > 0 )
			{
				Paint.SetPen( Color.Green.WithAlpha( 0.8f ) );
				Paint.DrawText( new Rect( sx, y, 140, 16 ), $"⇄ {mergeLogCount} semantics", TextFlag.LeftCenter );
				sx += 120;
			}
			if ( failCount > 0 )
			{
				Paint.SetPen( Color.Red.WithAlpha( 0.8f ) );
				Paint.DrawText( new Rect( sx, y, 90, 16 ), $"✗ {failCount} failed", TextFlag.LeftCenter );
			}
			y += 22;

			// Per-item log
			foreach ( var entry in _syncLog )
			{
				if ( y > _scrollAreaTop && y < Height )
				{
					// Icon
					var isMismatch = entry.Detail != null && entry.Detail.Contains( "Mismatch" );
					var isVerified = entry.Detail != null && entry.Detail.Contains( "Verified" );
					var isMergeAvail = entry.Detail != null && entry.Detail.Contains( "Remote semantics available" );
					var icon = isMergeAvail ? "⇄"
						: entry.Ok ? ( isVerified ? "●" : "✓" )
						: ( isMismatch ? "▲" : "✗" );
					var iconColor = isMergeAvail ? Color.Green.WithAlpha( 0.7f )
						: entry.Ok ? ( isVerified ? Color.Cyan.WithAlpha( 0.7f ) : Color.Green.WithAlpha( 0.6f ) )
						: ( isMismatch ? Color.Orange.WithAlpha( 0.8f ) : Color.Red.WithAlpha( 0.7f ) );

					Paint.SetDefaultFont( size: 9 );
					Paint.SetPen( iconColor );
					Paint.DrawText( new Rect( pad + 8, y, 16, 16 ), icon, TextFlag.Center );

					// Name
					Paint.SetPen( Color.White.WithAlpha( 0.8f ) );
					var nameW = w * 0.35f;
					Paint.DrawText( new Rect( pad + 26, y, nameW, 16 ), entry.Name, TextFlag.LeftCenter );

					// Detail
					var detailText = entry.Detail ?? ( entry.Ok ? "Pushed" : "Failed" );
					var detailColor = isMismatch ? Color.Orange.WithAlpha( 0.8f )
						: isVerified ? Color.Cyan.WithAlpha( 0.6f )
						: entry.Ok ? Color.Green.WithAlpha( 0.5f )
						: Color.Red.WithAlpha( 0.6f );
					Paint.SetDefaultFont( size: 8 );
					Paint.SetPen( detailColor );
					Paint.DrawText( new Rect( pad + 26 + nameW + 4, y, w - nameW - 100, 16 ), detailText, TextFlag.LeftCenter );

					// Type badge
					Paint.SetPen( Color.White.WithAlpha( 0.25f ) );
					Paint.DrawText( new Rect( pad + w - 70, y, 70, 16 ), entry.Type, TextFlag.RightCenter );
				}
				y += 18;
			}
			y += 8;
		}

		// ── Record content height for scrollbar ──
		_contentHeight = ( y + _scrollY ) - _scrollAreaTop;

		// ── Redraw header background to cover scrolled content ──
		Paint.SetBrush( new Color( 0.133f, 0.133f, 0.133f ) );
		Paint.SetPen( Color.Transparent );
		Paint.DrawRect( new Rect( 0, 0, Width, _scrollAreaTop ) );

		// Re-draw header (replayed from top)
		RedrawHeader( pad, w );

		// ── Scrollbar ──
		if ( MaxScroll > 0 )
		{
			var trackX = Width - 8;
			var trackH = Height - _scrollAreaTop - 4;
			var viewRatio = ( Height - _scrollAreaTop ) / _contentHeight;
			var thumbH = Math.Max( 20, trackH * viewRatio );
			var thumbY = _scrollAreaTop + ( _scrollY / MaxScroll ) * ( trackH - thumbH );

			Paint.SetBrush( Color.White.WithAlpha( 0.04f ) );
			Paint.SetPen( Color.Transparent );
			Paint.DrawRect( new Rect( trackX, _scrollAreaTop, 6, trackH ) );

			Paint.SetBrush( Color.White.WithAlpha( 0.15f ) );
			Paint.DrawRect( new Rect( trackX, thumbY, 6, thumbH ), 3 );
		}
	}

	/// <summary>
	/// Redraws the fixed header area on top of scrolled content so it doesn't bleed through.
	/// </summary>
	private void RedrawHeader( float pad, float w )
	{
		var y = 38f;

		// Header + Push All + Test All
		Paint.SetDefaultFont( size: 13, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w * 0.55f, 22 ), "Network Storage Sync", TextFlag.LeftCenter );

		if ( SyncToolConfig.IsValid )
		{
			var btnW2 = 70f;
			var testAllW = 65f;
			var pushAllRect = new Rect( pad + w - btnW2, y, btnW2, 22 );
			var testAllRect = new Rect( pad + w - btnW2 - 4 - testAllW, y, testAllW, 22 );
			DrawSmallButton( pushAllRect, "Push All", Color.Green, "push_all", () => _ = PushAll() );
			DrawSmallButton( testAllRect, "Test All", Color.Cyan, "test_all", () => TestResultsWindow.OpenAndRun() );
		}
		y += 30;

		// Config status
		if ( !SyncToolConfig.IsValid )
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.Red );
			Paint.DrawText( new Rect( pad, y, w, 16 ), "Not configured — click Setup to enter your keys", TextFlag.LeftCenter );
			y += 24;
		}
		else
		{
			Paint.SetDefaultFont( size: 9 );
			Paint.SetPen( Color.Green.WithAlpha( 0.8f ) );
			Paint.DrawText( new Rect( pad, y, w, 14 ), $"Connected — {SyncToolConfig.ProjectId}", TextFlag.LeftCenter );
			y += 18;

			Paint.SetDefaultFont( size: 9 );
			Paint.SetPen( Color.White.WithAlpha( 0.72f ) );
			Paint.DrawText( new Rect( pad, y, w, 14 ),
				$"Auth Sessions: {SyncToolConfig.AuthSessionsLabel}   Encrypted Requests: {SyncToolConfig.EncryptedRequestsLabel}",
				TextFlag.LeftCenter );
			y += 20;
		}

		// Check for Updates button
		if ( SyncToolConfig.IsValid )
		{
			var checkBtnH = 26f;
			var checkLabel = _hasCheckedRemote ? "Pull from Web (re-check)" : "Check for Updates";
			var checkRect = new Rect( pad, y, w, checkBtnH );
			DrawWideButton( checkRect, checkLabel, Color.Cyan, "check_updates", () => _ = CheckForUpdates() );
			y += checkBtnH + 8;

			var remoteSemanticsCount = GetRemoteSemanticsCount();
			if ( remoteSemanticsCount > 0 )
			{
				var semanticsLabel = remoteSemanticsCount == 1
					? "Pull All (1 semantic item)"
					: $"Pull All ({remoteSemanticsCount} semantic items)";
				var semanticsRect = new Rect( pad, y, w, checkBtnH );
				DrawWideButton( semanticsRect, semanticsLabel, Color.Green, "pull_remote_semantics_all",
					() => PullAllRemoteSemantics() );
				y += checkBtnH + 8;
			}
		}

		DrawSeparator( ref y, w, pad );
	}

	// ──────────────────────────────────────────────────────
	//  Drawing helpers
	// ──────────────────────────────────────────────────────

	private void DrawSectionHeader( ref float y, float pad, float w, string title )
	{
		Paint.SetDefaultFont( size: 10, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad, y, w, 18 ), title, TextFlag.LeftCenter );
		y += 24;
	}

	private void DrawSeparator( ref float y, float w, float pad )
	{
		Paint.SetPen( Color.White.WithAlpha( 0.08f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 8;
	}

	private void DrawSmallButton( Rect rect, string label, Color color, string id, Action onClick )
	{
		var hovered = rect.IsInside( _mousePos );
		var isBusy = _busy && _busyItem == id;

		Paint.SetBrush( color.WithAlpha( isBusy ? 0.2f : hovered ? 0.15f : 0.08f ) );
		Paint.SetPen( color.WithAlpha( hovered ? 0.5f : 0.25f ) );
		Paint.DrawRect( rect, 3 );
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( isBusy ? Color.Yellow : color.WithAlpha( hovered ? 1f : 0.7f ) );
		Paint.DrawText( rect, isBusy ? "..." : label, TextFlag.Center );

		if ( !_busy )
			_buttons.Add( new ClickRegion { Rect = rect, Id = id, OnClick = onClick } );
	}

	private void DrawWideButton( Rect rect, string label, Color color, string id, Action onClick )
	{
		var hovered = rect.IsInside( _mousePos );
		var isBusy = _busy && _busyItem == id;

		Paint.SetBrush( color.WithAlpha( isBusy ? 0.15f : hovered ? 0.1f : 0.05f ) );
		Paint.SetPen( color.WithAlpha( hovered ? 0.4f : 0.2f ) );
		Paint.DrawRect( rect, 4 );
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( isBusy ? Color.Yellow : color.WithAlpha( hovered ? 0.9f : 0.6f ) );
		Paint.DrawText( rect, isBusy ? "Checking..." : label, TextFlag.Center );

		if ( !_busy )
			_buttons.Add( new ClickRegion { Rect = rect, Id = id, OnClick = onClick } );
	}

	private void DrawResourceRow( ref float y, float pad, float w, string name, string info, string id,
		Action pushAction, Action pullAction, Action testAction = null, bool deprecated = false )
	{
		var rowH = 28f;
		var btnW = 48f;
		var btnH = 20f;
		var rowRect = new Rect( pad, y, w, rowH );
		var hovered = rowRect.IsInside( _mousePos );

		if ( hovered )
		{
			Paint.SetBrush( Color.White.WithAlpha( 0.03f ) );
			Paint.SetPen( Color.Transparent );
			Paint.DrawRect( rowRect, 3 );
		}

		// Status icon + label (leftmost)
		_items.TryGetValue( id, out var state );
		var hasResult = !string.IsNullOrEmpty( state.SyncResult );
		var hasStatusBadge = _hasCheckedRemote || hasResult;
		var hasIndicator = hasResult || state.RemoteDiffers || state.Status == SyncStatus.MergeAvailable || ( _hasCheckedRemote && state.Status != SyncStatus.Unknown );

		// Icon
		if ( hasResult )
		{
			var iconColor = state.SyncResult == "OK" ? Color.Green.WithAlpha( 0.8f ) : Color.Red.WithAlpha( 0.8f );
			Paint.SetPen( iconColor );
			Paint.SetDefaultFont( size: 9 );
			Paint.DrawText( new Rect( pad + 2, y, 18, rowH ), state.SyncResult == "OK" ? "✓" : "✗", TextFlag.Center );
		}
		else if ( state.Status == SyncStatus.MergeAvailable )
		{
			Paint.SetPen( Color.Green.WithAlpha( 0.8f ) );
			Paint.SetDefaultFont( size: 9 );
			Paint.DrawText( new Rect( pad + 2, y, 18, rowH ), "⇄", TextFlag.Center );
		}
		else if ( state.RemoteDiffers || state.Status == SyncStatus.Differs )
		{
			Paint.SetPen( Color.Orange.WithAlpha( 0.8f ) );
			Paint.SetDefaultFont( size: 9 );
			Paint.DrawText( new Rect( pad + 2, y, 18, rowH ), "●", TextFlag.Center );
		}
		else if ( state.Status == SyncStatus.LocalOnly )
		{
			Paint.SetPen( Color.Yellow.WithAlpha( 0.8f ) );
			Paint.SetDefaultFont( size: 9 );
			Paint.DrawText( new Rect( pad + 2, y, 18, rowH ), "▲", TextFlag.Center );
		}
		else if ( state.Status == SyncStatus.InSync )
		{
			Paint.SetPen( Color.Green.WithAlpha( 0.5f ) );
			Paint.SetDefaultFont( size: 9 );
			Paint.DrawText( new Rect( pad + 2, y, 18, rowH ), "✓", TextFlag.Center );
		}
		else if ( state.Status == SyncStatus.RemoteOnly )
		{
			Paint.SetPen( Color.Cyan.WithAlpha( 0.8f ) );
			Paint.SetDefaultFont( size: 9 );
			Paint.DrawText( new Rect( pad + 2, y, 18, rowH ), "▼", TextFlag.Center );
		}

		var contentX = pad + ( hasIndicator ? 22 : 8 );
		var btnY = y + ( rowH - btnH ) / 2;

		// Pull/Review button — LEFT side (only if remote has changes to pull)
		if ( state.Status == SyncStatus.MergeAvailable )
		{
			var mergeW = 56f;
			var mergeRect = new Rect( contentX, btnY, mergeW, btnH );
			var capturedMergeId = id;
			var capturedMergeName = name;
			DrawSmallButton( mergeRect, "Review", Color.Green, $"merge_{id}",
				() => OpenMergeView( capturedMergeId, capturedMergeName ) );
			contentX += mergeW + 6;
		}
		else if ( state.RemoteDiffers )
		{
			var pullRect = new Rect( contentX, btnY, btnW, btnH );
			DrawSmallButton( pullRect, "Pull", Color.Cyan, $"pull_{id}", pullAction );
			contentX += btnW + 6;
		}

		// File name
		// Calculate right-side button area width
		var rightBtnsW = 4f;
		if ( pushAction != null ) rightBtnsW += btnW + 4;
		if ( testAction != null ) rightBtnsW += btnW + 4;

		Paint.SetDefaultFont( size: 10 );
		Paint.SetPen( deprecated ? Color.White.WithAlpha( 0.4f ) : Color.White.WithAlpha( 0.9f ) );
		var badgeReserve = hasStatusBadge && !hasResult ? 86f : 70f;
		var nameW = w - ( contentX - pad ) - rightBtnsW - badgeReserve;
		Paint.DrawText( new Rect( contentX, y, nameW, rowH ), name, TextFlag.LeftCenter );

		// Deprecated badge
		if ( deprecated )
		{
			var depBadgeX = contentX + Paint.MeasureText( name ).x + 6;
			Paint.SetDefaultFont( size: 7 );

			var depText = "deprecated";
			var depTextW = Paint.MeasureText( depText ).x + 8;
			var depBadgeRect = new Rect( depBadgeX, y + ( rowH - 14 ) / 2, depTextW, 14 );

			Paint.SetBrush( new Color( 0.96f, 0.62f, 0.04f, 0.12f ) );
			Paint.SetPen( new Color( 0.96f, 0.62f, 0.04f, 0.25f ) );
			Paint.DrawRect( depBadgeRect, 3 );

			Paint.SetPen( new Color( 0.96f, 0.62f, 0.04f, 0.7f ) );
			Paint.DrawText( depBadgeRect, depText, TextFlag.Center );
		}

		// Status badge (right of name, before buttons)
		if ( hasStatusBadge && !hasResult )
		{
			var (badgeText, badgeColor) = state.Status switch
			{
				SyncStatus.InSync => ("Synced", Color.Green.WithAlpha( 0.5f )),
				SyncStatus.LocalOnly => ("Local", Color.Yellow.WithAlpha( 0.7f )),
				SyncStatus.RemoteOnly => ("Remote", Color.Cyan.WithAlpha( 0.7f )),
				SyncStatus.Differs => ("Changed", Color.Orange.WithAlpha( 0.7f )),
				SyncStatus.MergeAvailable => ("Semantic", Color.Green.WithAlpha( 0.7f )),
				_ => ("", Color.White.WithAlpha( 0.3f ))
			};

			if ( !string.IsNullOrEmpty( badgeText ) )
			{
				Paint.SetDefaultFont( size: 8 );
				Paint.SetPen( badgeColor );
				var badgeX = contentX + nameW + 4;
				var badgeW = Math.Max( 50f, Paint.MeasureText( badgeText ).x + 8 );
				Paint.DrawText( new Rect( badgeX, y, badgeW, rowH ), badgeText, TextFlag.LeftCenter );
			}
		}

		// Test button — RIGHT side, before Push
		var rightX = pad + w - 4;
		if ( pushAction != null )
		{
			rightX -= btnW;
			var pushRect = new Rect( rightX, btnY, btnW, btnH );
			DrawSmallButton( pushRect, "Push", Color.White, $"push_{id}", pushAction );
			rightX -= 4;
		}
		if ( testAction != null )
		{
			rightX -= btnW;
			var testRect = new Rect( rightX, btnY, btnW, btnH );
			DrawSmallButton( testRect, "Test", Color.Cyan, $"test_{id}", _busy ? null : testAction );
		}

		y += rowH + 1;

		// Diff/status details (below the row)
		if ( !string.IsNullOrEmpty( state.DiffSummary ) && ( state.RemoteDiffers || state.Status == SyncStatus.LocalOnly || state.Status == SyncStatus.MergeAvailable ) )
		{
			// Summary text
			var summaryColor = state.Status == SyncStatus.MergeAvailable ? Color.Green.WithAlpha( 0.6f )
				: state.Status == SyncStatus.LocalOnly ? Color.Yellow.WithAlpha( 0.6f )
				: Color.Orange.WithAlpha( 0.6f );
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( summaryColor );
			Paint.DrawText( new Rect( pad + 22, y, w - 80, 14 ), state.DiffSummary, TextFlag.LeftCenter );

			// Action buttons on the diff line
			if ( state.Status != SyncStatus.LocalOnly && state.Status != SyncStatus.MergeAvailable )
			{
				var btnX = pad + w - 4;

				// View Diff button
				var diffBtnW = 56f;
				var diffBtnH = 16f;
				btnX -= diffBtnW;
				var diffBtnRect = new Rect( btnX, y - 1, diffBtnW, diffBtnH );
				var diffHovered = diffBtnRect.IsInside( _mousePos );

				Paint.SetBrush( Color.Orange.WithAlpha( diffHovered ? 0.2f : 0.08f ) );
				Paint.SetPen( Color.Orange.WithAlpha( diffHovered ? 0.6f : 0.25f ) );
				Paint.DrawRect( diffBtnRect, 3 );
				Paint.SetDefaultFont( size: 8, weight: 600 );
				Paint.SetPen( Color.Orange.WithAlpha( diffHovered ? 1f : 0.7f ) );
				Paint.DrawText( diffBtnRect, "View Diff", TextFlag.Center );

				var capturedId = id;
				var capturedName = name;
				if ( !_busy )
					_buttons.Add( new ClickRegion { Rect = diffBtnRect, Id = $"diff_{id}", OnClick = () => OpenDiffView( capturedId, capturedName ) } );

				// Merge Meta button (for collections where schema is same but metadata differs)
				if ( id.StartsWith( "col_" ) && state.DiffSummary != null && state.DiffSummary.Contains( "schema: identical" ) && state.DiffSummary.Contains( "metadata differs" ) )
				{
					var mergeBtnW = 68f;
					btnX -= mergeBtnW + 4;
					var mergeBtnRect = new Rect( btnX, y - 1, mergeBtnW, diffBtnH );
					var mergeHovered = mergeBtnRect.IsInside( _mousePos );

					Paint.SetBrush( Color.Green.WithAlpha( mergeHovered ? 0.2f : 0.08f ) );
					Paint.SetPen( Color.Green.WithAlpha( mergeHovered ? 0.6f : 0.25f ) );
					Paint.DrawRect( mergeBtnRect, 3 );
					Paint.SetDefaultFont( size: 8, weight: 600 );
					Paint.SetPen( Color.Green.WithAlpha( mergeHovered ? 1f : 0.7f ) );
					Paint.DrawText( mergeBtnRect, "Merge Meta", TextFlag.Center );

					if ( !_busy )
						_buttons.Add( new ClickRegion { Rect = mergeBtnRect, Id = $"merge_{id}", OnClick = () => MergeMetadata( capturedId ) } );
				}
			}

			y += 18;
		}

		y += 1;
	}

	// ──────────────────────────────────────────────────────
	//  Data Sources (C# → Collection sync mappings)
	// ──────────────────────────────────────────────────────

	private void DrawDataSourcesSection( ref float y, float pad, float w )
	{
		DrawSectionHeader( ref y, pad, w, $"DATA SOURCES ({SyncToolConfig.SyncMappings.Count})" );

		foreach ( var mapping in SyncToolConfig.SyncMappings )
		{
			var status = GetDataSourceStatus( mapping );

			var rowH = 28f;
			var btnW = 68f;
			var btnH = 20f;
			var rowRect = new Rect( pad, y, w, rowH );
			var hovered = rowRect.IsInside( _mousePos );

			if ( hovered )
			{
				Paint.SetBrush( Color.White.WithAlpha( 0.03f ) );
				Paint.SetPen( Color.Transparent );
				Paint.DrawRect( rowRect, 3 );
			}

			// Status icon
			Paint.SetDefaultFont( size: 9 );
			Paint.SetPen( status.Color );
			Paint.DrawText( new Rect( pad + 2, y, 18, rowH ), status.Icon, TextFlag.Center );

			// Mapping label
			Paint.SetDefaultFont( size: 9 );
			Paint.SetPen( Color.White.WithAlpha( 0.75f ) );
			var labelSuffix = status.IsStale ? " (stale)" : "";
			Paint.DrawText( new Rect( pad + 22, y, w - btnW - 30, rowH ),
				$"{mapping.CsFile} → {mapping.Collection}.collection.yml{labelSuffix}", TextFlag.LeftCenter );

			// Generate button
			if ( status.SourceExists )
			{
				var btnY = y + ( rowH - btnH ) / 2;
				var btnRect = new Rect( pad + w - btnW - 4, btnY, btnW, btnH );
				var genId = $"gen_{mapping.Collection}";
				var isBusyGen = _generateBusy && _busyItem == genId;
				var capturedCollection = mapping.Collection;
				DrawSmallButton( btnRect, isBusyGen ? "..." : "Generate", Color.Green, genId,
					() => ConfirmDialog.Show(
						"Generate Collection Data",
						$"This will overwrite {capturedCollection}.collection.yml with data parsed from your C# source files.",
						() => _ = RunGenerate( capturedCollection ),
						"Local JSON will be regenerated from C# definitions" ) );
			}

			y += rowH + 1;

			// Description
			var detailText = string.IsNullOrEmpty( mapping.Description )
				? status.Detail
				: $"{status.Label}: {status.Detail} - {mapping.Description}";
			if ( !string.IsNullOrEmpty( detailText ) )
			{
				Paint.SetDefaultFont( size: 8 );
				Paint.SetPen( status.IsStale ? Color.Yellow.WithAlpha( 0.75f ) : Color.White.WithAlpha( 0.35f ) );
				Paint.DrawText( new Rect( pad + 22, y, w - 30, 14 ), detailText, TextFlag.LeftCenter );
				y += 16;
			}
		}

	}

	private async Task RunGenerate( string collectionFilter )
	{
		if ( _generateBusy ) return;
		_generateBusy = true;
		_busyItem = collectionFilter != null ? $"gen_{collectionFilter}" : "generate_all";
		SetStatus( "Generating collection data from C# sources..." );
		Update();

		try
		{
			var syncPy = SyncToolConfig.Abs( SyncToolConfig.SyncPyPath );
			if ( !File.Exists( syncPy ) )
			{
				ShowGenerateFailure( $"sync.py not found at {SyncToolConfig.SyncPyPath}" );
				return;
			}

			var projectRoot = SyncToolConfig.ProjectRoot;
			var args = $"\"{syncPy}\" --project-root \"{projectRoot}\" --generate";
			if ( collectionFilter != null )
				args += $" --collection \"{collectionFilter}\"";

			var psi = new ProcessStartInfo
			{
				FileName = "python",
				Arguments = args,
				WorkingDirectory = SyncToolConfig.ProjectRoot,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			var process = Process.Start( psi );
			if ( process == null )
			{
				ShowGenerateFailure( "Failed to start Python. Check that Python is installed and on PATH." );
				return;
			}

			var stdout = await Task.Run( () => process.StandardOutput.ReadToEnd() );
			var stderr = await Task.Run( () => process.StandardError.ReadToEnd() );
			process.WaitForExit( 30000 );
			var trimmedStdout = stdout?.Trim();
			var trimmedStderr = stderr?.Trim();

			// Log output to console, not inline
			if ( !string.IsNullOrWhiteSpace( trimmedStdout ) )
				Log.Info( $"[SyncTool] sync.py output:\n{trimmedStdout}" );
			if ( !string.IsNullOrWhiteSpace( trimmedStderr ) )
				Log.Warning( $"[SyncTool] sync.py errors:\n{trimmedStderr}" );

			if ( process.ExitCode == 0 )
			{
				if ( string.IsNullOrWhiteSpace( trimmedStdout ) && string.IsNullOrWhiteSpace( trimmedStderr ) )
				{
					ShowGenerateFailure(
						"sync.py exited with code 0 but produced no output",
						$"Command: python {args}\nWorking directory: {SyncToolConfig.ProjectRoot}" );
				}
				else
				{
					SetStatus( "Generation complete" );
					RefreshFileList();
				}
			}
			else
			{
				var summary = ExtractGenerateFailureSummary( stdout, stderr, process.ExitCode );
				var detail = BuildGenerateFailureDetail( stdout, stderr );
				ShowGenerateFailure( summary, detail );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Generate error: {ex.Message}" );
			ShowGenerateFailure( $"Generate failed: {ex.Message}" );
		}
		finally
		{
			_generateBusy = false;
			_busyItem = null;
			Update();
		}
	}

	// ──────────────────────────────────────────────────────
	//  Mouse + Scroll
	// ──────────────────────────────────────────────────────

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );
		if ( _busy ) return;

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
		base.OnMouseMove( e );
		_mousePos = e.LocalPosition;
		Update();
	}

	protected override void OnMouseWheel( WheelEvent e )
	{
		var direction = e.Delta > 0 ? -1 : 1;
		_scrollY = Math.Clamp( _scrollY + direction * RowH * 3, 0, MaxScroll );
		Update();
		e.Accept();
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		var handled = true;
		switch ( e.Key )
		{
			case KeyCode.Up: _scrollY = Math.Max( 0, _scrollY - RowH ); break;
			case KeyCode.Down: _scrollY = Math.Min( MaxScroll, _scrollY + RowH ); break;
			case KeyCode.PageUp: _scrollY = Math.Max( 0, _scrollY - RowH * 10 ); break;
			case KeyCode.PageDown: _scrollY = Math.Min( MaxScroll, _scrollY + RowH * 10 ); break;
			case KeyCode.Home: _scrollY = 0; break;
			case KeyCode.End: _scrollY = MaxScroll; break;
			default: handled = false; break;
		}

		if ( handled ) Update();
		else base.OnKeyPress( e );
	}

	/// <summary>Scroll to show the bottom of the content.</summary>
	private void ScrollToBottom()
	{
		if ( !IsValid ) return;
		_scrollY = MaxScroll;
		Update();
	}

	// ──────────────────────────────────────────────────────
	//  Helpers
	// ──────────────────────────────────────────────────────

	private bool HasRemoteDiff( string idPrefix )
	{
		return _items.Any( kv => kv.Key.StartsWith( idPrefix ) && kv.Value.RemoteDiffers );
	}

	private SyncStatus GetItemStatus( string id )
	{
		return _items.TryGetValue( id, out var s ) ? s.Status : SyncStatus.Unknown;
	}

	/// <summary>
	/// Draws a row in the CHANGES section — reconstructs push/pull actions from the item id prefix.
	/// </summary>
	private void DrawChangedItemRow( ref float y, float pad, float w, string id )
	{
		string name, info;
		Action pushAction, pullAction;
		Action testAction = null;

		bool deprecated = false;

		if ( id.StartsWith( "ep_" ) )
		{
			var slug = id.Substring( 3 );
			var localFile = _endpointFiles.FirstOrDefault( f => ResourceIdFromFile( f, "endpoint" ) == slug );
			var hasLocal = localFile != null;
			deprecated = hasLocal && IsEndpointDeprecated( localFile );
			name = $"{slug}.endpoint.yml";
			info = deprecated ? $"{GetEndpointInfo( localFile )} - deprecated, ignored by sync" : hasLocal ? GetEndpointInfo( localFile ) : "remote only";
			var capturedId = id;
			var capturedSlug = slug;
			pushAction = hasLocal && !deprecated ? () => PushItem( capturedId ) : null;
			pullAction = deprecated ? null : () => PullItem( capturedId );
			testAction = deprecated ? null : () => TestResultsWindow.OpenAndRun( capturedSlug );
		}
		else if ( id.StartsWith( "col_" ) )
		{
			var colName = id.Substring( 4 );
			var hasLocal = _collectionFiles.Any( f => ResourceIdFromFile( f, "collection" ) == colName );
			name = $"{colName}.collection.yml";
			info = hasLocal ? "schema" : "remote only";
			var capturedId = id;
			pushAction = hasLocal ? () => PushItem( capturedId ) : null;
			pullAction = () => PullItem( capturedId );
		}
		else if ( id.StartsWith( "wf_" ) )
		{
			var wfId = id.Substring( 3 );
			var localFile = SyncToolConfig.FindWorkflowFileById( wfId );
			var hasLocal = localFile != null;
			name = $"{wfId}.workflow.yml";
			info = hasLocal ? GetWorkflowInfo( localFile ) : "remote only";
			var capturedId = id;
			pushAction = hasLocal ? () => PushItem( capturedId ) : null;
			pullAction = () => PullItem( capturedId );
		}
		else return;

		DrawResourceRow( ref y, pad, w, name, info, id, pushAction, pullAction, testAction, deprecated );
	}

	private List<string> GetRemoteEndpointSlugs()
	{
		if ( !_remoteEndpoints.HasValue ) return new List<string>();
		var data = _remoteEndpoints.Value;
		if ( data.TryGetProperty( "data", out var d ) ) data = d;
		if ( data.ValueKind != JsonValueKind.Array ) return new List<string>();

		var slugs = new List<string>();
		foreach ( var ep in data.EnumerateArray() )
		{
			if ( SyncToolConfig.IsEndpointDeprecated( ep ) )
				continue;

			var slug = GetRemoteEndpointSlug( ep );
			if ( !string.IsNullOrEmpty( slug ) )
				slugs.Add( slug );
		}
		return slugs;
	}

	private static string GetRemoteEndpointSlug( JsonElement ep )
	{
		if ( ep.TryGetProperty( "slug", out var s ) && s.ValueKind == JsonValueKind.String )
			return s.GetString();

		var local = SyncToolTransforms.ServerEndpointToLocal( ep );
		return local.TryGetValue( "slug", out var value ) ? value?.ToString() : "";
	}

	private List<string> GetRemoteCollectionNames()
	{
		if ( !_remoteCollections.HasValue ) return new List<string>();
		var collections = SyncToolTransforms.ServerToCollections( _remoteCollections.Value );
		return collections.Select( c => c.Name ).ToList();
	}

	private List<string> GetRemoteWorkflowIds()
	{
		if ( !_remoteWorkflows.HasValue ) return new List<string>();
		var data = _remoteWorkflows.Value;
		if ( data.TryGetProperty( "data", out var d ) ) data = d;
		if ( data.ValueKind != JsonValueKind.Array ) return new List<string>();

		var ids = new List<string>();
		foreach ( var wf in data.EnumerateArray() )
		{
			if ( wf.TryGetProperty( "id", out var id ) )
				ids.Add( id.GetString() );
		}
		return ids;
	}

	private string GetWorkflowInfo( string filePath )
	{
		try
		{
			if ( !TryReadLocalResourceFile( filePath, "workflow", out var wf ) )
				return "";
			return wf.TryGetProperty( "name", out var n ) ? n.GetString() : "workflow";
		}
		catch { return ""; }
	}

	private string GetEndpointInfo( string filePath )
	{
		try
		{
			if ( !TryReadLocalResourceFile( filePath, "endpoint", out var ep ) )
				return "";
			return ep.TryGetProperty( "method", out var m ) ? m.GetString() : "POST";
		}
		catch { return ""; }
	}

	private string[] GetActiveEndpointFiles()
	{
		return _endpointFiles.Where( f => !IsEndpointDeprecated( f ) ).ToArray();
	}

	private bool IsEndpointDeprecated( string filePath )
	{
		try
		{
			if ( !TryReadLocalResourceFile( filePath, "endpoint", out var ep ) )
				return false;
			return SyncToolConfig.IsEndpointDeprecated( ep );
		}
		catch { return false; }
	}

	private bool TryReadLocalResourceFile( string filePath, string kind, out JsonElement resource )
	{
		resource = default;
		var extension = Path.GetExtension( filePath );
		if ( extension.Equals( ".json", StringComparison.OrdinalIgnoreCase ) )
		{
			resource = JsonSerializer.Deserialize<JsonElement>( File.ReadAllText( filePath ), _readOptions );
			return resource.ValueKind == JsonValueKind.Object;
		}

		return SyncToolConfig.TryLoadSourceCanonicalResource( kind, filePath, out resource );
	}

	private void SetItemState( string id, string result = null, bool? remoteDiffers = null,
		string diffSummary = null, string localJson = null, string remoteJson = null, SyncStatus? status = null )
	{
		_items.TryGetValue( id, out var state );
		if ( result != null ) state.SyncResult = result;
		if ( remoteDiffers.HasValue ) state.RemoteDiffers = remoteDiffers.Value;
		if ( diffSummary != null ) state.DiffSummary = diffSummary;
		if ( localJson != null ) state.LocalJson = localJson;
		if ( remoteJson != null ) state.RemoteJson = remoteJson;
		if ( status.HasValue ) state.Status = status.Value;
		_items[id] = state;
	}

	private void ClearAllRemoteDiffs()
	{
		foreach ( var key in _items.Keys.ToList() )
		{
			var s = _items[key];
			s.RemoteDiffers = false;
			_items[key] = s;
		}
	}

	private string[] GetRemoteSemanticsIds()
	{
		return _items
			.Where( x => x.Value.Status == SyncStatus.MergeAvailable && !string.IsNullOrEmpty( x.Value.RemoteJson ) )
			.Select( x => x.Key )
			.OrderBy( x => x )
			.ToArray();
	}

	private int GetRemoteSemanticsCount()
	{
		return GetRemoteSemanticsIds().Length;
	}

	// ──────────────────────────────────────────────────────
	//  Check for Updates (compare local vs remote)
	// ──────────────────────────────────────────────────────

	private async Task CheckForUpdates()
	{
		if ( _busy || !SyncToolConfig.IsValid ) return;
		_scrollY = 0;
		_busy = true;
		_busyItem = "check_updates";
		_status = "Checking remote for changes...";
		_items.Clear();
		_syncLog.Clear();
		RefreshFileList();
		Update();

		try
		{
			await DoCheckForUpdates();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Check for updates failed: {ex.Message}" );
			_status = $"Check failed: {ex.Message}";
		}
		finally
		{
			_busy = false;
			_busyItem = null;
			Update();
		}
	}

	private async Task DoCheckForUpdates()
	{
		_items.Clear();

		var diffs = 0;
		var remoteSemanticsCount = 0;
		var localOnlyCount = 0;

		// ── Load local files via SyncToolConfig (uses System.IO with correct project root) ──
		var localEndpoints = SyncToolConfig.LoadEndpoints();
		var localCollections = SyncToolConfig.LoadCollections();
		var localWorkflows = SyncToolConfig.LoadWorkflows();

		var localEpBySlug = new Dictionary<string, JsonElement>();
		foreach ( var ep in localEndpoints )
		{
			var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
			if ( !string.IsNullOrEmpty( slug ) ) localEpBySlug[slug] = ep;
		}

		// Also track deprecated local files so the diff loop can distinguish
		// "remote endpoint has no local counterpart" (truly remote-only) from
		// "remote endpoint has a local counterpart flagged deprecated" (intentionally ignored).
		var localDeprecatedSlugs = new HashSet<string>();
		foreach ( var ep in SyncToolConfig.LoadEndpoints( includeDeprecated: true ) )
		{
			if ( !SyncToolConfig.IsEndpointDeprecated( ep ) ) continue;
			var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
			if ( !string.IsNullOrEmpty( slug ) ) localDeprecatedSlugs.Add( slug );
		}

		var localColByName = new Dictionary<string, string>();
		foreach ( var (name, data) in localCollections )
		{
			var stripped = SyncToolTransforms.StripServerManagedFields( data );
			localColByName[name] = JsonSerializer.Serialize( stripped, new JsonSerializerOptions { WriteIndented = true } );
		}

		var localWfById = new Dictionary<string, JsonElement>();
		foreach ( var wf in localWorkflows )
		{
			var wfId = wf.TryGetProperty( "id", out var id ) ? id.GetString() : "";
			if ( !string.IsNullOrEmpty( wfId ) ) localWfById[wfId] = wf;
		}

		// ── Fetch endpoints, collections, workflows in parallel ──
		var remoteEpsTask = SyncToolApi.GetEndpoints();
		var remoteColsTask = SyncToolApi.GetCollections();
		var remoteWfsTask = SyncToolApi.GetWorkflows();
		var projectSettingsTask = SyncToolApi.GetProjectSettings();

		await Task.WhenAll( remoteEpsTask, remoteColsTask, remoteWfsTask, projectSettingsTask );

		_remoteEndpoints = await remoteEpsTask;
		_remoteCollections = await remoteColsTask;
		_remoteWorkflows = await remoteWfsTask;
		var projectSettings = await projectSettingsTask;
		if ( projectSettings.HasValue )
			SyncToolConfig.TryApplyProjectSecuritySettings( projectSettings.Value );
		if ( _remoteEndpoints.HasValue )
			SyncToolConfig.TryApplyProjectSecuritySettings( _remoteEndpoints.Value );
		if ( _remoteCollections.HasValue )
			SyncToolConfig.TryApplyProjectSecuritySettings( _remoteCollections.Value );
		if ( _remoteWorkflows.HasValue )
			SyncToolConfig.TryApplyProjectSecuritySettings( _remoteWorkflows.Value );

		if ( !_remoteEndpoints.HasValue )
		{
			_status = "Failed to fetch endpoints from server — check Base URL and credentials";
			_hasCheckedRemote = true;
			return;
		}

		if ( !_remoteCollections.HasValue )
		{
			_status = "Failed to fetch collections from server — check Base URL and credentials";
			_hasCheckedRemote = true;
			return;
		}

		// ── Process endpoints ──
		var remoteSlugs = new HashSet<string>();
		{
			var data = _remoteEndpoints.Value;
			if ( data.TryGetProperty( "data", out var d ) ) data = d;
			if ( data.ValueKind == JsonValueKind.Array )
			{
				foreach ( var ep in data.EnumerateArray() )
				{
					if ( SyncToolConfig.IsEndpointDeprecated( ep ) )
						continue;

					var slug = GetRemoteEndpointSlug( ep );
					if ( string.IsNullOrEmpty( slug ) ) continue;
					remoteSlugs.Add( slug );
					var id = $"ep_{slug}";

					var remoteLocal = SyncToolTransforms.ServerEndpointToLocal( ep );
					var remoteJson = JsonSerializer.Serialize( remoteLocal, new JsonSerializerOptions { WriteIndented = true } );

					if ( !localEpBySlug.TryGetValue( slug, out var localEp ) )
					{
						if ( localDeprecatedSlugs.Contains( slug ) )
						{
							// Local file exists but is flagged `_deprecated` — sync intentionally
							// ignores it. Treat as in-sync from a diff perspective so the row
							// isn't flagged "Remote only" every check; the main endpoints list
							// still labels it "deprecated, ignored by sync".
							SetItemState( id, remoteDiffers: false, status: SyncStatus.InSync,
								diffSummary: "Deprecated locally — ignored by sync",
								localJson: "", remoteJson: PrettyJson( remoteJson ) );
						}
						else
						{
							SetItemState( id, remoteDiffers: true, status: SyncStatus.RemoteOnly,
								diffSummary: "Remote only — not in local files",
								localJson: "", remoteJson: PrettyJson( remoteJson ) );
							diffs++;
						}
					}
					else
					{
						// Transform local same as remote to strip server-managed fields
						var localTransformed = SyncToolTransforms.ServerEndpointToLocal( localEp );
						var localJson = JsonSerializer.Serialize( localTransformed, new JsonSerializerOptions { WriteIndented = true } );
						var differs = NormalizeJson( remoteJson ) != NormalizeJson( localJson );

						if ( differs )
						{
							var localPretty = PrettyJson( localJson );
							var remotePretty = PrettyJson( remoteJson );
							var classification = ClassifyRemoteDifference( id, localPretty, remotePretty );

							SetItemState( id, remoteDiffers: true, status: classification.Status,
								diffSummary: classification.Summary,
								localJson: localPretty, remoteJson: remotePretty );

							if ( classification.Status == SyncStatus.MergeAvailable ) remoteSemanticsCount++;
							else diffs++;
						}
						else
						{
							SetItemState( id, remoteDiffers: false, status: SyncStatus.InSync,
								localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
						}
					}
				}
			}
		}

		foreach ( var slug in localEpBySlug.Keys )
		{
			if ( !remoteSlugs.Contains( slug ) )
			{
				var id = $"ep_{slug}";
				var localJson = JsonSerializer.Serialize( localEpBySlug[slug], new JsonSerializerOptions { WriteIndented = true } );
				SetItemState( id, remoteDiffers: false, status: SyncStatus.LocalOnly,
					diffSummary: "Local only — not pushed to server",
					localJson: PrettyJson( localJson ), remoteJson: "" );
				localOnlyCount++;
			}
		}

		// ── Check collections ──
		var remoteColNames = new HashSet<string>();
		{
			var remoteCollections = SyncToolTransforms.ServerToCollections( _remoteCollections.Value );
			foreach ( var (colName, remoteLocal) in remoteCollections )
			{
				remoteColNames.Add( colName );
				var id = $"col_{colName}";
				var remoteJson = JsonSerializer.Serialize( remoteLocal, new JsonSerializerOptions { WriteIndented = true } );

				if ( !localColByName.TryGetValue( colName, out var localJson ) )
				{
					SetItemState( id, remoteDiffers: true, status: SyncStatus.RemoteOnly,
						diffSummary: "Remote only — no local file",
						localJson: "", remoteJson: PrettyJson( remoteJson ) );
					diffs++;
				}
				else
				{
					var differs = NormalizeJson( remoteJson ) != NormalizeJson( localJson );

					if ( differs )
					{
						var localPretty = PrettyJson( localJson );
						var remotePretty = PrettyJson( remoteJson );
						var classification = ClassifyRemoteDifference( id, localPretty, remotePretty );

						SetItemState( id, remoteDiffers: true, status: classification.Status,
							diffSummary: classification.Summary,
							localJson: localPretty, remoteJson: remotePretty );

						if ( classification.Status == SyncStatus.MergeAvailable ) remoteSemanticsCount++;
						else diffs++;
					}
					else
					{
						SetItemState( id, remoteDiffers: false, status: SyncStatus.InSync,
							localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
					}
				}
			}
		}

		foreach ( var colName in localColByName.Keys )
		{
			if ( !remoteColNames.Contains( colName ) )
			{
				var id = $"col_{colName}";
				SetItemState( id, remoteDiffers: false, status: SyncStatus.LocalOnly,
					diffSummary: "Local only — not pushed to server",
					localJson: PrettyJson( localColByName[colName] ), remoteJson: "" );
				localOnlyCount++;
			}
		}

		// ── Check workflows ──
		var remoteWfIds = new HashSet<string>();
		if ( _remoteWorkflows.HasValue )
		{
			var workflows = SyncToolTransforms.ServerToWorkflows( _remoteWorkflows.Value );
			foreach ( var (wfId, remoteLocal) in workflows )
			{
				remoteWfIds.Add( wfId );
				var id = $"wf_{wfId}";
				var remoteJson = JsonSerializer.Serialize( remoteLocal, new JsonSerializerOptions { WriteIndented = true } );

				if ( !localWfById.TryGetValue( wfId, out var localWf ) )
				{
					SetItemState( id, remoteDiffers: true, status: SyncStatus.RemoteOnly,
						diffSummary: "Remote only — no local file",
						localJson: "", remoteJson: PrettyJson( remoteJson ) );
					diffs++;
				}
				else
				{
					// Transform local same as remote to strip server-managed fields
					var localTransformed = SyncToolTransforms.ServerWorkflowToLocal( localWf );
					var localJson = JsonSerializer.Serialize( localTransformed, new JsonSerializerOptions { WriteIndented = true } );
					var normRemote = NormalizeJson( remoteJson );
					var normLocal = NormalizeJson( localJson );
					var differs = normRemote != normLocal;

					Log.Info( $"[SyncTool] ── Workflow {wfId} ──" );
					Log.Info( $"[SyncTool] LOCAL:  {localJson}" );
					Log.Info( $"[SyncTool] REMOTE: {remoteJson}" );
					Log.Info( $"[SyncTool] NORM LOCAL:  {normLocal}" );
					Log.Info( $"[SyncTool] NORM REMOTE: {normRemote}" );
					Log.Info( $"[SyncTool] DIFFERS: {differs}" );

					if ( differs )
					{
						var localPretty = PrettyJson( localJson );
						var remotePretty = PrettyJson( remoteJson );
						var classification = ClassifyRemoteDifference( id, localPretty, remotePretty );

						SetItemState( id, remoteDiffers: true, status: classification.Status,
							diffSummary: classification.Summary,
							localJson: localPretty, remoteJson: remotePretty );

						if ( classification.Status == SyncStatus.MergeAvailable ) remoteSemanticsCount++;
						else diffs++;
					}
					else
					{
						SetItemState( id, remoteDiffers: false, status: SyncStatus.InSync,
							localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
					}
				}
			}
		}

		foreach ( var wfId in localWfById.Keys )
		{
			if ( !remoteWfIds.Contains( wfId ) )
			{
				var id = $"wf_{wfId}";
				var localJson = JsonSerializer.Serialize( localWfById[wfId], new JsonSerializerOptions { WriteIndented = true } );
				SetItemState( id, remoteDiffers: false, status: SyncStatus.LocalOnly,
					diffSummary: "Local only — not pushed to server",
					localJson: PrettyJson( localJson ), remoteJson: "" );
				localOnlyCount++;
			}
		}

			_hasCheckedRemote = true;
		var parts = new List<string>();
		if ( diffs > 0 ) parts.Add( $"{diffs} remote diff(s)" );
		if ( remoteSemanticsCount > 0 ) parts.Add( $"{remoteSemanticsCount} remote semantics" );
		if ( localOnlyCount > 0 ) parts.Add( $"{localOnlyCount} local only" );
		_status = parts.Count > 0
			? string.Join( ", ", parts )
			: "Everything is in sync";
	}

	/// <summary>
	/// Normalize JSON for comparison — sorts all object keys recursively so key order doesn't cause false diffs.
	/// </summary>
	private static string NormalizeJson( string json )
	{
		try
		{
			var el = JsonSerializer.Deserialize<JsonElement>( json );
			var sorted = SortJsonElement( el );
			return JsonSerializer.Serialize( sorted, new JsonSerializerOptions { WriteIndented = false } );
		}
		catch { return json.Trim(); }
	}

	private static object SortJsonElement( JsonElement el )
	{
		switch ( el.ValueKind )
		{
			case JsonValueKind.Object:
				var sorted = new SortedDictionary<string, object>();
				foreach ( var prop in el.EnumerateObject() )
				{
					if ( IsIgnoredComparisonField( prop.Name ) )
						continue;
					sorted[prop.Name] = SortJsonElement( prop.Value );
				}
				return sorted.Count == 0 ? null : sorted;
			case JsonValueKind.Array:
				var arr = new List<object>();
				foreach ( var item in el.EnumerateArray() )
					arr.Add( SortJsonElement( item ) );
				return arr;
			case JsonValueKind.String:
				return el.GetString();
			case JsonValueKind.Number:
				return el.TryGetInt64( out var l ) ? (object)l : el.GetDouble();
			case JsonValueKind.True:
				return true;
			case JsonValueKind.False:
				return false;
			default:
				return null;
		}
	}

	private static string PrettyJson( string json )
	{
		if ( string.IsNullOrEmpty( json ) ) return "";
		try
		{
			var el = JsonSerializer.Deserialize<JsonElement>( json );
			return JsonSerializer.Serialize( el, new JsonSerializerOptions { WriteIndented = true } );
		}
		catch { return json; }
	}

	private (SyncStatus Status, string Summary, JsonDiffUtilities.ComparisonResult Analysis) ClassifyRemoteDifference(
		string id, string localJson, string remoteJson )
	{
		var analysis = JsonDiffUtilities.Analyze( localJson, remoteJson );
		if ( analysis.IsRemoteAdditiveOnly )
			return (SyncStatus.MergeAvailable, BuildRemoteSemanticsSummary( analysis ), analysis);

		var lineSummary = JsonDiffUtilities.SummarizeLineDifferences( analysis.LineCounts );
		var detail = BuildResourceDiffDetail( id, localJson, remoteJson );
		return (SyncStatus.Differs, CombineDiffSummary( lineSummary, detail ), analysis);
	}

	private string BuildResourceDiffDetail( string id, string localJson, string remoteJson )
	{
		if ( id.StartsWith( "ep_" ) )
			return DiffEndpoint( localJson, remoteJson, id[3..] );
		if ( id.StartsWith( "col_" ) )
			return DiffCollectionSchema( localJson, remoteJson );

		return null;
	}

	private static string BuildRemoteSemanticsSummary( JsonDiffUtilities.ComparisonResult analysis )
	{
		var lineSummary = analysis.LineCounts.HasChanges
			? JsonDiffUtilities.SummarizeLineDifferences( analysis.LineCounts )
			: $"Remote added {analysis.Added.Count} field{Plural( analysis.Added.Count )}";
		var preview = JsonDiffUtilities.PreviewPaths( analysis.Added );

		return string.IsNullOrWhiteSpace( preview )
			? $"{lineSummary} | Pull Remote Semantics"
			: $"{lineSummary} | Pull Remote Semantics: {preview}";
	}

	private static string BuildRemoteSemanticsLogDetail( JsonDiffUtilities.ComparisonResult analysis )
	{
		var preview = JsonDiffUtilities.PreviewPaths( analysis.Added );
		var count = analysis.Added.Count;
		var countText = $"{count} remote field{Plural( count )}";

		return string.IsNullOrWhiteSpace( preview )
			? $"Remote semantics available - {countText}"
			: $"Remote semantics available - {countText}: {preview}";
	}

	private static string CombineDiffSummary( string lineSummary, string detail )
	{
		if ( string.IsNullOrWhiteSpace( detail ) )
			return lineSummary;

		if ( detail.Equals( "Field ordering differs (content identical)", StringComparison.OrdinalIgnoreCase ) )
			return lineSummary;

		if ( detail.StartsWith( "Content differs", StringComparison.OrdinalIgnoreCase ) )
			return lineSummary;

		if ( string.IsNullOrWhiteSpace( lineSummary ) )
			return detail;

		return $"{lineSummary} | {detail}";
	}

	private static string Plural( int count )
	{
		return count == 1 ? "" : "s";
	}

	private static string DescribeSyncItem( string id )
	{
		if ( id.StartsWith( "ep_" ) ) return $"{id[3..]}.endpoint.yml (endpoint)";
		if ( id.StartsWith( "col_" ) ) return $"{id[4..]}.collection.yml (collection)";
		if ( id.StartsWith( "wf_" ) ) return $"{id[3..]}.workflow.yml (workflow)";
		if ( id.StartsWith( "test_" ) ) return $"{id[5..]}.json (test)";
		return id;
	}

	// ──────────────────────────────────────────────────────
	//  Push (with remote-newer warning)
	// ──────────────────────────────────────────────────────

	private async Task PushAll()
	{
		if ( _busy || !SyncToolConfig.IsValid ) return;
		_scrollY = 0;
		_busy = true;
		_busyItem = "push_all";
		_status = "Pushing all resources...";
		_syncLog.Clear();
		foreach ( var k in _items.Keys.ToList() ) SetItemState( k, result: null );
		Update();

		var activeEndpointFiles = GetActiveEndpointFiles();

		// ── Load local files for pre-push comparison ──
		// Apply same transform as remote so id/createdAt are stripped from both sides
		var localEndpoints = SyncToolConfig.LoadEndpoints();
		var localEpBySlug = new Dictionary<string, string>();
		foreach ( var ep in localEndpoints )
		{
			var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
			if ( !string.IsNullOrEmpty( slug ) )
			{
				var localTransformed = SyncToolTransforms.ServerEndpointToLocal( ep );
				localEpBySlug[slug] = NormalizeJson( JsonSerializer.Serialize( localTransformed ) );
			}
		}

		// ── Push all resources in parallel ──
		_busyItem = "push_all";
		_status = "Pushing all resources...";
		Update();

		var pushEpTask = activeEndpointFiles.Length > 0 ? DoPushAllEndpoints() : Task.FromResult( false );
		var pushColTask = _collectionFiles.Length > 0 ? DoPushCollections() : Task.FromResult( false );
		var pushWfTask = _workflowFiles.Length > 0 ? DoPushAllWorkflows() : Task.FromResult( false );

		await Task.WhenAll( pushEpTask, pushColTask, pushWfTask );

		var epOk = activeEndpointFiles.Length > 0 ? await pushEpTask : false;
		var colOk = _collectionFiles.Length > 0 ? await pushColTask : false;
		var wfOk = _workflowFiles.Length > 0 ? await pushWfTask : false;

		// ── Log push results ──
		if ( activeEndpointFiles.Length > 0 )
		{
			foreach ( var f in activeEndpointFiles )
			{
				var slug = ResourceIdFromFile( f, "endpoint" );
				var failDetail = GetPushFailDetail( "endpoints", slug );
				var detail = epOk ? "Pushed" : failDetail;
				SetItemState( $"ep_{slug}", result: epOk ? "OK" : "FAIL",
					remoteDiffers: false, diffSummary: "", status: epOk ? SyncStatus.InSync : null );
				_syncLog.Add( new SyncLogEntry { Name = $"{slug}.endpoint.yml", Type = "Endpoint", Ok = epOk, Detail = detail } );
			}
		}

		if ( _collectionFiles.Length > 0 )
		{
			foreach ( var f in _collectionFiles )
			{
				var name = ResourceIdFromFile( f, "collection" );
				var failDetail = GetPushFailDetail( "collections", name );
				var detail = colOk ? "Pushed" : failDetail;
				SetItemState( $"col_{name}", result: colOk ? "OK" : "FAIL",
					remoteDiffers: false, diffSummary: "", status: colOk ? SyncStatus.InSync : null );
				_syncLog.Add( new SyncLogEntry { Name = $"{name}.collection.yml", Type = "Collection", Ok = colOk, Detail = detail } );
			}
		}

		if ( _workflowFiles.Length > 0 )
		{
			foreach ( var f in _workflowFiles )
			{
				var name = ResourceIdFromFile( f, "workflow" );
				var failDetail = GetPushFailDetail( "workflows", name );
				var detail = wfOk ? "Pushed" : failDetail;
				SetItemState( $"wf_{name}", result: wfOk ? "OK" : "FAIL",
					remoteDiffers: false, diffSummary: "", status: wfOk ? SyncStatus.InSync : null );
				_syncLog.Add( new SyncLogEntry { Name = $"{name}.workflow.yml", Type = "Workflow", Ok = wfOk, Detail = detail } );
			}
		}

		ScrollToBottom();

		// ── Auto-verify: re-fetch remote and compare to local ──
		_busyItem = "verify";
		_status = "Verifying remote matches local...";
		Update();

		await VerifyPushResults( localEpBySlug );

		// ── Auto-generate typed C# files from the pushed schemas ──
		_busyItem = "codegen";
		_status = "Generating typed C# code...";
		Update();

		try
		{
			var filesWritten = CodeGenerator.Generate();
			_syncLog.Add( new SyncLogEntry { Name = "Code Generation", Type = "CodeGen", Ok = true, Detail = $"{filesWritten} files written to Code/Data/NetworkStorage/" } );
		}
		catch ( Exception ex )
		{
			_syncLog.Add( new SyncLogEntry { Name = "Code Generation", Type = "CodeGen", Ok = false, Detail = ex.Message } );
		}

		// Invalidate cached remote data — next check will fetch fresh
		ClearAllRemoteDiffs();
		_remoteEndpoints = null;
		_remoteCollections = null;
		_remoteWorkflows = null;
		_hasCheckedRemote = false;

		var okCount = _syncLog.Count( e => e.Ok );
		var failCount = _syncLog.Count( e => !e.Ok );
		var mismatchCount = _syncLog.Count( e => e.Detail != null && e.Detail.Contains( "Mismatch" ) );
		var verifiedCount = _syncLog.Count( e => e.Detail != null && e.Detail.Contains( "Verified" ) );
		var mergeCount = _syncLog.Count( e => e.Detail != null && e.Detail.Contains( "Remote semantics available" ) );

		if ( mergeCount > 0 && mismatchCount == 0 && failCount == 0 )
			_status = $"Pushed OK - {mergeCount} item(s) can pull remote semantics";
		else if ( mismatchCount > 0 )
			_status = $"Done: {okCount} pushed, {mismatchCount} mismatch(es) — check diffs";
		else if ( failCount > 0 )
			_status = $"Done: {okCount - failCount} OK, {failCount} failed";
		else
			_status = $"All synced and verified ({verifiedCount} resources)";

		_busy = false;
		_busyItem = null;
		ScrollToBottom();
	}

	/// <summary>
	/// After pushing, re-fetch remote data and compare each resource to local.
	/// Updates sync log entries with "Verified ✓" or "Mismatch — see diff".
	/// </summary>
	private async Task VerifyPushResults( Dictionary<string, string> localEpBySlug )
	{
		try
		{
			// Re-load local files fresh, stripping server-managed fields for fair comparison
			var localCollections = SyncToolConfig.LoadCollections();
			var localColByName = new Dictionary<string, string>();
			foreach ( var (name, data) in localCollections )
			{
				var stripped = SyncToolTransforms.StripServerManagedFields( data );
				localColByName[name] = NormalizeJson( JsonSerializer.Serialize( stripped, new JsonSerializerOptions { WriteIndented = true } ) );
			}

			var localWorkflows = SyncToolConfig.LoadWorkflows();
			var localWfById = new Dictionary<string, string>();
			foreach ( var wf in localWorkflows )
			{
				var wfId = wf.TryGetProperty( "id", out var id ) ? id.GetString() : "";
				if ( !string.IsNullOrEmpty( wfId ) )
				{
					var localTransformed = SyncToolTransforms.ServerWorkflowToLocal( wf );
					localWfById[wfId] = NormalizeJson( JsonSerializer.Serialize( localTransformed ) );
				}
			}

			// Fetch all 3 resource types in parallel
			var remoteEpsTask = SyncToolApi.GetEndpoints();
			var remoteColsTask = SyncToolApi.GetCollections();
			var remoteWfsTask = SyncToolApi.GetWorkflows();

			await Task.WhenAll( remoteEpsTask, remoteColsTask, remoteWfsTask );

			var remoteEps = await remoteEpsTask;
			var remoteCols = await remoteColsTask;
			var remoteWfs = await remoteWfsTask;

			// Process endpoint results
			if ( remoteEps.HasValue )
			{
				var data = remoteEps.Value;
				if ( data.TryGetProperty( "data", out var d ) ) data = d;
				if ( data.ValueKind == JsonValueKind.Array )
				{
					foreach ( var ep in data.EnumerateArray() )
					{
						var slug = GetRemoteEndpointSlug( ep );
						if ( string.IsNullOrEmpty( slug ) ) continue;

						var remoteLocal = SyncToolTransforms.ServerEndpointToLocal( ep );
						var remoteNorm = NormalizeJson( JsonSerializer.Serialize( remoteLocal ) );

						var logIdx = _syncLog.FindIndex( e => e.Name == $"{slug}.endpoint.yml" && e.Type == "Endpoint" );
						if ( logIdx < 0 ) continue;

						var entry = _syncLog[logIdx];
						if ( !entry.Ok ) continue; // Skip failed pushes

						if ( localEpBySlug.TryGetValue( slug, out var localNorm ) && localNorm == remoteNorm )
						{
							entry.Detail = "Verified ✓";
						}
						else
						{
							var eid = $"ep_{slug}";
							var localPretty = localEpBySlug.TryGetValue( slug, out var lj ) ? PrettyJson( lj ) : "{}";
							var remotePretty = PrettyJson( JsonSerializer.Serialize( remoteLocal ) );
							var classification = ClassifyRemoteDifference( eid, localPretty, remotePretty );

							entry.Detail = classification.Status == SyncStatus.MergeAvailable
								? BuildRemoteSemanticsLogDetail( classification.Analysis )
								: $"Mismatch - {classification.Summary}";
							entry.Ok = classification.Status == SyncStatus.MergeAvailable;
							SetItemState( eid, remoteDiffers: true, status: classification.Status,
								diffSummary: classification.Summary,
								localJson: localPretty, remoteJson: remotePretty );
						}
						_syncLog[logIdx] = entry;
					}
				}
			}

			// Process collection results
			if ( remoteCols.HasValue )
			{
				var collections = SyncToolTransforms.ServerToCollections( remoteCols.Value );
				foreach ( var (colName, remoteLocal) in collections )
				{
					var remoteNorm = NormalizeJson( JsonSerializer.Serialize( remoteLocal ) );
					var logIdx = _syncLog.FindIndex( e => e.Name == $"{colName}.collection.yml" && e.Type == "Collection" );
					if ( logIdx < 0 ) continue;

					var entry = _syncLog[logIdx];
					if ( !entry.Ok ) continue;

					if ( localColByName.TryGetValue( colName, out var localNorm ) && localNorm == remoteNorm )
					{
						entry.Detail = "Verified ✓";
					}
					else
					{
						var cid = $"col_{colName}";
						var localPretty = PrettyJson( localColByName.GetValueOrDefault( colName, "{}" ) );
						var remotePretty = PrettyJson( JsonSerializer.Serialize( remoteLocal ) );
						var classification = ClassifyRemoteDifference( cid, localPretty, remotePretty );

						entry.Detail = classification.Status == SyncStatus.MergeAvailable
							? BuildRemoteSemanticsLogDetail( classification.Analysis )
							: $"Mismatch - {classification.Summary}";
						entry.Ok = classification.Status == SyncStatus.MergeAvailable;
						SetItemState( cid, remoteDiffers: true, status: classification.Status,
							diffSummary: classification.Summary,
							localJson: localPretty, remoteJson: remotePretty );
					}
					_syncLog[logIdx] = entry;
				}
			}

			// Process workflow results
			if ( remoteWfs.HasValue )
			{
				var workflows = SyncToolTransforms.ServerToWorkflows( remoteWfs.Value );
				foreach ( var (wfId, remoteLocal) in workflows )
				{
					var remoteNorm = NormalizeJson( JsonSerializer.Serialize( remoteLocal ) );
					var logIdx = _syncLog.FindIndex( e => e.Name == $"{wfId}.workflow.yml" && e.Type == "Workflow" );
					if ( logIdx < 0 ) continue;

					var entry = _syncLog[logIdx];
					if ( !entry.Ok ) continue;

					if ( localWfById.TryGetValue( wfId, out var localNorm ) && localNorm == remoteNorm )
					{
						entry.Detail = "Verified ✓";
					}
					else
					{
						var wid = $"wf_{wfId}";
						var localPretty = PrettyJson( localWfById.GetValueOrDefault( wfId, "{}" ) );
						var remotePretty = PrettyJson( JsonSerializer.Serialize( remoteLocal ) );
						var classification = ClassifyRemoteDifference( wid, localPretty, remotePretty );

						entry.Detail = classification.Status == SyncStatus.MergeAvailable
							? BuildRemoteSemanticsLogDetail( classification.Analysis )
							: $"Mismatch - {classification.Summary}";
						entry.Ok = classification.Status == SyncStatus.MergeAvailable;
						SetItemState( wid, remoteDiffers: true, status: classification.Status,
							diffSummary: classification.Summary,
							localJson: localPretty, remoteJson: remotePretty );
					}
					_syncLog[logIdx] = entry;
				}
			}

			Update();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Post-push verification failed: {ex.Message}" );
			_status = $"Push done, verification failed: {ex.Message}";
		}
	}

	private void PushItem( string id )
	{
		if ( _busy || !SyncToolConfig.IsValid ) return;

		_items.TryGetValue( id, out var state );
		var label = id.StartsWith( "ep_" ) ? $"endpoint '{id[3..]}'"
			: id.StartsWith( "col_" ) ? $"collection '{id[4..]}'"
			: id.StartsWith( "wf_" ) ? $"workflow '{id[3..]}'"
			: id.StartsWith( "test_" ) ? $"test '{id[5..]}'"
			: "resource";

		if ( state.RemoteDiffers )
		{
			ConfirmDialog.Show(
				"Overwrite Remote?",
				$"The stored {label} is newer on your project dashboard. Pushing will overwrite the remote version with your local file.",
				() => _ = DoPushItem( id ),
				detail: state.DiffSummary
			);
		}
		else
		{
			_ = DoPushItem( id );
		}
	}

	private async Task DoPushItem( string id )
	{
		_busy = true;
		_busyItem = $"push_{id}";
		_status = $"Pushing {id}...";
		Update();

		try
		{
			bool ok;
			string itemName;
			string itemType;

			if ( id.StartsWith( "ep_" ) )
			{
				var slug = id[3..];
				ok = await DoPushSingleEndpointMerged( slug );
				itemName = $"{slug}.endpoint.yml";
				itemType = "Endpoint";
			}
			else if ( id.StartsWith( "col_" ) )
			{
				ok = await DoPushCollections();
				itemName = $"{id[4..]}.collection.yml";
				itemType = "Collection";
			}
			else if ( id.StartsWith( "wf_" ) )
			{
				ok = await DoPushAllWorkflows();
				itemName = $"{id[3..]}.workflow.yml";
				itemType = "Workflow";
			}
			else
			{
				ok = false;
				itemName = id;
				itemType = "Unknown";
			}

			// Update this item's state
			SetItemState( id, result: ok ? "OK" : "FAIL", remoteDiffers: false, diffSummary: "",
				status: ok ? SyncStatus.InSync : null );

			// Add to sync log
			_syncLog.Add( new SyncLogEntry { Name = itemName, Type = itemType, Ok = ok, Detail = ok ? "Pushed" : GetPushFailDetailForItem( id ) } );

			// Regenerate typed C# files so Code/Data/NetworkStorage/ stays in sync
			if ( ok )
			{
				try
				{
					var filesWritten = CodeGenerator.Generate();
					_syncLog.Add( new SyncLogEntry { Name = "Code Generation", Type = "CodeGen", Ok = true, Detail = $"{filesWritten} files written to Code/Data/NetworkStorage/" } );
				}
				catch ( Exception ex )
				{
					_syncLog.Add( new SyncLogEntry { Name = "Code Generation", Type = "CodeGen", Ok = false, Detail = ex.Message } );
				}
			}

			// Invalidate cached remote data so next Check for Updates is fresh,
			// but preserve other items' diff state so they don't disappear
			_remoteEndpoints = null;
			_remoteCollections = null;
			_remoteWorkflows = null;

			_status = ok ? $"Pushed {id}" : $"Push failed for {id}";
			ScrollToBottom();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Push {id} failed: {ex.Message}" );
			SetItemState( id, result: "FAIL" );
			_status = $"Push failed for {id}: {ex.Message}";
		}
		finally
		{
			_busy = false;
			_busyItem = null;
			Update();
		}
	}

	// ──────────────────────────────────────────────────────
	//  Pull (per-item)
	// ──────────────────────────────────────────────────────

	private void PullItem( string id )
	{
		if ( _busy || !SyncToolConfig.IsValid ) return;

		var label = id.StartsWith( "ep_" ) ? $"endpoint '{id[3..]}'"
			: id.StartsWith( "col_" ) ? $"collection '{id[4..]}'"
			: id.StartsWith( "wf_" ) ? $"workflow '{id[3..]}'"
			: id.StartsWith( "test_" ) ? $"test '{id[5..]}'"
			: "resource";
		_items.TryGetValue( id, out var pullState );

		ConfirmDialog.Show(
			"Pull from Web",
			$"This will replace your local {label} in Editor/{SyncToolConfig.DataFolder}/ with the version from the project dashboard.",
			() => _ = DoPullItem( id ),
			detail: pullState.DiffSummary
		);
	}

	private async Task DoPullItem( string id )
	{
		_busy = true;
		_busyItem = $"pull_{id}";
		_status = $"Pulling {id}...";
		Update();

		try
		{
			bool ok;
			if ( id.StartsWith( "ep_" ) )
			{
				var slug = id[3..];
				ok = await DoPullSingleEndpoint( slug );
			}
			else if ( id.StartsWith( "col_" ) )
			{
				var colName = id[4..];
				ok = await DoPullSingleCollection( colName );
			}
			else if ( id.StartsWith( "wf_" ) )
			{
				var wfId = id[3..];
				ok = await DoPullSingleWorkflow( wfId );
			}
			else
			{
				ok = false;
			}

			if ( ok )
			{
				SetItemState( id, result: "OK", remoteDiffers: false, diffSummary: "",
					status: SyncStatus.InSync );
				RefreshFileList();

				// Regenerate typed C# files so Code/Data/NetworkStorage/ reflects the pulled data
				try
				{
					CodeGenerator.Generate();
				}
				catch ( Exception ex )
				{
					Log.Warning( $"[SyncTool] Code generation after pull failed: {ex.Message}" );
				}

				// Invalidate cached remote data so next Check is fresh,
				// but keep _hasCheckedRemote and other items' state so they don't disappear
				_remoteEndpoints = null;
				_remoteCollections = null;
				_remoteWorkflows = null;
			}
			else
			{
				SetItemState( id, result: "FAIL" );
			}

			_status = ok ? $"Pulled {id}" : $"Pull failed for {id}";
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Pull {id} failed: {ex.Message}" );
			SetItemState( id, result: "FAIL" );
			_status = $"Pull failed for {id}: {ex.Message}";
		}
		finally
		{
			_busy = false;
			_busyItem = null;
			Update();
		}
	}

	/// <summary>
	/// Get a human-readable failure message after a failed push, using SyncToolApi error state.
	/// </summary>
	private static string GetPushFailDetail( string resource, string resourceId = null )
	{
		if ( SyncToolApi.LastErrorCode == "KEY_UPGRADE_REQUIRED" )
			return "Key uses old format — regenerate at sbox.cool";
		if ( SyncToolApi.LastErrorCode == "FORBIDDEN" )
			return $"No write permission for {resource}";
		if ( !string.IsNullOrWhiteSpace( resourceId ) )
		{
			var resourceMessage = SyncToolApi.GetLastResourceErrorMessage( resource, resourceId );
			if ( !string.IsNullOrEmpty( resourceMessage ) )
				return resourceMessage;
		}
		var pathMessage = SyncToolApi.GetLastErrorMessage( resource );
		if ( !string.IsNullOrEmpty( pathMessage ) )
			return pathMessage;
		if ( !string.IsNullOrEmpty( SyncToolApi.LastErrorMessage ) )
			return SyncToolApi.LastErrorMessage;
		return "Push failed";
	}

	private static string GetPushFailDetailForItem( string id )
	{
		if ( id.StartsWith( "ep_" ) )
			return GetPushFailDetail( "endpoints", id[3..] );
		if ( id.StartsWith( "col_" ) )
			return GetPushFailDetail( "collections", id[4..] );
		if ( id.StartsWith( "wf_" ) )
			return GetPushFailDetail( "workflows", id[3..] );
		return GetPushFailDetail( "resource" );
	}

	// ──────────────────────────────────────────────────────
	//  Push implementation
	// ──────────────────────────────────────────────────────

	private async Task<bool> DoPushAllEndpoints()
	{
		try
		{
			Log.Info( "[SyncTool] Preparing endpoint push..." );
			var localEps = SyncToolConfig.LoadEndpoints();
			Log.Info( $"[SyncTool] Loaded {localEps.Count} local endpoint(s) for push." );
			if ( localEps.Count == 0 )
			{
				SyncToolApi.ReportLocalError( "endpoints", "No readable local endpoint source files were loaded for push." );
				return false;
			}
			var existing = await SyncToolApi.GetEndpoints();
			var serverFmt = SyncToolTransforms.EndpointsToServer( localEps, existing );
			var resp = await SyncToolApi.PushEndpoints( serverFmt );
			return resp.HasValue;
		}
		catch ( Exception ex )
		{
			SyncToolApi.ReportLocalError( "endpoints", $"Local endpoint push preparation failed: {ex.Message}", ex );
			return false;
		}
	}

	private static bool IsIgnoredComparisonField( string name )
	{
		return name is "authoringMode"
			or "sourceText"
			or "sourceFormat"
			or "sourceVersion"
			or "sourcePath"
			or "compilerFingerprint"
			or "compilerFingerprintHash"
			or "sourceHash"
			or "dependencyHash"
			or "canonicalHash"
			or "executionPlanHash"
			or "dependencies"
			or "executionPlan"
			or "diagnostics"
			or "canonicalDefinition";
	}

	/// <summary>
	/// Push a single endpoint by merging it into the remote list.
	/// GETs remote endpoints, replaces the matching slug, PUTs the merged list.
	/// </summary>
	private async Task<bool> DoPushSingleEndpointMerged( string slug )
	{
		try
		{
			Log.Info( $"[SyncTool] Preparing single endpoint push for {slug}..." );
			var localFile = _endpointFiles.FirstOrDefault( f => ResourceIdFromFile( f, "endpoint" ) == slug );
			if ( localFile == null )
			{
				SyncToolApi.ReportLocalError( "endpoints", $"Local endpoint file for {slug} was not found." );
				return false;
			}

			if ( !TryReadLocalResourceFile( localFile, "endpoint", out var localEp ) )
			{
				SyncToolApi.ReportLocalError( "endpoints", $"Local endpoint file for {slug} could not be parsed." );
				return false;
			}
			if ( SyncToolConfig.IsEndpointDeprecated( localEp ) ) return false;

			var remoteResp = await SyncToolApi.GetEndpoints();
			if ( !remoteResp.HasValue ) return false;

			var data = remoteResp.Value;
			if ( data.TryGetProperty( "data", out var d ) ) data = d;
			if ( data.ValueKind != JsonValueKind.Array )
			{
				SyncToolApi.ReportLocalError( "endpoints", "Remote endpoints payload was not an array." );
				return false;
			}

			var merged = new List<object>();
			var replaced = false;
			foreach ( var ep in data.EnumerateArray() )
			{
				var epSlug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
				if ( epSlug == slug )
				{
					var localDict = SyncToolTransforms.ServerEndpointToLocal( localEp );
					if ( ep.TryGetProperty( "id", out var idEl ) )
						localDict["id"] = idEl.GetString();
					if ( ep.TryGetProperty( "createdAt", out var caEl ) )
						localDict["createdAt"] = caEl.GetString();
					merged.Add( localDict );
					replaced = true;
				}
				else
				{
					merged.Add( ep );
				}
			}

			if ( !replaced )
			{
				var localDict = SyncToolTransforms.ServerEndpointToLocal( localEp );
				localDict["id"] = Guid.NewGuid().ToString( "N" )[..16];
				merged.Add( localDict );
			}

			var mergedJson = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( merged ) );
			var resp = await SyncToolApi.PushEndpoints( mergedJson );
			return resp.HasValue;
		}
		catch ( Exception ex )
		{
			SyncToolApi.ReportLocalError( "endpoints", $"Single endpoint push preparation failed for {slug}: {ex.Message}", ex );
			return false;
		}
	}

	private async Task<bool> DoPushCollections()
	{
		try
		{
			Log.Info( "[SyncTool] Preparing collection push..." );
			var collections = SyncToolConfig.LoadCollections();
			Log.Info( $"[SyncTool] Loaded {collections.Count} local collection(s) for push." );
			if ( collections.Count == 0 )
			{
				SyncToolApi.ReportLocalError( "collections", "No readable local collection source files were loaded for push." );
				return false;
			}
			var serverFmt = SyncToolTransforms.CollectionsToServer( collections.Select( c => c.Data ).ToList() );
			var resp = await SyncToolApi.PushCollections( serverFmt );
			return resp.HasValue;
		}
		catch ( Exception ex )
		{
			SyncToolApi.ReportLocalError( "collections", $"Local collection push preparation failed: {ex.Message}", ex );
			return false;
		}
	}

	private async Task<bool> DoPushAllWorkflows()
	{
		try
		{
			Log.Info( "[SyncTool] Preparing workflow push..." );
			var localWfs = SyncToolConfig.LoadWorkflows();
			Log.Info( $"[SyncTool] Loaded {localWfs.Count} local workflow(s) for push." );
			if ( localWfs.Count == 0 )
			{
				SyncToolApi.ReportLocalError( "workflows", "No readable local workflow source files were loaded for push." );
				return false;
			}
			var existing = await SyncToolApi.GetWorkflows();
			var serverFmt = SyncToolTransforms.WorkflowsToServer( localWfs, existing );
			var resp = await SyncToolApi.PushWorkflows( serverFmt );
			return resp.HasValue;
		}
		catch ( Exception ex )
		{
			SyncToolApi.ReportLocalError( "workflows", $"Local workflow push preparation failed: {ex.Message}", ex );
			return false;
		}
	}

	// ──────────────────────────────────────────────────────
	//  Pull implementation
	// ──────────────────────────────────────────────────────

	private async Task<bool> DoPullSingleEndpoint( string slug )
	{
		// Use cached remote data if available, otherwise fetch
		var resp = _remoteEndpoints ?? await SyncToolApi.GetEndpoints();
		if ( !resp.HasValue ) return false;

		try
		{
			var data = resp.Value;
			if ( data.TryGetProperty( "data", out var d ) ) data = d;
			if ( data.ValueKind != JsonValueKind.Array ) return false;

			foreach ( var ep in data.EnumerateArray() )
			{
				var epSlug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
				if ( epSlug != slug ) continue;
				if ( SyncToolConfig.IsEndpointDeprecated( ep ) ) return false;

				return SyncToolPullWriter.SaveEndpoint( slug, ep );
			}
			return false;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Pull endpoint {slug} failed: {ex.Message}" );
			return false;
		}
	}

	private async Task<bool> DoPullCollections()
	{
		var resp = _remoteCollections ?? await SyncToolApi.GetCollections();
		if ( !resp.HasValue ) return false;
		try
		{
			return SyncToolPullWriter.SaveCollections( resp.Value ) > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Pull collections failed: {ex.Message}" );
			return false;
		}
	}

	private async Task<bool> DoPullSingleCollection( string colName )
	{
		var resp = _remoteCollections ?? await SyncToolApi.GetCollections();
		if ( !resp.HasValue ) return false;
		try
		{
			var data = resp.Value;
			if ( data.TryGetProperty( "data", out var d ) ) data = d;
			if ( data.ValueKind != JsonValueKind.Array ) return false;

			foreach ( var collection in data.EnumerateArray() )
			{
				var local = SyncToolTransforms.ServerCollectionToLocal( collection );
				var name = local.TryGetValue( "name", out var value ) ? value?.ToString() : null;
				if ( name == colName )
					return SyncToolPullWriter.SaveCollection( colName, collection );
			}

			return false;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Pull collection {colName} failed: {ex.Message}" );
			return false;
		}
	}

	private async Task<bool> DoPullSingleWorkflow( string wfId )
	{
		var resp = _remoteWorkflows ?? await SyncToolApi.GetWorkflows();
		if ( !resp.HasValue ) return false;
		try
		{
			var data = resp.Value;
			if ( data.TryGetProperty( "data", out var d ) ) data = d;
			if ( data.ValueKind != JsonValueKind.Array ) return false;

			foreach ( var workflow in data.EnumerateArray() )
			{
				var local = SyncToolTransforms.ServerWorkflowToLocal( workflow );
				var id = local.TryGetValue( "id", out var value ) ? value?.ToString() : null;
				if ( id == wfId )
					return SyncToolPullWriter.SaveWorkflow( wfId, workflow );
			}

			return false;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Pull workflow {wfId} failed: {ex.Message}" );
			return false;
		}
	}

	// ──────────────────────────────────────────────────────
	//  Diff breakdown helpers
	// ──────────────────────────────────────────────────────

	/// <summary>
	/// Compare two endpoint JSON files key-by-key.
	/// Categorizes changes as cosmetic (name, description, notes) vs structural (steps, input, response, method).
	/// </summary>
	private string DiffEndpoint( string localJson, string remoteJson, string slug )
	{
		try
		{
			var local = JsonSerializer.Deserialize<JsonElement>( localJson );
			var remote = JsonSerializer.Deserialize<JsonElement>( remoteJson );

			var cosmetic = new List<string>();
			var structural = new List<string>();

			// ── Cosmetic fields (harmless label changes) ──
			var localName = GetStr( local, "name" );
			var remoteName = GetStr( remote, "name" );
			if ( localName != remoteName )
				cosmetic.Add( $"name: \"{remoteName}\" → \"{localName}\"" );

			var localDesc = GetStr( local, "description" );
			var remoteDesc = GetStr( remote, "description" );
			if ( localDesc != remoteDesc )
			{
				var label = string.IsNullOrEmpty( remoteDesc ) ? "description added locally" : "description differs";
				cosmetic.Add( label );
			}

			var localNotes = GetStr( local, "notes" );
			var remoteNotes = GetStr( remote, "notes" );
			if ( localNotes != remoteNotes )
			{
				var label = string.IsNullOrEmpty( remoteNotes ) ? "notes added locally" : "notes differ";
				cosmetic.Add( label );
			}

			var localEnabled = !local.TryGetProperty( "enabled", out var le ) || le.ValueKind != JsonValueKind.False;
			var remoteEnabled = !remote.TryGetProperty( "enabled", out var re ) || re.ValueKind != JsonValueKind.False;
			if ( localEnabled != remoteEnabled )
				cosmetic.Add( $"enabled: {remoteEnabled} → {localEnabled}" );

			// ── Structural fields (logic changes) ──
			var localMethod = GetStr( local, "method" );
			var remoteMethod = GetStr( remote, "method" );
			if ( localMethod != remoteMethod )
				structural.Add( $"method: {remoteMethod} → {localMethod}" );

			var localInput = local.TryGetProperty( "input", out var li ) ? NormalizeJson( li.ToString() ) : "{}";
			var remoteInput = remote.TryGetProperty( "input", out var ri ) ? NormalizeJson( ri.ToString() ) : "{}";
			if ( localInput != remoteInput )
				structural.Add( "input schema" );

			var localSteps = local.TryGetProperty( "steps", out var ls ) ? NormalizeJson( ls.ToString() ) : "[]";
			var remoteSteps = remote.TryGetProperty( "steps", out var rs ) ? NormalizeJson( rs.ToString() ) : "[]";
			if ( localSteps != remoteSteps )
			{
				var lCount = ls.ValueKind == JsonValueKind.Array ? ls.GetArrayLength() : 0;
				var rCount = rs.ValueKind == JsonValueKind.Array ? rs.GetArrayLength() : 0;
				structural.Add( lCount != rCount ? $"steps: {rCount} → {lCount}" : "step logic" );
			}

			var localResp = local.TryGetProperty( "response", out var lresp ) ? NormalizeJson( lresp.ToString() ) : "{}";
			var remoteResp = remote.TryGetProperty( "response", out var rresp ) ? NormalizeJson( rresp.ToString() ) : "{}";
			if ( localResp != remoteResp )
				structural.Add( "response" );

			// ── Build summary ──
			if ( structural.Count == 0 && cosmetic.Count > 0 )
				return $"cosmetic only: {string.Join( ", ", cosmetic )}";
			if ( structural.Count > 0 && cosmetic.Count == 0 )
				return $"logic differs: {string.Join( ", ", structural )}";
			if ( structural.Count > 0 && cosmetic.Count > 0 )
				return $"logic: {string.Join( ", ", structural )} | cosmetic: {string.Join( ", ", cosmetic )}";

			return "Field ordering differs (content identical)";
		}
		catch { return "Content differs (click View Diff)"; }
	}

	private static string GetStr( JsonElement el, string key )
	{
		return el.TryGetProperty( key, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
	}

	/// <summary>
	/// Compare two collection JSON files field-by-field.
	/// Distinguishes schema (structural) from metadata (non-structural) changes.
	/// </summary>
	private string DiffCollectionSchema( string localJson, string remoteJson )
	{
		try
		{
			if ( string.IsNullOrEmpty( localJson ) )
				return "New — no local file exists";

			var local = JsonSerializer.Deserialize<JsonElement>( localJson );
			var remote = JsonSerializer.Deserialize<JsonElement>( remoteJson );

			var schemaChanges = new List<string>();
			var metaChanges = new List<string>();

			// Compare schema (structural)
			var localSchema = local.TryGetProperty( "schema", out var ls ) ? NormalizeJson( ls.ToString() ) : "{}";
			var remoteSchema = remote.TryGetProperty( "schema", out var rs ) ? NormalizeJson( rs.ToString() ) : "{}";

			if ( localSchema == remoteSchema )
				schemaChanges.Add( "schema: identical" );
			else
				schemaChanges.Add( "schema: differs" );

			// Compare metadata fields (non-structural)
			CompareField( local, remote, "description", metaChanges );
			CompareField( local, remote, "accessMode", metaChanges );
			CompareField( local, remote, "collectionType", metaChanges );
			CompareField( local, remote, "maxRecords", metaChanges );
			CompareField( local, remote, "allowRecordDelete", metaChanges );
			CompareField( local, remote, "requireSaveVersion", metaChanges );
			CompareField( local, remote, "rateLimitAction", metaChanges );

			// Compare rateLimits object
			var localRL = local.TryGetProperty( "rateLimits", out var lrl ) ? NormalizeJson( lrl.ToString() ) : "";
			var remoteRL = remote.TryGetProperty( "rateLimits", out var rrl ) ? NormalizeJson( rrl.ToString() ) : "";
			if ( localRL != remoteRL )
				metaChanges.Add( "rateLimits" );

			// Compare constants/tables
			var localConst = local.TryGetProperty( "constants", out var lc ) ? NormalizeJson( lc.ToString() ) : "";
			var remoteConst = remote.TryGetProperty( "constants", out var rc ) ? NormalizeJson( rc.ToString() ) : "";
			if ( localConst != remoteConst )
				metaChanges.Add( "constants" );

			var localTables = local.TryGetProperty( "tables", out var lt ) ? NormalizeJson( lt.ToString() ) : "";
			var remoteTables = remote.TryGetProperty( "tables", out var rt ) ? NormalizeJson( rt.ToString() ) : "";
			if ( localTables != remoteTables )
				metaChanges.Add( "tables" );

			var parts = new List<string>();
			parts.AddRange( schemaChanges );
			if ( metaChanges.Count > 0 )
				parts.Add( $"metadata differs: {string.Join( ", ", metaChanges )}" );

			return string.Join( " | ", parts );
		}
		catch { return "Content differs (click View Diff)"; }
	}

	private static void CompareField( JsonElement local, JsonElement remote, string field, List<string> changes )
	{
		var lv = local.TryGetProperty( field, out var l ) ? l.ToString() : "";
		var rv = remote.TryGetProperty( field, out var r ) ? r.ToString() : "";
		if ( lv != rv )
			changes.Add( $"{field}: {lv} → {rv}" );
	}

	/// <summary>
	/// Merge only non-structural metadata from remote into the local collection file.
	/// Pulls: description, accessMode, rateLimits, rateLimitAction, maxRecords, etc.
	/// Keeps: schema, constants, tables unchanged (local version preserved).
	/// </summary>
	private void MergeMetadata( string id )
	{
		if ( _busy || !id.StartsWith( "col_" ) ) return;

		_items.TryGetValue( id, out var state );
		if ( string.IsNullOrEmpty( state.RemoteJson ) ) return;

		var colName = id[4..];

		ConfirmDialog.Show(
			"Merge Metadata from Remote",
			$"This will update non-structural fields (description, accessMode, rateLimits, etc.) in your local '{colName}' collection file with values from the server. Your schema, constants, and tables will NOT be changed.",
			() =>
			{
				try
				{
					var localFile = _collectionFiles.FirstOrDefault( f => ResourceIdFromFile( f, "collection" ) == colName );
					if ( localFile == null ) return;

					if ( !TryReadLocalResourceFile( localFile, "collection", out var localElement ) )
						return;
					var local = JsonSerializer.Deserialize<Dictionary<string, object>>( localElement.GetRawText(), _readOptions );
					var remote = JsonSerializer.Deserialize<JsonElement>( state.RemoteJson );

					// Merge metadata fields from remote (non-structural)
					string[] metaFields = { "description", "accessMode", "collectionType", "maxRecords",
						"allowRecordDelete", "requireSaveVersion", "rateLimitAction", "webhookOnRateLimit", "rateLimits" };

					foreach ( var field in metaFields )
					{
						if ( remote.TryGetProperty( field, out var val ) )
						{
							local[field] = val.ValueKind switch
							{
								JsonValueKind.String => (object)val.GetString(),
								JsonValueKind.Number => val.TryGetInt32( out var i ) ? i : val.GetDouble(),
								JsonValueKind.True => true,
								JsonValueKind.False => false,
								_ => val
							};
						}
					}

					// Save — preserves local schema, constants, tables
					SyncToolPullWriter.WriteSource( "collection", colName, local );

					SetItemState( id, result: "OK", remoteDiffers: false, status: SyncStatus.InSync, diffSummary: "" );
					_status = $"Merged metadata for {colName}";
					Update();
				}
				catch ( Exception ex )
				{
					_status = $"Merge failed: {ex.Message}";
					Update();
				}
			}
		);
	}

	/// <summary>
	/// Pull additive remote-only fields into every eligible local file.
	/// </summary>
	private void PullAllRemoteSemantics()
	{
		if ( _busy || !SyncToolConfig.IsValid ) return;

		var ids = GetRemoteSemanticsIds();
		if ( ids.Length == 0 ) return;

		var detail = string.Join( "\n", ids.Select( DescribeSyncItem ) );
		ConfirmDialog.Show(
			"Pull Remote Semantics",
			$"This will update {ids.Length} local file(s) with additive remote-only fields. Files with modified or missing content are excluded from this action.",
			() => _ = DoPullAllRemoteSemantics( ids ),
			detail: detail );
	}

	private Task DoPullAllRemoteSemantics( string[] ids )
	{
		_busy = true;
		_busyItem = "pull_remote_semantics_all";
		_status = $"Pulling remote semantics for {ids.Length} item(s)...";
		Update();

		try
		{
			var okCount = 0;
			var failCount = 0;

			foreach ( var id in ids )
			{
				if ( !_items.TryGetValue( id, out var state ) || string.IsNullOrEmpty( state.RemoteJson ) )
				{
					failCount++;
					continue;
				}

				if ( TryApplyRemoteJsonToLocal( id, state.RemoteJson, out var error ) )
				{
					okCount++;
					SetItemState( id, result: "OK", remoteDiffers: false, status: SyncStatus.InSync, diffSummary: "" );
				}
				else
				{
					failCount++;
					SetItemState( id, result: "FAIL" );
					Log.Warning( $"[SyncTool] Pull remote semantics failed for {id}: {error}" );
				}
			}

			if ( okCount > 0 )
			{
				RefreshFileList();
				TryRunCodeGeneration( "pull remote semantics" );
				InvalidateRemoteCache();
			}

			_status = failCount == 0
				? $"Pulled remote semantics for {okCount} item(s)"
				: $"Pulled remote semantics for {okCount} item(s), {failCount} failed";
		}
		finally
		{
			_busy = false;
			_busyItem = null;
			Update();
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// Open the MergeViewWindow showing additive remote-only fields with explanations.
	/// </summary>
	private void OpenMergeView( string id, string name )
	{
		if ( !_items.TryGetValue( id, out var state ) ) return;

		var (added, changed, _) = MergeViewWindow.AnalyzeDifferences( state.LocalJson ?? "{}", state.RemoteJson ?? "{}" );

		var resourceType = id.StartsWith( "ep_" ) ? "endpoint"
			: id.StartsWith( "col_" ) ? "collection"
			: "workflow";

		var capturedId = id;
		var window = new MergeViewWindow( name, resourceType, added, changed, () => DoMergeItem( capturedId ) );
		window.Show();
	}

	/// <summary>
	/// Accept the additive remote semantics by saving the remote JSON to the local file.
	/// </summary>
	private void DoMergeItem( string id )
	{
		_items.TryGetValue( id, out var state );
		if ( string.IsNullOrEmpty( state.RemoteJson ) ) return;

		if ( TryApplyRemoteJsonToLocal( id, state.RemoteJson, out var error ) )
		{
			SetItemState( id, result: "OK", remoteDiffers: false, status: SyncStatus.InSync, diffSummary: "" );
			RefreshFileList();
			TryRunCodeGeneration( "pull remote semantics" );
			InvalidateRemoteCache();
			_status = $"Pulled remote semantics for {id}";
			Update();
		}
		else
		{
			_status = $"Pull remote semantics failed: {error}";
			Update();
		}
	}

	private bool TryApplyRemoteJsonToLocal( string id, string remoteJson, out string error )
	{
		try
		{
			var json = TryGetCurrentLocalJson( id, out var localJson )
				? MergeRemoteOnlyFields( localJson, remoteJson )
				: PrettyJson( remoteJson );

			if ( id.StartsWith( "ep_" ) )
			{
				var slug = id[3..];
				var data = JsonSerializer.Deserialize<Dictionary<string, object>>( json, _readOptions );
				SyncToolPullWriter.WriteSource( "endpoint", slug, data );
			}
			else if ( id.StartsWith( "col_" ) )
			{
				var colName = id[4..];
				var data = JsonSerializer.Deserialize<Dictionary<string, object>>( json, _readOptions );
				SyncToolPullWriter.WriteSource( "collection", colName, data );
			}
			else if ( id.StartsWith( "wf_" ) )
			{
				var wfId = id[3..];
				var data = JsonSerializer.Deserialize<Dictionary<string, object>>( json, _readOptions );
				SyncToolPullWriter.WriteSource( "workflow", wfId, data );
			}
			else if ( id.StartsWith( "test_" ) )
			{
				var testId = id[5..];
				var dir = SyncToolConfig.Abs( SyncToolConfig.TestsPath );
				if ( !Directory.Exists( dir ) ) Directory.CreateDirectory( dir );
				File.WriteAllText( Path.Combine( dir, $"{testId}.json" ), json );
			}
			else
			{
				error = $"Unsupported sync item: {id}";
				return false;
			}

			error = null;
			return true;
		}
		catch ( Exception ex )
		{
			error = ex.Message;
			return false;
		}
	}

	private bool TryGetCurrentLocalJson( string id, out string localJson )
	{
		localJson = null;
		JsonElement local;

		if ( id.StartsWith( "ep_" ) )
		{
			var slug = id[3..];
			var localFile = _endpointFiles.FirstOrDefault( f => ResourceIdFromFile( f, "endpoint" ) == slug );
			if ( localFile == null || !TryReadLocalResourceFile( localFile, "endpoint", out local ) )
				return false;

			localJson = JsonSerializer.Serialize( SyncToolTransforms.ServerEndpointToLocal( local ), new JsonSerializerOptions { WriteIndented = true } );
			return true;
		}

		if ( id.StartsWith( "col_" ) )
		{
			var colName = id[4..];
			var localFile = _collectionFiles.FirstOrDefault( f => ResourceIdFromFile( f, "collection" ) == colName );
			if ( localFile == null || !TryReadLocalResourceFile( localFile, "collection", out local ) )
				return false;

			var data = JsonSerializer.Deserialize<Dictionary<string, object>>( local.GetRawText(), _readOptions );
			localJson = JsonSerializer.Serialize( SyncToolTransforms.StripServerManagedFields( data ), new JsonSerializerOptions { WriteIndented = true } );
			return true;
		}

		if ( id.StartsWith( "wf_" ) )
		{
			var wfId = id[3..];
			var localFile = SyncToolConfig.FindWorkflowFileById( wfId );
			if ( localFile == null || !TryReadLocalResourceFile( localFile, "workflow", out local ) )
				return false;

			localJson = JsonSerializer.Serialize( SyncToolTransforms.ServerWorkflowToLocal( local ), new JsonSerializerOptions { WriteIndented = true } );
			return true;
		}

		return false;
	}

	private static string MergeRemoteOnlyFields( string localJson, string remoteJson )
	{
		var local = JsonSerializer.Deserialize<JsonElement>( localJson );
		var remote = JsonSerializer.Deserialize<JsonElement>( remoteJson );
		var merged = MergeRemoteOnlyFields( local, remote );
		return JsonSerializer.Serialize( merged, new JsonSerializerOptions { WriteIndented = true } );
	}

	private static object MergeRemoteOnlyFields( JsonElement local, JsonElement remote )
	{
		if ( local.ValueKind == JsonValueKind.Object && remote.ValueKind == JsonValueKind.Object )
		{
			var result = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
			var remoteProps = remote.EnumerateObject().ToDictionary( prop => prop.Name, prop => prop.Value, StringComparer.OrdinalIgnoreCase );

			foreach ( var localProp in local.EnumerateObject() )
			{
				result[localProp.Name] = remoteProps.TryGetValue( localProp.Name, out var remoteValue )
					? MergeRemoteOnlyFields( localProp.Value, remoteValue )
					: JsonElementToPlainObject( localProp.Value );
			}

			foreach ( var remoteProp in remote.EnumerateObject() )
			{
				if ( !result.ContainsKey( remoteProp.Name ) )
					result[remoteProp.Name] = JsonElementToPlainObject( remoteProp.Value );
			}

			return result;
		}

		return JsonElementToPlainObject( local );
	}

	private static object JsonElementToPlainObject( JsonElement value )
	{
		return value.ValueKind switch
		{
			JsonValueKind.Object => value.EnumerateObject()
				.ToDictionary( prop => prop.Name, prop => JsonElementToPlainObject( prop.Value ), StringComparer.OrdinalIgnoreCase ),
			JsonValueKind.Array => value.EnumerateArray().Select( JsonElementToPlainObject ).ToList(),
			JsonValueKind.String => value.GetString(),
			JsonValueKind.Number when value.TryGetInt64( out var integer ) => integer,
			JsonValueKind.Number when value.TryGetDouble( out var number ) => number,
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			_ => null
		};
	}

	private void TryRunCodeGeneration( string context )
	{
		try
		{
			CodeGenerator.Generate();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Code generation after {context} failed: {ex.Message}" );
		}
	}

	private void InvalidateRemoteCache()
	{
		_remoteEndpoints = null;
		_remoteCollections = null;
		_remoteWorkflows = null;
	}

	private void OpenDiffView( string id, string name )
	{
		if ( !_items.TryGetValue( id, out var state ) ) return;
		var window = new DiffViewWindow( name, state.LocalJson ?? "", state.RemoteJson ?? "" );
		window.Show();
	}

	[Button( "Docs", Icon = "menu_book" )]
	public void OpenDocs()
	{
		Process.Start( new ProcessStartInfo
		{
			FileName = "https://sboxcool.com/wiki/network-storage-v3",
			UseShellExecute = true
		} );
	}

	[Button( "Setup", Icon = "settings" )]
	public void OpenSetup()
	{
		var window = new SetupWindow();
		window.Show();
	}

	[Button( "Refresh", Icon = "refresh" )]
	public void Refresh()
	{
		SyncToolConfig.Load();
		RefreshFileList();
		_items.Clear();
		_syncLog.Clear();
		_scrollY = 0;
		_hasCheckedRemote = false;

		_remoteEndpoints = null;
		_remoteCollections = null;
		_remoteWorkflows = null;
		_status = SyncToolConfig.IsValid ? "Refreshed" : "Config invalid — check .env";
		Update();
	}
}
