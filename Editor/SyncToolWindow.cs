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
public class SyncToolWindow : DockWindow
{
	private string _status = "Ready";
	private bool _busy;
	private string _busyItem;
	private Dictionary<string, ItemState> _items = new();
	private List<ClickRegion> _buttons = new();
	private Vector2 _mousePos;
	private bool _hasCheckedRemote;

	// Cached file lists
	private string[] _endpointFiles = Array.Empty<string>();
	private string[] _collectionFiles = Array.Empty<string>(); // collections/{name}.json
	private string[] _workflowFiles = Array.Empty<string>(); // workflows/{id}.json

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
		Size = new Vector2( 480, 620 );
		MinimumSize = new Vector2( 400, 400 );
		SyncToolConfig.Load();
		RefreshFileList();
	}

	[Menu( "Editor", "Network Storage/Sync Tool" )]
	public static void OpenWindow()
	{
		var window = new SyncToolWindow();
		window.Show();
	}

	private void RefreshFileList()
	{
		var epDir = SyncToolConfig.Abs( SyncToolConfig.EndpointsPath );
		_endpointFiles = Directory.Exists( epDir )
			? Directory.GetFiles( epDir, "*.json" ).OrderBy( f => f ).ToArray()
			: Array.Empty<string>();

		var colDir = SyncToolConfig.Abs( SyncToolConfig.CollectionsPath );
		_collectionFiles = Directory.Exists( colDir )
			? Directory.GetFiles( colDir, "*.json" ).OrderBy( f => f ).ToArray()
			: Array.Empty<string>();

		var wfDir = SyncToolConfig.Abs( SyncToolConfig.WorkflowsPath );
		_workflowFiles = Directory.Exists( wfDir )
			? Directory.GetFiles( wfDir, "*.json" ).OrderBy( f => f ).ToArray()
			: Array.Empty<string>();
	}

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

		// Push All button (next to header)
		if ( SyncToolConfig.IsValid )
		{
			var pushAllW = 70f;
			var pushAllRect = new Rect( pad + w - pushAllW, y, pushAllW, 22 );
			DrawSmallButton( pushAllRect, "Push All", Color.Green, "push_all", () => _ = PushAll() );
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
		}

		DrawSeparator( ref y, w, pad );

		// ── Begin scrollable content ──
		_scrollAreaTop = y;
		y -= _scrollY;

		// ── Endpoints ──
		var localEpCount = _endpointFiles.Length;
		var remoteEpSlugs = GetRemoteEndpointSlugs();
		var allSlugs = new HashSet<string>();

		foreach ( var f in _endpointFiles )
			allSlugs.Add( Path.GetFileNameWithoutExtension( f ) );
		foreach ( var s in remoteEpSlugs )
			allSlugs.Add( s );

		DrawSectionHeader( ref y, pad, w, $"ENDPOINTS ({allSlugs.Count})" );

		if ( allSlugs.Count > 0 )
		{
			foreach ( var slug in allSlugs.OrderBy( s => s ) )
			{
				var id = $"ep_{slug}";
				var localFile = _endpointFiles.FirstOrDefault( f => Path.GetFileNameWithoutExtension( f ) == slug );
				var hasLocal = localFile != null;
				var info = hasLocal ? GetEndpointInfo( localFile ) : "remote only";

				DrawResourceRow( ref y, pad, w, $"{slug}.json", info, id,
					hasLocal ? () => PushItem( id ) : null,
					() => PullItem( id ) );
			}
		}
		else
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.3f ) );
			Paint.DrawText( new Rect( pad + 8, y, w, 16 ), "No endpoint files found", TextFlag.LeftCenter );
			y += 22;
		}

		DrawSeparator( ref y, w, pad );

		// ── Collections ──
		var localColNames = _collectionFiles.Select( f => Path.GetFileNameWithoutExtension( f ) ).ToHashSet();
		var remoteColNames = GetRemoteCollectionNames();
		var allColNames = new HashSet<string>( localColNames );
		foreach ( var n in remoteColNames ) allColNames.Add( n );

		DrawSectionHeader( ref y, pad, w, $"COLLECTIONS ({allColNames.Count})" );

		if ( allColNames.Count > 0 )
		{
			foreach ( var colName in allColNames.OrderBy( n => n ) )
			{
				var id = $"col_{colName}";
				var hasLocal = localColNames.Contains( colName );
				var info = hasLocal ? "schema" : "remote only";

				DrawResourceRow( ref y, pad, w, $"{colName}.json", info, id,
					hasLocal ? () => PushItem( id ) : null,
					() => PullItem( id ) );
			}
		}
		else
		{
			Paint.SetDefaultFont( size: 10 );
			Paint.SetPen( Color.White.WithAlpha( 0.3f ) );
			Paint.DrawText( new Rect( pad + 8, y, w, 16 ), "No collections found", TextFlag.LeftCenter );
			y += 22;
		}

		DrawSeparator( ref y, w, pad );

		// ── Workflows ──
		var localWfIds = _workflowFiles.Select( f => Path.GetFileNameWithoutExtension( f ) ).ToHashSet();
		var remoteWfIds = GetRemoteWorkflowIds();
		var allWfIds = new HashSet<string>( localWfIds );
		foreach ( var id2 in remoteWfIds ) allWfIds.Add( id2 );

		DrawSectionHeader( ref y, pad, w, $"WORKFLOWS ({allWfIds.Count})" );

		if ( allWfIds.Count > 0 )
		{
			foreach ( var wfId in allWfIds.OrderBy( n => n ) )
			{
				var itemId = $"wf_{wfId}";
				var hasLocal = localWfIds.Contains( wfId );
				var info = hasLocal ? GetWorkflowInfo( _workflowFiles.FirstOrDefault( f => Path.GetFileNameWithoutExtension( f ) == wfId ) ) : "remote only";

				DrawResourceRow( ref y, pad, w, $"{wfId}.json", info, itemId,
					hasLocal ? () => PushItem( itemId ) : null,
					() => PullItem( itemId ) );
			}
		}
		else
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
		Paint.SetPen( _busy ? Color.Yellow : Color.White.WithAlpha( 0.4f ) );
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
			var mergeLogCount = _syncLog.Count( e => e.Detail != null && e.Detail.Contains( "merge available" ) );
			if ( mergeLogCount > 0 )
			{
				Paint.SetPen( Color.Green.WithAlpha( 0.8f ) );
				Paint.DrawText( new Rect( sx, y, 100, 16 ), $"⇄ {mergeLogCount} to merge", TextFlag.LeftCenter );
				sx += 90;
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
					var isMergeAvail = entry.Detail != null && entry.Detail.Contains( "merge available" );
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

		// Header + Push All
		Paint.SetDefaultFont( size: 13, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w * 0.55f, 22 ), "Network Storage Sync", TextFlag.LeftCenter );

		if ( SyncToolConfig.IsValid )
		{
			var pushAllW = 70f;
			var pushAllRect = new Rect( pad + w - pushAllW, y, pushAllW, 22 );
			DrawSmallButton( pushAllRect, "Push All", Color.Green, "push_all", () => _ = PushAll() );
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
		Action pushAction, Action pullAction )
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

		// Pull/Merge button — LEFT side (only if remote has changes to pull or merge)
		if ( state.Status == SyncStatus.MergeAvailable )
		{
			var mergeW = 52f;
			var mergeRect = new Rect( contentX, btnY, mergeW, btnH );
			var capturedMergeId = id;
			var capturedMergeName = name;
			DrawSmallButton( mergeRect, "Merge", Color.Green, $"merge_{id}",
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
		Paint.SetDefaultFont( size: 10 );
		Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
		var nameW = w - ( contentX - pad ) - btnW - 80;
		Paint.DrawText( new Rect( contentX, y, nameW, rowH ), name, TextFlag.LeftCenter );

		// Status badge (right of name, before push button)
		if ( hasStatusBadge && !hasResult )
		{
			var (badgeText, badgeColor) = state.Status switch
			{
				SyncStatus.InSync => ("Synced", Color.Green.WithAlpha( 0.5f )),
				SyncStatus.LocalOnly => ("Local, not pushed", Color.Yellow.WithAlpha( 0.7f )),
				SyncStatus.RemoteOnly => ("Remote only", Color.Cyan.WithAlpha( 0.7f )),
				SyncStatus.Differs => ("Changed", Color.Orange.WithAlpha( 0.7f )),
				SyncStatus.MergeAvailable => ("Merge available", Color.Green.WithAlpha( 0.7f )),
				_ => ("", Color.White.WithAlpha( 0.3f ))
			};

			if ( !string.IsNullOrEmpty( badgeText ) )
			{
				Paint.SetDefaultFont( size: 8 );
				Paint.SetPen( badgeColor );
				var badgeX = contentX + nameW + 4;
				Paint.DrawText( new Rect( badgeX, y, 90, rowH ), badgeText, TextFlag.LeftCenter );
			}
		}

		// Push button — RIGHT side, always at far right
		if ( pushAction != null )
		{
			var pushRect = new Rect( pad + w - btnW - 4, btnY, btnW, btnH );
			DrawSmallButton( pushRect, "Push", Color.White, $"push_{id}", pushAction );
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

	protected override void OnWheel( WheelEvent e )
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

	private List<string> GetRemoteEndpointSlugs()
	{
		if ( !_remoteEndpoints.HasValue ) return new List<string>();
		var data = _remoteEndpoints.Value;
		if ( data.TryGetProperty( "data", out var d ) ) data = d;
		if ( data.ValueKind != JsonValueKind.Array ) return new List<string>();

		var slugs = new List<string>();
		foreach ( var ep in data.EnumerateArray() )
		{
			if ( ep.TryGetProperty( "slug", out var s ) )
				slugs.Add( s.GetString() );
		}
		return slugs;
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
			var text = File.ReadAllText( filePath );
			var wf = JsonSerializer.Deserialize<JsonElement>( text );
			return wf.TryGetProperty( "name", out var n ) ? n.GetString() : "workflow";
		}
		catch { return ""; }
	}

	private string GetEndpointInfo( string filePath )
	{
		try
		{
			var text = File.ReadAllText( filePath );
			var ep = JsonSerializer.Deserialize<JsonElement>( text );
			return ep.TryGetProperty( "method", out var m ) ? m.GetString() : "POST";
		}
		catch { return ""; }
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

	// ──────────────────────────────────────────────────────
	//  Check for Updates (compare local vs remote)
	// ──────────────────────────────────────────────────────

	private async Task CheckForUpdates()
	{
		if ( _busy || !SyncToolConfig.IsValid ) return;
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
		var diffs = 0;
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

		var localColByName = new Dictionary<string, string>();
		foreach ( var (name, data) in localCollections )
			localColByName[name] = JsonSerializer.Serialize( data, new JsonSerializerOptions { WriteIndented = true } );

		var localWfById = new Dictionary<string, JsonElement>();
		foreach ( var wf in localWorkflows )
		{
			var wfId = wf.TryGetProperty( "id", out var id ) ? id.GetString() : "";
			if ( !string.IsNullOrEmpty( wfId ) ) localWfById[wfId] = wf;
		}

		// ── Fetch all 3 resource types in parallel ──
		var remoteEpsTask = SyncToolApi.GetEndpoints();
		var remoteColsTask = SyncToolApi.GetCollections();
		var remoteWfsTask = SyncToolApi.GetWorkflows();

		await Task.WhenAll( remoteEpsTask, remoteColsTask, remoteWfsTask );

		_remoteEndpoints = await remoteEpsTask;
		_remoteCollections = await remoteColsTask;
		_remoteWorkflows = await remoteWfsTask;

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
					var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
					if ( string.IsNullOrEmpty( slug ) ) continue;
					remoteSlugs.Add( slug );
					var id = $"ep_{slug}";

					var remoteLocal = SyncToolTransforms.ServerEndpointToLocal( ep );
					var remoteJson = JsonSerializer.Serialize( remoteLocal, new JsonSerializerOptions { WriteIndented = true } );

					if ( !localEpBySlug.TryGetValue( slug, out var localEp ) )
					{
						SetItemState( id, remoteDiffers: true, status: SyncStatus.RemoteOnly,
							diffSummary: "Remote only — not in local files",
							localJson: "", remoteJson: PrettyJson( remoteJson ) );
						diffs++;
					}
					else
					{
						var localJson = JsonSerializer.Serialize( localEp, new JsonSerializerOptions { WriteIndented = true } );
						var differs = NormalizeJson( remoteJson ) != NormalizeJson( localJson );

						if ( differs )
						{
							// Classify: is the diff only server-added defaults?
							var (addedFields, changedFields, isDefaultsOnly) = MergeViewWindow.AnalyzeDifferences(
								PrettyJson( localJson ), PrettyJson( remoteJson ) );

							if ( isDefaultsOnly && changedFields.Count == 0 )
							{
								SetItemState( id, remoteDiffers: true, status: SyncStatus.MergeAvailable,
									diffSummary: $"Server added {addedFields.Count} default field(s) — click Merge to accept",
									localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
							}
							else
							{
								SetItemState( id, remoteDiffers: true, status: SyncStatus.Differs,
									diffSummary: DiffEndpoint( localJson, remoteJson, slug ),
									localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
							}
							diffs++;
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
						// Classify: is the diff only server-added defaults?
						var (addedFields, changedFields, isDefaultsOnly) = MergeViewWindow.AnalyzeDifferences(
							PrettyJson( localJson ), PrettyJson( remoteJson ) );

						if ( isDefaultsOnly && changedFields.Count == 0 )
						{
							SetItemState( id, remoteDiffers: true, status: SyncStatus.MergeAvailable,
								diffSummary: $"Server added {addedFields.Count} default field(s) — click Merge to accept",
								localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
						}
						else
						{
							SetItemState( id, remoteDiffers: true, status: SyncStatus.Differs,
								diffSummary: DiffCollectionSchema( localJson, remoteJson ),
								localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
						}
						diffs++;
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
					var localJson = JsonSerializer.Serialize( localWf, new JsonSerializerOptions { WriteIndented = true } );
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
						// Classify: is the diff only server-added defaults?
						var (addedFields, changedFields, isDefaultsOnly) = MergeViewWindow.AnalyzeDifferences(
							PrettyJson( localJson ), PrettyJson( remoteJson ) );

						if ( isDefaultsOnly && changedFields.Count == 0 )
						{
							SetItemState( id, remoteDiffers: true, status: SyncStatus.MergeAvailable,
								diffSummary: $"Server added {addedFields.Count} default field(s) — click Merge to accept",
								localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
						}
						else
						{
							SetItemState( id, remoteDiffers: true, status: SyncStatus.Differs, diffSummary: "Content differs",
								localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
						}
						diffs++;
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
					sorted[prop.Name] = SortJsonElement( prop.Value );
				return sorted;
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

	// ──────────────────────────────────────────────────────
	//  Push (with remote-newer warning)
	// ──────────────────────────────────────────────────────

	private async Task PushAll()
	{
		if ( _busy || !SyncToolConfig.IsValid ) return;
		_busy = true;
		_busyItem = "push_all";
		_status = "Pushing all resources...";
		_syncLog.Clear();
		foreach ( var k in _items.Keys.ToList() ) SetItemState( k, result: null );
		Update();

		// ── Load local files for pre-push comparison ──
		var localEndpoints = SyncToolConfig.LoadEndpoints();
		var localEpBySlug = new Dictionary<string, string>();
		foreach ( var ep in localEndpoints )
		{
			var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
			if ( !string.IsNullOrEmpty( slug ) )
				localEpBySlug[slug] = NormalizeJson( JsonSerializer.Serialize( ep ) );
		}

		// ── Push all resources in parallel ──
		_busyItem = "push_all";
		_status = "Pushing all resources...";
		Update();

		var pushEpTask = _endpointFiles.Length > 0 ? DoPushAllEndpoints() : Task.FromResult( false );
		var pushColTask = _collectionFiles.Length > 0 ? DoPushCollections() : Task.FromResult( false );
		var pushWfTask = _workflowFiles.Length > 0 ? DoPushAllWorkflows() : Task.FromResult( false );

		await Task.WhenAll( pushEpTask, pushColTask, pushWfTask );

		var epOk = _endpointFiles.Length > 0 ? await pushEpTask : false;
		var colOk = _collectionFiles.Length > 0 ? await pushColTask : false;
		var wfOk = _workflowFiles.Length > 0 ? await pushWfTask : false;

		// ── Log push results ──
		if ( _endpointFiles.Length > 0 )
		{
			var failDetail = GetPushFailDetail( "endpoints" );
			foreach ( var f in _endpointFiles )
			{
				var slug = Path.GetFileNameWithoutExtension( f );
				var detail = epOk ? "Pushed" : failDetail;
				SetItemState( $"ep_{slug}", result: epOk ? "OK" : "FAIL",
					remoteDiffers: false, diffSummary: "", status: epOk ? SyncStatus.InSync : null );
				_syncLog.Add( new SyncLogEntry { Name = $"{slug}.json", Type = "Endpoint", Ok = epOk, Detail = detail } );
			}
		}

		if ( _collectionFiles.Length > 0 )
		{
			var failDetail = GetPushFailDetail( "collections" );
			foreach ( var f in _collectionFiles )
			{
				var name = Path.GetFileNameWithoutExtension( f );
				var detail = colOk ? "Pushed" : failDetail;
				SetItemState( $"col_{name}", result: colOk ? "OK" : "FAIL",
					remoteDiffers: false, diffSummary: "", status: colOk ? SyncStatus.InSync : null );
				_syncLog.Add( new SyncLogEntry { Name = $"{name}.json", Type = "Collection", Ok = colOk, Detail = detail } );
			}
		}

		if ( _workflowFiles.Length > 0 )
		{
			var failDetail = GetPushFailDetail( "workflows" );
			foreach ( var f in _workflowFiles )
			{
				var name = Path.GetFileNameWithoutExtension( f );
				var detail = wfOk ? "Pushed" : failDetail;
				SetItemState( $"wf_{name}", result: wfOk ? "OK" : "FAIL",
					remoteDiffers: false, diffSummary: "", status: wfOk ? SyncStatus.InSync : null );
				_syncLog.Add( new SyncLogEntry { Name = $"{name}.json", Type = "Workflow", Ok = wfOk, Detail = detail } );
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
		var mergeCount = _syncLog.Count( e => e.Detail != null && e.Detail.Contains( "merge available" ) );

		if ( mergeCount > 0 && mismatchCount == 0 && failCount == 0 )
			_status = $"Pushed OK — {mergeCount} item(s) have server defaults to merge";
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
			// Re-load local files fresh
			var localCollections = SyncToolConfig.LoadCollections();
			var localColByName = new Dictionary<string, string>();
			foreach ( var (name, data) in localCollections )
				localColByName[name] = NormalizeJson( JsonSerializer.Serialize( data, new JsonSerializerOptions { WriteIndented = true } ) );

			var localWorkflows = SyncToolConfig.LoadWorkflows();
			var localWfById = new Dictionary<string, string>();
			foreach ( var wf in localWorkflows )
			{
				var wfId = wf.TryGetProperty( "id", out var id ) ? id.GetString() : "";
				if ( !string.IsNullOrEmpty( wfId ) )
					localWfById[wfId] = NormalizeJson( JsonSerializer.Serialize( wf ) );
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
						var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
						if ( string.IsNullOrEmpty( slug ) ) continue;

						var remoteLocal = SyncToolTransforms.ServerEndpointToLocal( ep );
						var remoteNorm = NormalizeJson( JsonSerializer.Serialize( remoteLocal ) );

						var logIdx = _syncLog.FindIndex( e => e.Name == $"{slug}.json" && e.Type == "Endpoint" );
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
							var (addedFields, changedFields, isDefaultsOnly) = MergeViewWindow.AnalyzeDifferences( localPretty, remotePretty );

							if ( isDefaultsOnly && changedFields.Count == 0 )
							{
								entry.Detail = $"Server added {addedFields.Count} default(s) — merge available";
								entry.Ok = true;
								SetItemState( eid, remoteDiffers: true, status: SyncStatus.MergeAvailable,
									diffSummary: $"Server added {addedFields.Count} default field(s) — click Merge to accept",
									localJson: localPretty, remoteJson: remotePretty );
							}
							else
							{
								entry.Detail = "Mismatch — pushed but remote differs. See diff";
								entry.Ok = false;
								SetItemState( eid, remoteDiffers: true, status: SyncStatus.Differs,
									diffSummary: "Post-push verification failed — remote doesn't match local",
									localJson: localPretty, remoteJson: remotePretty );
							}
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
					var logIdx = _syncLog.FindIndex( e => e.Name == $"{colName}.json" && e.Type == "Collection" );
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
						var (addedFields, changedFields, isDefaultsOnly) = MergeViewWindow.AnalyzeDifferences( localPretty, remotePretty );

						if ( isDefaultsOnly && changedFields.Count == 0 )
						{
							entry.Detail = $"Server added {addedFields.Count} default(s) — merge available";
							entry.Ok = true;
							SetItemState( cid, remoteDiffers: true, status: SyncStatus.MergeAvailable,
								diffSummary: $"Server added {addedFields.Count} default field(s) — click Merge to accept",
								localJson: localPretty, remoteJson: remotePretty );
						}
						else
						{
							entry.Detail = "Mismatch — pushed but remote differs. See diff";
							entry.Ok = false;
							SetItemState( cid, remoteDiffers: true, status: SyncStatus.Differs,
								diffSummary: "Post-push verification failed — remote doesn't match local",
								localJson: localPretty, remoteJson: remotePretty );
						}
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
					var logIdx = _syncLog.FindIndex( e => e.Name == $"{wfId}.json" && e.Type == "Workflow" );
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
						var (addedFields, changedFields, isDefaultsOnly) = MergeViewWindow.AnalyzeDifferences( localPretty, remotePretty );

						if ( isDefaultsOnly && changedFields.Count == 0 )
						{
							entry.Detail = $"Server added {addedFields.Count} default(s) — merge available";
							entry.Ok = true;
							SetItemState( wid, remoteDiffers: true, status: SyncStatus.MergeAvailable,
								diffSummary: $"Server added {addedFields.Count} default field(s) — click Merge to accept",
								localJson: localPretty, remoteJson: remotePretty );
						}
						else
						{
							entry.Detail = "Mismatch — pushed but remote differs. See diff";
							entry.Ok = false;
							SetItemState( wid, remoteDiffers: true, status: SyncStatus.Differs,
								diffSummary: "Post-push verification failed — remote doesn't match local",
								localJson: localPretty, remoteJson: remotePretty );
						}
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
				itemName = $"{slug}.json";
				itemType = "Endpoint";
			}
			else if ( id.StartsWith( "col_" ) )
			{
				ok = await DoPushCollections();
				itemName = $"{id[4..]}.json";
				itemType = "Collection";
			}
			else if ( id.StartsWith( "wf_" ) )
			{
				ok = await DoPushAllWorkflows();
				itemName = $"{id[3..]}.json";
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
			_syncLog.Add( new SyncLogEntry { Name = itemName, Type = itemType, Ok = ok, Detail = ok ? "Pushed" : "Push failed" } );

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
	private static string GetPushFailDetail( string resource )
	{
		if ( SyncToolApi.LastErrorCode == "KEY_UPGRADE_REQUIRED" )
			return "Key uses old format -- regenerate at sbox.cool";
		if ( SyncToolApi.LastErrorCode == "FORBIDDEN" )
			return $"No write permission for {resource}";
		if ( !string.IsNullOrEmpty( SyncToolApi.LastErrorMessage ) )
			return SyncToolApi.LastErrorMessage;
		return "Push failed";
	}

	// ──────────────────────────────────────────────────────
	//  Push implementation
	// ──────────────────────────────────────────────────────

	private async Task<bool> DoPushAllEndpoints()
	{
		var localEps = SyncToolConfig.LoadEndpoints();
		if ( localEps.Count == 0 ) return false;
		var existing = await SyncToolApi.GetEndpoints();
		var serverFmt = SyncToolTransforms.EndpointsToServer( localEps, existing );
		var resp = await SyncToolApi.PushEndpoints( serverFmt );
		return resp.HasValue;
	}

	/// <summary>
	/// Push a single endpoint by merging it into the remote list.
	/// GETs remote endpoints, replaces the matching slug, PUTs the merged list.
	/// </summary>
	private async Task<bool> DoPushSingleEndpointMerged( string slug )
	{
		// Read the local endpoint file
		var localFile = _endpointFiles.FirstOrDefault( f => Path.GetFileNameWithoutExtension( f ) == slug );
		if ( localFile == null ) return false;

		var localText = File.ReadAllText( localFile );
		var localEp = JsonSerializer.Deserialize<JsonElement>( localText );

		// GET current remote endpoints
		var remoteResp = await SyncToolApi.GetEndpoints();
		if ( !remoteResp.HasValue ) return false;

		var data = remoteResp.Value;
		if ( data.TryGetProperty( "data", out var d ) ) data = d;
		if ( data.ValueKind != JsonValueKind.Array ) return false;

		// Build merged list: replace matching slug, keep others
		var merged = new List<object>();
		var replaced = false;
		foreach ( var ep in data.EnumerateArray() )
		{
			var epSlug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
			if ( epSlug == slug )
			{
				// Use local version but preserve server ID
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
			// New endpoint, append
			var localDict = SyncToolTransforms.ServerEndpointToLocal( localEp );
			localDict["id"] = Guid.NewGuid().ToString( "N" )[..16];
			merged.Add( localDict );
		}

		var mergedJson = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( merged ) );
		var resp = await SyncToolApi.PushEndpoints( mergedJson );
		return resp.HasValue;
	}

	private async Task<bool> DoPushCollections()
	{
		var collections = SyncToolConfig.LoadCollections();
		if ( collections.Count == 0 ) return false;
		var serverFmt = SyncToolTransforms.CollectionsToServer( collections.Select( c => c.Data ).ToList() );
		var resp = await SyncToolApi.PushCollections( serverFmt );
		return resp.HasValue;
	}

	private async Task<bool> DoPushAllWorkflows()
	{
		var localWfs = SyncToolConfig.LoadWorkflows();
		if ( localWfs.Count == 0 ) return false;
		var existing = await SyncToolApi.GetWorkflows();
		var serverFmt = SyncToolTransforms.WorkflowsToServer( localWfs, existing );
		var resp = await SyncToolApi.PushWorkflows( serverFmt );
		return resp.HasValue;
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

				var localDict = SyncToolTransforms.ServerEndpointToLocal( ep );
				if ( !Directory.Exists( SyncToolConfig.Abs( SyncToolConfig.EndpointsPath ) ) )
					Directory.CreateDirectory( SyncToolConfig.Abs( SyncToolConfig.EndpointsPath ) );

				var json = JsonSerializer.Serialize( localDict, new JsonSerializerOptions { WriteIndented = true } );
				File.WriteAllText( SyncToolConfig.Abs( $"{SyncToolConfig.EndpointsPath}/{slug}.json" ), json );
				return true;
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
			var collections = SyncToolTransforms.ServerToCollections( resp.Value );
			if ( collections.Count == 0 ) return false;
			SyncToolConfig.SaveCollections( collections );
			return true;
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
			var collections = SyncToolTransforms.ServerToCollections( resp.Value );
			var match = collections.FirstOrDefault( c => c.Name == colName );
			if ( match.Local == null ) return false;
			SyncToolConfig.SaveCollection( colName, match.Local );
			return true;
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
			var workflows = SyncToolTransforms.ServerToWorkflows( resp.Value );
			var match = workflows.FirstOrDefault( w => w.Id == wfId );
			if ( match.Local == null ) return false;
			SyncToolConfig.SaveWorkflow( wfId, match.Local );
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Pull workflow {wfId} failed: {ex.Message}" );
			return false;
		}
	}

	// ──────────────────────────────────────────────────────
	//  Toolbar
	// ──────────────────────────────────────────────────────

	// ──────────────────────────────────────────────────────
	//  Diff breakdown helpers
	// ──────────────────────────────────────────────────────

	/// <summary>
	/// Compare two endpoint JSON files key-by-key.
	/// Categorizes changes as cosmetic (name, description) vs structural (steps, input, response, method).
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
					var localFile = _collectionFiles.FirstOrDefault( f => Path.GetFileNameWithoutExtension( f ) == colName );
					if ( localFile == null ) return;

					var local = JsonSerializer.Deserialize<Dictionary<string, object>>( File.ReadAllText( localFile ) );
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
					var json = JsonSerializer.Serialize( local, new JsonSerializerOptions { WriteIndented = true } );
					File.WriteAllText( localFile, json );

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
	/// Open the MergeViewWindow showing server-added fields with explanations.
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
	/// Accept the server's version by saving the remote JSON to the local file.
	/// </summary>
	private void DoMergeItem( string id )
	{
		_items.TryGetValue( id, out var state );
		if ( string.IsNullOrEmpty( state.RemoteJson ) ) return;

		try
		{
			// Pretty-print for consistent formatting
			var json = PrettyJson( state.RemoteJson );

			if ( id.StartsWith( "ep_" ) )
			{
				var slug = id[3..];
				var dir = SyncToolConfig.Abs( SyncToolConfig.EndpointsPath );
				if ( !Directory.Exists( dir ) ) Directory.CreateDirectory( dir );
				File.WriteAllText( Path.Combine( dir, $"{slug}.json" ), json );
			}
			else if ( id.StartsWith( "col_" ) )
			{
				var colName = id[4..];
				var dir = SyncToolConfig.Abs( SyncToolConfig.CollectionsPath );
				if ( !Directory.Exists( dir ) ) Directory.CreateDirectory( dir );
				File.WriteAllText( Path.Combine( dir, $"{colName}.json" ), json );
			}
			else if ( id.StartsWith( "wf_" ) )
			{
				var wfId = id[3..];
				var dir = SyncToolConfig.Abs( SyncToolConfig.WorkflowsPath );
				if ( !Directory.Exists( dir ) ) Directory.CreateDirectory( dir );
				File.WriteAllText( Path.Combine( dir, $"{wfId}.json" ), json );
			}

			SetItemState( id, result: "OK", remoteDiffers: false, status: SyncStatus.InSync, diffSummary: "" );
			_status = $"Merged server defaults for {id}";
			RefreshFileList();
			Update();
		}
		catch ( Exception ex )
		{
			_status = $"Merge failed: {ex.Message}";
			Update();
		}
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
