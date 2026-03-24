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

	private struct ClickRegion
	{
		public Rect Rect;
		public string Id;
		public Action OnClick;
	}

	private enum SyncStatus { Unknown, InSync, LocalOnly, RemoteOnly, Differs }

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

	private static string ResolvePath( string relativePath )
		=> Editor.FileSystem.Root.GetFullPath( relativePath );

	private void RefreshFileList()
	{
		var epDir = ResolvePath( SyncToolConfig.EndpointsPath );
		_endpointFiles = Directory.Exists( epDir )
			? Directory.GetFiles( epDir, "*.json" ).OrderBy( f => f ).ToArray()
			: Array.Empty<string>();

		var colDir = ResolvePath( SyncToolConfig.CollectionsPath );
		_collectionFiles = Directory.Exists( colDir )
			? Directory.GetFiles( colDir, "*.json" ).OrderBy( f => f ).ToArray()
			: Array.Empty<string>();

		var wfDir = ResolvePath( SyncToolConfig.WorkflowsPath );
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
		var hasIndicator = hasResult || state.RemoteDiffers || ( _hasCheckedRemote && state.Status != SyncStatus.Unknown );

		// Icon
		if ( hasResult )
		{
			var iconColor = state.SyncResult == "OK" ? Color.Green.WithAlpha( 0.8f ) : Color.Red.WithAlpha( 0.8f );
			Paint.SetPen( iconColor );
			Paint.SetDefaultFont( size: 9 );
			Paint.DrawText( new Rect( pad + 2, y, 18, rowH ), state.SyncResult == "OK" ? "✓" : "✗", TextFlag.Center );
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

		// Pull button — LEFT side (only if remote has changes to pull)
		if ( state.RemoteDiffers )
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
		if ( !string.IsNullOrEmpty( state.DiffSummary ) && ( state.RemoteDiffers || state.Status == SyncStatus.LocalOnly ) )
		{
			// Summary text
			var summaryColor = state.Status == SyncStatus.LocalOnly ? Color.Yellow.WithAlpha( 0.6f ) : Color.Orange.WithAlpha( 0.6f );
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( summaryColor );
			Paint.DrawText( new Rect( pad + 22, y, w - 80, 14 ), state.DiffSummary, TextFlag.LeftCenter );

			// Action buttons on the diff line
			if ( state.Status != SyncStatus.LocalOnly )
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
	//  Mouse
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
				break;
			}
		}
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		_mousePos = e.LocalPosition;
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
		Update();

		var diffs = 0;
		var localOnlyCount = 0;
		var remoteSlugs = new HashSet<string>();
		var remoteColNames = new HashSet<string>();

		// ── Check endpoints ──
		_remoteEndpoints = await SyncToolApi.GetEndpoints();
		if ( !_remoteEndpoints.HasValue )
		{
			_status = "Failed to fetch endpoints from server — check Base URL and credentials";
			_hasCheckedRemote = true;
			_busy = false;
			_busyItem = null;
			Update();
			return;
		}
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

					var localFile = _endpointFiles.FirstOrDefault( f => Path.GetFileNameWithoutExtension( f ) == slug );
					if ( localFile == null )
					{
						SetItemState( id, remoteDiffers: true, status: SyncStatus.RemoteOnly,
							diffSummary: "Remote only — not in local files",
							localJson: "", remoteJson: PrettyJson( JsonSerializer.Serialize( SyncToolTransforms.ServerEndpointToLocal( ep ) ) ) );
						diffs++;
					}
					else
					{
						var remoteLocal = SyncToolTransforms.ServerEndpointToLocal( ep );
						var remoteJson = JsonSerializer.Serialize( remoteLocal, new JsonSerializerOptions { WriteIndented = true } );
						var localJson = File.ReadAllText( localFile );
						var differs = NormalizeJson( remoteJson ) != NormalizeJson( localJson );

						if ( differs )
						{
							var epDiff = DiffEndpoint( localJson, remoteJson, slug );
							SetItemState( id, remoteDiffers: true, status: SyncStatus.Differs, diffSummary: epDiff,
								localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
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

		// Detect local-only endpoints (exist locally but not on server)
		foreach ( var file in _endpointFiles )
		{
			var slug = Path.GetFileNameWithoutExtension( file );
			if ( !remoteSlugs.Contains( slug ) )
			{
				var id = $"ep_{slug}";
				var localJson = File.ReadAllText( file );
				SetItemState( id, remoteDiffers: false, status: SyncStatus.LocalOnly,
					diffSummary: "Local only — not pushed to server",
					localJson: PrettyJson( localJson ), remoteJson: "" );
				localOnlyCount++;
			}
		}

		// ── Check collections ──
		_remoteCollections = await SyncToolApi.GetCollections();
		if ( !_remoteCollections.HasValue )
		{
			_status = "Failed to fetch collections from server — check Base URL and credentials";
			_hasCheckedRemote = true;
			_busy = false;
			_busyItem = null;
			Update();
			return;
		}
		{
			var remoteCollections = SyncToolTransforms.ServerToCollections( _remoteCollections.Value );
			foreach ( var (colName, remoteLocal) in remoteCollections )
			{
				remoteColNames.Add( colName );
				var id = $"col_{colName}";
				var remoteJson = JsonSerializer.Serialize( remoteLocal, new JsonSerializerOptions { WriteIndented = true } );
				var localFile = _collectionFiles.FirstOrDefault( f => Path.GetFileNameWithoutExtension( f ) == colName );

				if ( localFile == null )
				{
					SetItemState( id, remoteDiffers: true, status: SyncStatus.RemoteOnly,
						diffSummary: "Remote only — no local file",
						localJson: "", remoteJson: PrettyJson( remoteJson ) );
					diffs++;
				}
				else
				{
					var localJson = File.ReadAllText( localFile );
					var differs = NormalizeJson( remoteJson ) != NormalizeJson( localJson );

					if ( differs )
					{
						var colDiff = DiffCollectionSchema( localJson, remoteJson );
						SetItemState( id, remoteDiffers: true, status: SyncStatus.Differs, diffSummary: colDiff,
							localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
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

		// Detect local-only collections
		foreach ( var file in _collectionFiles )
		{
			var colName = Path.GetFileNameWithoutExtension( file );
			if ( !remoteColNames.Contains( colName ) )
			{
				var id = $"col_{colName}";
				var localJson = File.ReadAllText( file );
				SetItemState( id, remoteDiffers: false, status: SyncStatus.LocalOnly,
					diffSummary: "Local only — not pushed to server",
					localJson: PrettyJson( localJson ), remoteJson: "" );
				localOnlyCount++;
			}
		}

		// ── Check workflows ──
		var remoteWfIds = new HashSet<string>();
		_remoteWorkflows = await SyncToolApi.GetWorkflows();
		if ( _remoteWorkflows.HasValue )
		{
			var workflows = SyncToolTransforms.ServerToWorkflows( _remoteWorkflows.Value );
			foreach ( var (wfId, remoteLocal) in workflows )
			{
				remoteWfIds.Add( wfId );
				var id = $"wf_{wfId}";
				var remoteJson = JsonSerializer.Serialize( remoteLocal, new JsonSerializerOptions { WriteIndented = true } );
				var localFile = _workflowFiles.FirstOrDefault( f => Path.GetFileNameWithoutExtension( f ) == wfId );

				if ( localFile == null )
				{
					SetItemState( id, remoteDiffers: true, status: SyncStatus.RemoteOnly,
						diffSummary: "Remote only — no local file",
						localJson: "", remoteJson: PrettyJson( remoteJson ) );
					diffs++;
				}
				else
				{
					var localJson = File.ReadAllText( localFile );
					var differs = NormalizeJson( remoteJson ) != NormalizeJson( localJson );

					if ( differs )
					{
						SetItemState( id, remoteDiffers: true, status: SyncStatus.Differs, diffSummary: "Content differs",
							localJson: PrettyJson( localJson ), remoteJson: PrettyJson( remoteJson ) );
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

		// Detect local-only workflows
		foreach ( var file in _workflowFiles )
		{
			var wfId = Path.GetFileNameWithoutExtension( file );
			if ( !remoteWfIds.Contains( wfId ) )
			{
				var id = $"wf_{wfId}";
				var localJson = File.ReadAllText( file );
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
		_busy = false;
		_busyItem = null;
		Update();
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
		foreach ( var k in _items.Keys.ToList() ) SetItemState( k, result: null );
		Update();

		// Push all endpoints
		if ( _endpointFiles.Length > 0 )
		{
			_busyItem = "push_ep_all";
			Update();
			var ok = await DoPushAllEndpoints();
			foreach ( var f in _endpointFiles )
			{
				var slug = Path.GetFileNameWithoutExtension( f );
				SetItemState( $"ep_{slug}", result: ok ? "OK" : "FAIL",
					remoteDiffers: false, diffSummary: "", status: ok ? SyncStatus.InSync : null );
			}
		}

		// Push collections
		if ( _collectionFiles.Length > 0 )
		{
			_busyItem = "push_col";
			Update();
			var ok = await DoPushCollections();
			foreach ( var f in _collectionFiles )
				SetItemState( $"col_{Path.GetFileNameWithoutExtension( f )}", result: ok ? "OK" : "FAIL",
					remoteDiffers: false, diffSummary: "", status: ok ? SyncStatus.InSync : null );
		}

		// Push workflows
		if ( _workflowFiles.Length > 0 )
		{
			_busyItem = "push_wf";
			Update();
			var ok = await DoPushAllWorkflows();
			foreach ( var f in _workflowFiles )
				SetItemState( $"wf_{Path.GetFileNameWithoutExtension( f )}", result: ok ? "OK" : "FAIL",
					remoteDiffers: false, diffSummary: "", status: ok ? SyncStatus.InSync : null );
		}

		// Invalidate cached remote data — next check will fetch fresh
		ClearAllRemoteDiffs();

		_remoteEndpoints = null;
		_remoteCollections = null;
		_remoteWorkflows = null;
		_hasCheckedRemote = false;

		var okCount = _items.Values.Count( s => s.SyncResult == "OK" );
		var failCount = _items.Values.Count( s => s.SyncResult == "FAIL" );
		_status = failCount == 0 ? $"All pushed ({okCount} resources)" : $"Done: {okCount} OK, {failCount} failed";
		_busy = false;
		_busyItem = null;
		Update();
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

		bool ok;
		if ( id.StartsWith( "ep_" ) )
		{
			var slug = id[3..];
			ok = await DoPushSingleEndpointMerged( slug );
		}
		else if ( id.StartsWith( "col_" ) )
		{
			ok = await DoPushCollections();
		}
		else if ( id.StartsWith( "wf_" ) )
		{
			ok = await DoPushAllWorkflows();
		}
		else
		{
			ok = false;
		}

		// Clear diff state for this item only — keep other items' state intact
		SetItemState( id, result: ok ? "OK" : "FAIL", remoteDiffers: false, diffSummary: "",
			status: ok ? SyncStatus.InSync : null );
		_status = ok ? $"Pushed {id}" : $"Push failed for {id}";
		_busy = false;
		_busyItem = null;
		Update();
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
			// Don't invalidate cached remote data — other items still need their state
		}
		else
		{
			SetItemState( id, result: "FAIL" );
		}

		_status = ok ? $"Pulled {id}" : $"Pull failed for {id}";
		_busy = false;
		_busyItem = null;
		Update();
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
				var dir = ResolvePath( SyncToolConfig.EndpointsPath );
				if ( !Directory.Exists( dir ) ) Directory.CreateDirectory( dir );

				var json = JsonSerializer.Serialize( localDict, new JsonSerializerOptions { WriteIndented = true } );
				File.WriteAllText( Path.Combine( dir, $"{slug}.json" ), json );
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
		_hasCheckedRemote = false;

		_remoteEndpoints = null;
		_remoteCollections = null;
		_remoteWorkflows = null;
		_status = SyncToolConfig.IsValid ? "Refreshed" : "Config invalid — check .env";
		Update();
	}
}
