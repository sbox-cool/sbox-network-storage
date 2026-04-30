using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Editor;

/// <summary>
/// Settings window for Network Storage runtime behavior.
/// Configure multiplayer proxy mode, sync mappings, and other runtime options.
///
/// Access via Editor menu → Network Storage → Settings.
/// </summary>
[Dock( "Editor", "Network Storage Settings", "settings" )]
public class SettingsWindow : DockWindow
{
	private bool _proxyEnabled;
	private SyncToolConfig.SourceExportMode _sourceExportMode;
	private bool _saved;
	private float _savedTimer;
	private string _generateStatus = "";

	// Logging config state
	private bool _logRequests;
	private bool _logResponses;
	private bool _logTokens;
	private bool _logProxy;
	private bool _logErrors;
	private bool _logConfig;

	private List<ButtonRect> _buttons = new();
	private Vector2 _mousePos;
	private float _scrollY;
	private float _contentHeight;

	// ── Sync mapping editor state ──
	private List<MappingRow> _mappingRows = new();

	private class MappingRow
	{
		public LineEdit CsFileInput;
		public LineEdit CollectionInput;
		public LineEdit DescriptionInput;
	}

	private struct ButtonRect
	{
		public Rect Rect;
		public string Id;
		public Action OnClick;
	}

	public SettingsWindow()
	{
		Title = "Network Storage Settings";
		Size = new Vector2( 520, 700 );
		MinimumSize = new Vector2( 400, 400 );

		SyncToolConfig.Load();
		_proxyEnabled = SyncToolConfig.ProxyEnabled;
		_sourceExportMode = SyncToolConfig.SourceExport;
		RebuildMappingWidgets();
		LoadLogConfig();
	}

	private void LoadLogConfig()
	{
		_logRequests = NetworkStorageLogConfig.LogRequests;
		_logResponses = NetworkStorageLogConfig.LogResponses;
		_logTokens = NetworkStorageLogConfig.LogTokens;
		_logProxy = NetworkStorageLogConfig.LogProxy;
		_logErrors = NetworkStorageLogConfig.LogErrors;
		_logConfig = NetworkStorageLogConfig.LogConfig;
	}

	private void ApplyLogConfig()
	{
		NetworkStorageLogConfig.LogRequests = _logRequests;
		NetworkStorageLogConfig.LogResponses = _logResponses;
		NetworkStorageLogConfig.LogTokens = _logTokens;
		NetworkStorageLogConfig.LogProxy = _logProxy;
		NetworkStorageLogConfig.LogErrors = _logErrors;
		NetworkStorageLogConfig.LogConfig = _logConfig;
	}

	[Menu( "Editor", "Network Storage/Settings" )]
	public static void OpenWindow()
	{
		var window = new SettingsWindow();
		window.Show();
	}

	// ──────────────────────────────────────────────────────
	//  Rendering
	// ──────────────────────────────────────────────────────

	protected override void OnPaint()
	{
		base.OnPaint();
		_buttons.Clear();

		var pad = 20f;
		var w = Width - pad * 2;
		var visibleH = Height;

		// Scrollable content — offset all y by -_scrollY
		var y = 38f - _scrollY;

		// ── Title ──
		Paint.SetDefaultFont( size: 14, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w, 24 ), "Network Storage Settings", TextFlag.LeftCenter );
		y += 32;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
		Paint.DrawText( new Rect( pad, y, w, 14 ), "Runtime behavior — these settings ship with your game", TextFlag.LeftCenter );
		y += 22;

		DrawSeparator( ref y, w, pad );

		var lineH = 15f;

		// ── Sync Mappings (C# → Collection) — first ──
		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( pad, y, w, 20 ), "Data Source Mappings", TextFlag.LeftCenter );
		y += 26;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.45f ) );
		DrawWrappedText( ref y, pad, w, lineH + 2,
			"Map C# data files or directories to collection YAML source. " +
			"The Sync Tool's Generate button parses C# records and " +
			"writes the collection source automatically." );
		y += 12;

		// ── Mapping rows with LineEdit widgets ──
		var inputH = 22f;
		var removeBtnW = 20f;
		var gap = 4f;

		for ( int i = 0; i < _mappingRows.Count; i++ )
		{
			var row = _mappingRows[i];

			// Row number + remove button
			Paint.SetDefaultFont( size: 9, weight: 600 );
			Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
			Paint.DrawText( new Rect( pad, y, 16, inputH ), $"{i + 1}.", TextFlag.RightCenter );

			// C# source path
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
			Paint.DrawText( new Rect( pad + 22, y - 12, w * 0.55f, 12 ), "C# source (file or directory)", TextFlag.LeftCenter );
			PositionInput( row.CsFileInput, pad + 22, y, w * 0.55f - gap, inputH );

			// Collection name
			Paint.DrawText( new Rect( pad + 22 + w * 0.55f, y - 12, w * 0.35f, 12 ), "Collection name", TextFlag.LeftCenter );
			PositionInput( row.CollectionInput, pad + 22 + w * 0.55f, y, w * 0.35f - removeBtnW - gap * 2, inputH );

			// Remove button
			var removeBtnRect = new Rect( pad + w - removeBtnW, y, removeBtnW, inputH );
			var removeScreenRect = new Rect( pad + w - removeBtnW, y + _scrollY, removeBtnW, inputH );
			var removeHovered = removeScreenRect.IsInside( _mousePos );

			Paint.SetBrush( Color.Red.WithAlpha( removeHovered ? 0.2f : 0.08f ) );
			Paint.SetPen( Color.Red.WithAlpha( removeHovered ? 0.6f : 0.25f ) );
			Paint.DrawRect( removeBtnRect, 3 );
			Paint.SetDefaultFont( size: 11, weight: 700 );
			Paint.SetPen( Color.Red.WithAlpha( removeHovered ? 1f : 0.6f ) );
			Paint.DrawText( removeBtnRect, "×", TextFlag.Center );

			var capturedIndex = i;
			_buttons.Add( new ButtonRect
			{
				Rect = removeScreenRect,
				Id = $"remove_mapping_{i}",
				OnClick = () => RemoveMapping( capturedIndex )
			} );

			y += inputH + gap;

			// Description field
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
			Paint.DrawText( new Rect( pad + 22, y - 1, 80, 12 ), "Description", TextFlag.LeftCenter );
			PositionInput( row.DescriptionInput, pad + 22 + 64, y, w - 22 - 64 - removeBtnW - gap, inputH - 2 );

			y += inputH + 8;
		}

		// Add mapping button
		var addBtnW = 130f;
		var addBtnH = 26f;
		var addBtnRect = new Rect( pad, y, addBtnW, addBtnH );
		var addBtnScreenRect = new Rect( pad, y + _scrollY, addBtnW, addBtnH );
		var addBtnHovered = addBtnScreenRect.IsInside( _mousePos );

		Paint.SetBrush( Color.Green.WithAlpha( addBtnHovered ? 0.15f : 0.06f ) );
		Paint.SetPen( Color.Green.WithAlpha( addBtnHovered ? 0.5f : 0.2f ) );
		Paint.DrawRect( addBtnRect, 4 );
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.Green.WithAlpha( addBtnHovered ? 0.9f : 0.6f ) );
		Paint.DrawText( addBtnRect, "+ Add Mapping", TextFlag.Center );

		_buttons.Add( new ButtonRect
		{
			Rect = addBtnScreenRect,
			Id = "add_mapping",
			OnClick = AddMapping
		} );

		// Save mappings button (next to add)
		var saveMappingsBtnW = 130f;
		var saveMappingsBtnRect = new Rect( pad + addBtnW + 8, y, saveMappingsBtnW, addBtnH );
		var saveMappingsScreenRect = new Rect( pad + addBtnW + 8, y + _scrollY, saveMappingsBtnW, addBtnH );
		var saveMappingsHovered = saveMappingsScreenRect.IsInside( _mousePos );

		var mappingsDirty = AreMappingsDirty();
		var saveMappingsColor = mappingsDirty ? Color.Cyan : Color.White;
		Paint.SetBrush( saveMappingsColor.WithAlpha( saveMappingsHovered ? 0.15f : 0.06f ) );
		Paint.SetPen( saveMappingsColor.WithAlpha( saveMappingsHovered ? 0.5f : 0.2f ) );
		Paint.DrawRect( saveMappingsBtnRect, 4 );
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( saveMappingsColor.WithAlpha( saveMappingsHovered ? 0.9f : 0.6f ) );
		Paint.DrawText( saveMappingsBtnRect, "Save Mappings", TextFlag.Center );

		_buttons.Add( new ButtonRect
		{
			Rect = saveMappingsScreenRect,
			Id = "save_mappings",
			OnClick = SaveMappings
		} );

		y += addBtnH + 12;

		DrawSeparator( ref y, w, pad );

		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( pad, y, w, 20 ), "Pull / Export Format", TextFlag.LeftCenter );
		y += 26;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.45f ) );
		DrawWrappedText( ref y, pad, w, lineH + 2,
			"The Sync Tool writes YAML source only. Legacy JSON export and local JSON fallback are no longer supported." );
		y += 8;

		DrawReadonlyStatusRow( ref y, pad, w, "Export Format", "YAML Source Only", true );
		y += 6;

		DrawSeparator( ref y, w, pad );

		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( pad, y, w, 20 ), "Project Security", TextFlag.LeftCenter );
		y += 26;

		DrawReadonlyStatusRow( ref y, pad, w, "Auth Sessions", SyncToolConfig.AuthSessionsLabel, SyncToolConfig.EnableAuthSessions );
		DrawReadonlyStatusRow( ref y, pad, w, "Encrypted Requests", SyncToolConfig.EncryptedRequestsLabel, SyncToolConfig.EnableEncryptedRequests );

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.45f ) );
		DrawWrappedText( ref y, pad, w, lineH + 2,
			"Read-only project settings. Go to Sync Tool and pull changes to resync these settings." );
		y += 12;

		// ── Multiplayer Auth Proxy ──
		DrawSeparator( ref y, w, pad );

		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( pad, y, w, 20 ), "Multiplayer Auth Proxy", TextFlag.LeftCenter );

		// Info button (next to title)
		var infoBtnW = 40f;
		var infoBtnH = 20f;
		var infoBtnX = pad + w - infoBtnW;
		var infoBtnRect = new Rect( infoBtnX, y, infoBtnW, infoBtnH );
		var infoBtnScreenRect = new Rect( infoBtnX, y + _scrollY, infoBtnW, infoBtnH );
		var infoBtnHovered = infoBtnScreenRect.IsInside( _mousePos );

		Paint.SetBrush( Color.White.WithAlpha( infoBtnHovered ? 0.12f : 0.05f ) );
		Paint.SetPen( Color.White.WithAlpha( infoBtnHovered ? 0.4f : 0.2f ) );
		Paint.DrawRect( infoBtnRect, 3 );
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( infoBtnHovered ? 0.9f : 0.55f ) );
		Paint.DrawText( infoBtnRect, "Info", TextFlag.Center );

		_buttons.Add( new ButtonRect
		{
			Rect = infoBtnScreenRect,
			Id = "proxy_info",
			OnClick = ProxyInfoDialog.Open
		} );

		y += 26;

		// Checkbox + label
		var checkboxSize = 22f;
		var checkboxRect = new Rect( pad, y, checkboxSize, checkboxSize );
		var checkScreenRect = new Rect( pad, y + _scrollY, checkboxSize, checkboxSize );
		var checkHovered = checkScreenRect.IsInside( _mousePos );

		var checkColor = _proxyEnabled ? Color.Cyan : Color.White;
		Paint.SetBrush( checkColor.WithAlpha( _proxyEnabled ? 0.15f : checkHovered ? 0.08f : 0.04f ) );
		Paint.SetPen( checkColor.WithAlpha( _proxyEnabled ? 0.5f : checkHovered ? 0.25f : 0.15f ) );
		Paint.DrawRect( checkboxRect, 3 );

		if ( _proxyEnabled )
		{
			Paint.SetDefaultFont( size: 14, weight: 700 );
			Paint.SetPen( Color.Cyan );
			Paint.DrawText( checkboxRect, "\u2713", TextFlag.Center );
		}

		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( 0.85f ) );
		Paint.DrawText( new Rect( pad + checkboxSize + 10, y, w - checkboxSize - 10, checkboxSize ), "Host proxies API calls for non-host clients", TextFlag.LeftCenter );

		_buttons.Add( new ButtonRect
		{
			Rect = checkScreenRect,
			Id = "proxy_toggle",
			OnClick = () =>
			{
				_proxyEnabled = !_proxyEnabled;
				_saved = false;
				Update();
			}
		} );

		y += checkboxSize + 14;

		DrawProxySecurityNotice( ref y, pad, w, lineH );

		// Save button
		var btnW = 120f;
		var btnH = 30f;
		var btnRect = new Rect( pad, y, btnW, btnH );
		var btnScreenRect = new Rect( pad, y + _scrollY, btnW, btnH );
		var btnHovered = btnScreenRect.IsInside( _mousePos );

		var isDirty = _proxyEnabled != SyncToolConfig.ProxyEnabled
			|| _sourceExportMode != SyncToolConfig.SourceExport;
		var btnColor = _saved ? Color.Green : ( isDirty ? Color.Cyan : Color.White );
		Paint.SetBrush( btnColor.WithAlpha( btnHovered ? 0.2f : 0.1f ) );
		Paint.SetPen( btnColor.WithAlpha( btnHovered ? 0.6f : 0.35f ) );
		Paint.DrawRect( btnRect, 4 );

		Paint.SetDefaultFont( size: 9, weight: 700 );
		Paint.SetPen( btnColor.WithAlpha( 0.9f ) );
		var btnLabel = _saved ? "Saved!" : "Save Settings";
		Paint.DrawText( btnRect, btnLabel, TextFlag.Center );

		_buttons.Add( new ButtonRect
		{
			Rect = btnScreenRect,
			Id = "save",
			OnClick = () =>
			{
				SyncToolConfig.SetProxyEnabled( _proxyEnabled );
				SyncToolConfig.SetSourceExportMode( _sourceExportMode );
				_saved = true;
				_savedTimer = 2f;
				Update();
			}
		} );

		y += btnH + 20;

		DrawSeparator( ref y, w, pad );

		// ── Generate Code ──
		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( pad, y, w, 20 ), "Generate Code", TextFlag.LeftCenter );
		y += 26;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.45f ) );
		DrawWrappedText( ref y, pad, w, lineH + 2,
			"Reads your local endpoint definitions and collection schemas, then writes strongly-typed C# classes to Code/Data/NetworkStorage/. " +
			"These generated files (prefixed autoGenerated_) give you type-safe access to collection fields, endpoint slugs, and workflow IDs in game code. " +
			"Runs automatically after every Push — use this button if you have pulled changes from remote and want to regenerate without pushing." );
		y += 12;

		var genBtnW = 140f;
		var genBtnH = 28f;
		var genBtnRect = new Rect( pad, y, genBtnW, genBtnH );
		var genBtnScreenRect = new Rect( pad, y + _scrollY, genBtnW, genBtnH );
		var genBtnHovered = genBtnScreenRect.IsInside( _mousePos );

		Paint.SetBrush( Color.Cyan.WithAlpha( genBtnHovered ? 0.15f : 0.06f ) );
		Paint.SetPen( Color.Cyan.WithAlpha( genBtnHovered ? 0.5f : 0.2f ) );
		Paint.DrawRect( genBtnRect, 4 );
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.Cyan.WithAlpha( genBtnHovered ? 0.9f : 0.6f ) );
		Paint.DrawText( genBtnRect, "Generate Code", TextFlag.Center );

		_buttons.Add( new ButtonRect
		{
			Rect = genBtnScreenRect,
			Id = "generate_code",
			OnClick = () =>
			{
				SyncToolConfig.Load();
				var count = CodeGenerator.Generate();
				_generateStatus = $"✓ {count} files written to Code/Data/NetworkStorage/";
				Update();
			}
		} );

		if ( !string.IsNullOrEmpty( _generateStatus ) )
		{
			Paint.SetDefaultFont( size: 9 );
			Paint.SetPen( Color.Green.WithAlpha( 0.8f ) );
			Paint.DrawText( new Rect( pad + genBtnW + 10, y, w - genBtnW - 10, genBtnH ), _generateStatus, TextFlag.LeftCenter );
		}

		y += genBtnH + 20;

		// ── Console Logging ──
		DrawSeparator( ref y, w, pad );

		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( pad, y, w, 20 ), "Console Logging", TextFlag.LeftCenter );
		y += 26;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.45f ) );
		DrawWrappedText( ref y, pad, w, lineH + 2,
			"Control which [Network Storage] log messages appear in the console. " +
			"Changes apply immediately at runtime (not persisted across sessions)." );
		y += 8;

		// Enable All / Disable All buttons
		var logBtnW = 90f;
		var logBtnH = 24f;
		var logBtnGap = 8f;

		var enableAllRect = new Rect( pad, y, logBtnW, logBtnH );
		var enableAllScreenRect = new Rect( pad, y + _scrollY, logBtnW, logBtnH );
		var enableAllHovered = enableAllScreenRect.IsInside( _mousePos );

		Paint.SetBrush( Color.Green.WithAlpha( enableAllHovered ? 0.15f : 0.06f ) );
		Paint.SetPen( Color.Green.WithAlpha( enableAllHovered ? 0.5f : 0.2f ) );
		Paint.DrawRect( enableAllRect, 4 );
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.Green.WithAlpha( enableAllHovered ? 0.9f : 0.6f ) );
		Paint.DrawText( enableAllRect, "Enable All", TextFlag.Center );

		_buttons.Add( new ButtonRect
		{
			Rect = enableAllScreenRect,
			Id = "log_enable_all",
			OnClick = () =>
			{
				_logRequests = _logResponses = _logTokens = _logProxy = _logErrors = _logConfig = true;
				ApplyLogConfig();
				Update();
			}
		} );

		var disableAllRect = new Rect( pad + logBtnW + logBtnGap, y, logBtnW, logBtnH );
		var disableAllScreenRect = new Rect( pad + logBtnW + logBtnGap, y + _scrollY, logBtnW, logBtnH );
		var disableAllHovered = disableAllScreenRect.IsInside( _mousePos );

		Paint.SetBrush( Color.Red.WithAlpha( disableAllHovered ? 0.15f : 0.06f ) );
		Paint.SetPen( Color.Red.WithAlpha( disableAllHovered ? 0.5f : 0.2f ) );
		Paint.DrawRect( disableAllRect, 4 );
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.Red.WithAlpha( disableAllHovered ? 0.9f : 0.6f ) );
		Paint.DrawText( disableAllRect, "Disable All", TextFlag.Center );

		_buttons.Add( new ButtonRect
		{
			Rect = disableAllScreenRect,
			Id = "log_disable_all",
			OnClick = () =>
			{
				_logRequests = _logResponses = _logTokens = _logProxy = _logErrors = _logConfig = false;
				ApplyLogConfig();
				Update();
			}
		} );

		y += logBtnH + 12;

		// Log category checkboxes (2 columns)
		var colW = (w - 10) / 2f;
		var checkSize = 18f;
		var checkRowH = 26f;

		DrawLogCheckbox( ref y, pad, colW, checkSize, checkRowH, "Requests", ref _logRequests );
		DrawLogCheckbox( ref y, pad + colW + 10, colW, checkSize, checkRowH, "Responses", ref _logResponses, sameRow: true );
		y += checkRowH;

		DrawLogCheckbox( ref y, pad, colW, checkSize, checkRowH, "Tokens", ref _logTokens );
		DrawLogCheckbox( ref y, pad + colW + 10, colW, checkSize, checkRowH, "Proxy", ref _logProxy, sameRow: true );
		y += checkRowH;

		DrawLogCheckbox( ref y, pad, colW, checkSize, checkRowH, "Errors", ref _logErrors );
		DrawLogCheckbox( ref y, pad + colW + 10, colW, checkSize, checkRowH, "Config", ref _logConfig, sameRow: true );
		y += checkRowH + 8;

		// Bottom padding so content isn't flush against the edge
		y += 100;

		// Track content height for scroll clamping
		_contentHeight = y + _scrollY;

		// ── Scrollbar ──
		var scrollbarW = 4f;
		var scrollbarX = Width - scrollbarW - 4f;
		var scrollRatio = visibleH / Math.Max( _contentHeight, visibleH );
		if ( scrollRatio < 1f )
		{
			var trackH = visibleH - 8f;
			var thumbH = Math.Max( 30f, trackH * scrollRatio );
			var thumbY = 4f + ( _scrollY / ( _contentHeight - visibleH ) ) * ( trackH - thumbH );

			Paint.SetBrush( Color.White.WithAlpha( 0.08f ) );
			Paint.SetPen( Color.Transparent );
			Paint.DrawRect( new Rect( scrollbarX, 4f, scrollbarW, trackH ), scrollbarW / 2 );

			Paint.SetBrush( Color.White.WithAlpha( 0.2f ) );
			Paint.DrawRect( new Rect( scrollbarX, thumbY, scrollbarW, thumbH ), scrollbarW / 2 );
		}

		// Fade "Saved!" label after timer
		if ( _saved && _savedTimer > 0 )
		{
			_savedTimer -= RealTime.Delta;
			if ( _savedTimer <= 0 )
			{
				_saved = false;
				_savedTimer = 0;
			}
			Update();
		}
	}

	private void DrawWrappedText( ref float y, float x, float w, float lineH, string text )
	{
		var charsPerLine = Math.Max( 40, (int)( w / 6.2f ) );
		var words = text.Split( ' ' );
		var line = "";

		foreach ( var word in words )
		{
			if ( line.Length + word.Length + 1 > charsPerLine && line.Length > 0 )
			{
				Paint.DrawText( new Rect( x, y, w, lineH ), line, TextFlag.LeftCenter );
				y += lineH;
				line = word;
			}
			else
			{
				line = line.Length > 0 ? $"{line} {word}" : word;
			}
		}

		if ( line.Length > 0 )
		{
			Paint.DrawText( new Rect( x, y, w, lineH ), line, TextFlag.LeftCenter );
			y += lineH;
		}
	}

	private void DrawSeparator( ref float y, float w, float pad )
	{
		Paint.SetPen( Color.White.WithAlpha( 0.08f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 10;
	}

	private void DrawSourceExportButton( SyncToolConfig.SourceExportMode mode, string label, Rect rect, Rect screenRect )
	{
		var selected = _sourceExportMode == mode;
		var hovered = screenRect.IsInside( _mousePos );
		var color = selected ? Color.Cyan : Color.White;

		Paint.SetBrush( color.WithAlpha( selected ? 0.16f : hovered ? 0.09f : 0.04f ) );
		Paint.SetPen( color.WithAlpha( selected ? 0.55f : hovered ? 0.35f : 0.18f ) );
		Paint.DrawRect( rect, 4 );
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( color.WithAlpha( selected || hovered ? 0.9f : 0.6f ) );
		Paint.DrawText( rect, label, TextFlag.Center );

		_buttons.Add( new ButtonRect
		{
			Rect = screenRect,
			Id = $"source_export_{mode}",
			OnClick = () =>
			{
				_sourceExportMode = mode;
				_saved = false;
				Update();
			}
		} );
	}

	// ──────────────────────────────────────────────────────
	//  Sync mapping helpers
	// ──────────────────────────────────────────────────────

	private void DrawReadonlyStatusRow( ref float y, float pad, float w, string label, string value, bool enabled )
	{
		var rowH = 24f;
		Paint.SetBrush( Color.White.WithAlpha( 0.035f ) );
		Paint.SetPen( Color.White.WithAlpha( 0.08f ) );
		Paint.DrawRect( new Rect( pad, y, w, rowH ), 4 );

		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( 0.82f ) );
		Paint.DrawText( new Rect( pad + 10, y, w * 0.55f, rowH ), label, TextFlag.LeftCenter );

		var color = enabled ? Color.Green : Color.White;
		Paint.SetPen( color.WithAlpha( enabled ? 0.9f : 0.55f ) );
		Paint.DrawText( new Rect( pad + w * 0.55f, y, w * 0.45f - 10, rowH ), value, TextFlag.RightCenter );
		y += rowH + 6;
	}

	private void DrawProxySecurityNotice( ref float y, float pad, float w, float lineH )
	{
		var noticeY = y;
		var noticeH = 138f;
		Paint.SetBrush( Color.Orange.WithAlpha( 0.055f ) );
		Paint.SetPen( Color.Orange.WithAlpha( 0.22f ) );
		Paint.DrawRect( new Rect( pad, noticeY, w, noticeH ), 4 );

		var textY = noticeY + 10f;
		var textX = pad + 12f;
		var textW = w - 24f;
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.Orange.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( textX, textY, textW, 14 ), "Encryption is recommended when proxy mode is enabled.", TextFlag.LeftCenter );
		textY += 18f;

		Paint.SetDefaultFont( size: 8 );
		Paint.SetPen( Color.White.WithAlpha( 0.62f ) );
		DrawWrappedText( ref textY, textX, textW, lineH + 1,
			"Proxy mode routes non-host Network Storage calls through the host. Without encrypted requests, that host can inspect endpoint payloads." );
		DrawWrappedText( ref textY, textX, textW, lineH + 1,
			"Encryption hides the payload, but it does not stop replay by itself. A hostile host could resend the same encrypted auth-session request, like a Drop $500 call." );
		DrawWrappedText( ref textY, textX, textW, lineH + 1,
			"Encrypted requests should include a one-use key such as unixSeconds_random6+. The backend rejects keys that are too old or already used." );

		y = noticeY + noticeH + 12f;
	}

	private void PositionInput( LineEdit input, float x, float y, float w, float h )
	{
		input.Position = new Vector2( x, y );
		input.Size = new Vector2( w, h );
	}

	private LineEdit CreateLineEdit( string value, string placeholder )
	{
		var input = new LineEdit( this );
		input.Text = value ?? "";
		input.PlaceholderText = placeholder;
		input.Visible = true;
		return input;
	}

	private void RebuildMappingWidgets()
	{
		// Destroy old widgets
		foreach ( var row in _mappingRows )
		{
			row.CsFileInput?.Destroy();
			row.CollectionInput?.Destroy();
			row.DescriptionInput?.Destroy();
		}
		_mappingRows.Clear();

		// Create widgets for current mappings
		foreach ( var mapping in SyncToolConfig.SyncMappings )
		{
			_mappingRows.Add( new MappingRow
			{
				CsFileInput = CreateLineEdit( mapping.CsFile, "Code/Fishing" ),
				CollectionInput = CreateLineEdit( mapping.Collection, "game_values" ),
				DescriptionInput = CreateLineEdit( mapping.Description, "Optional description" )
			} );
		}
	}

	private void AddMapping()
	{
		_mappingRows.Add( new MappingRow
		{
			CsFileInput = CreateLineEdit( "", "Code/Fishing" ),
			CollectionInput = CreateLineEdit( "", "game_values" ),
			DescriptionInput = CreateLineEdit( "", "Optional description" )
		} );
		Update();
	}

	private void RemoveMapping( int index )
	{
		if ( index < 0 || index >= _mappingRows.Count ) return;
		var row = _mappingRows[index];
		row.CsFileInput?.Destroy();
		row.CollectionInput?.Destroy();
		row.DescriptionInput?.Destroy();
		_mappingRows.RemoveAt( index );
		Update();
	}

	private void SaveMappings()
	{
		var mappings = _mappingRows
			.Where( r => !string.IsNullOrWhiteSpace( r.CsFileInput?.Text ) && !string.IsNullOrWhiteSpace( r.CollectionInput?.Text ) )
			.Select( r => new SyncToolConfig.SyncMapping
			{
				CsFile = r.CsFileInput.Text.Trim(),
				Collection = r.CollectionInput.Text.Trim(),
				Description = r.DescriptionInput?.Text?.Trim() ?? ""
			} )
			.ToList();

		SyncToolConfig.SaveSyncMappings( mappings );
		_saved = true;
		_savedTimer = 2f;
		Update();
	}

	private bool AreMappingsDirty()
	{
		var current = SyncToolConfig.SyncMappings;
		if ( _mappingRows.Count != current.Count ) return true;
		for ( int i = 0; i < _mappingRows.Count; i++ )
		{
			if ( i >= current.Count ) return true;
			if ( _mappingRows[i].CsFileInput?.Text != current[i].CsFile ) return true;
			if ( _mappingRows[i].CollectionInput?.Text != current[i].Collection ) return true;
			if ( ( _mappingRows[i].DescriptionInput?.Text ?? "" ) != ( current[i].Description ?? "" ) ) return true;
		}
		return false;
	}

	private void DrawLogCheckbox( ref float y, float x, float colW, float checkSize, float rowH, string label, ref bool value, bool sameRow = false )
	{
		var checkRect = new Rect( x, y, checkSize, checkSize );
		var checkScreenRect = new Rect( x, y + _scrollY, checkSize, checkSize );
		var checkHovered = checkScreenRect.IsInside( _mousePos );

		var checkColor = value ? Color.Cyan : Color.White;
		Paint.SetBrush( checkColor.WithAlpha( value ? 0.15f : checkHovered ? 0.08f : 0.04f ) );
		Paint.SetPen( checkColor.WithAlpha( value ? 0.5f : checkHovered ? 0.25f : 0.15f ) );
		Paint.DrawRect( checkRect, 3 );

		if ( value )
		{
			Paint.SetDefaultFont( size: 12, weight: 700 );
			Paint.SetPen( Color.Cyan );
			Paint.DrawText( checkRect, "✓", TextFlag.Center );
		}

		Paint.SetDefaultFont( size: 9, weight: 500 );
		Paint.SetPen( Color.White.WithAlpha( 0.8f ) );
		Paint.DrawText( new Rect( x + checkSize + 8, y, colW - checkSize - 8, checkSize ), label, TextFlag.LeftCenter );

		var fieldName = label;
		_buttons.Add( new ButtonRect
		{
			Rect = checkScreenRect,
			Id = $"log_{label.ToLower()}",
			OnClick = () =>
			{
				ToggleLogField( fieldName );
				ApplyLogConfig();
				Update();
			}
		} );
	}

	private void ToggleLogField( string fieldName )
	{
		switch ( fieldName )
		{
			case "Requests": _logRequests = !_logRequests; break;
			case "Responses": _logResponses = !_logResponses; break;
			case "Tokens": _logTokens = !_logTokens; break;
			case "Proxy": _logProxy = !_logProxy; break;
			case "Errors": _logErrors = !_logErrors; break;
			case "Config": _logConfig = !_logConfig; break;
		}
	}

	// ──────────────────────────────────────────────────────
	//  Input handling
	// ──────────────────────────────────────────────────────

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

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
		base.OnMouseWheel( e );

		var maxScroll = Math.Max( 0f, _contentHeight - Height );
		_scrollY = Math.Clamp( _scrollY - e.Delta * 30f, 0f, maxScroll );
		Update();
	}
}
