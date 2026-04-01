using System;
using System.Collections.Generic;
using Sandbox;
using Editor;

/// <summary>
/// Settings window for Network Storage runtime behavior.
/// Configure multiplayer proxy mode and other runtime options.
///
/// Access via Editor menu → Network Storage → Settings.
/// </summary>
[Dock( "Editor", "Network Storage Settings", "settings" )]
public class SettingsWindow : DockWindow
{
	private bool _proxyEnabled;
	private bool _saved;
	private float _savedTimer;

	private List<ButtonRect> _buttons = new();
	private Vector2 _mousePos;
	private float _scrollY;
	private float _contentHeight;

	private struct ButtonRect
	{
		public Rect Rect;
		public string Id;
		public Action OnClick;
	}

	public SettingsWindow()
	{
		Title = "Network Storage Settings";
		Size = new Vector2( 520, 480 );
		MinimumSize = new Vector2( 400, 320 );

		SyncToolConfig.Load();
		_proxyEnabled = SyncToolConfig.ProxyEnabled;
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

		// ── Multiplayer Proxy Section ──
		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( pad, y, w, 20 ), "Multiplayer Auth Proxy", TextFlag.LeftCenter );
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
			Paint.DrawText( checkboxRect, "✓", TextFlag.Center );
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

		// ── Save button ──
		var btnW = 120f;
		var btnH = 30f;
		var btnRect = new Rect( pad, y, btnW, btnH );
		var btnScreenRect = new Rect( pad, y + _scrollY, btnW, btnH );
		var btnHovered = btnScreenRect.IsInside( _mousePos );

		var isDirty = _proxyEnabled != SyncToolConfig.ProxyEnabled;
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
				_saved = true;
				_savedTimer = 2f;
				Update();
			}
		} );

		y += btnH + 20;

		// ── Explanation box ──
		var lineH = 15f;
		var boxPad = 12f;
		var boxTop = y;
		var boxH = lineH * 9 + boxPad * 2;
		var boxRect = new Rect( pad, y, w, boxH );

		Paint.SetBrush( Color.White.WithAlpha( 0.03f ) );
		Paint.SetPen( Color.White.WithAlpha( 0.08f ) );
		Paint.DrawRect( boxRect, 4 );

		var textX = pad + boxPad;
		var textW = w - boxPad * 2;
		y += boxPad;

		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( textX, y, textW, lineH ), "What this does:", TextFlag.LeftCenter );
		y += lineH + 2;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
		DrawWrappedText( ref y, textX, textW, lineH,
			"Steam auth tokens are tied to the Steam session. When running " +
			"multiple editor instances on the same machine (same Steam account), " +
			"only the primary session can generate valid tokens — so editor " +
			"clients fail to authenticate directly." );
		y += 4;
		DrawWrappedText( ref y, textX, textW, lineH,
			"When enabled, non-host clients route API calls through the host " +
			"via RPC. The host authenticates with its own valid token and calls " +
			"the backend on the client's behalf using a signed proxy request." );
		y += 8;

		Paint.SetDefaultFont( size: 9, weight: 600 );
		var recColor = _proxyEnabled ? Color.Cyan : Color.Yellow;
		Paint.SetPen( recColor.WithAlpha( 0.8f ) );
		Paint.DrawText( new Rect( textX, y, textW, lineH ), _proxyEnabled ? "Status: Enabled" : "Status: Disabled", TextFlag.LeftCenter );
		y += lineH + 2;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.45f ) );
		DrawWrappedText( ref y, textX, textW, lineH,
			"Enable for local development and editor testing. In production, " +
			"each player uses their own Steam account and can authenticate " +
			"directly — you can safely disable this then." );

		y = boxTop + boxH + 12;

		DrawSeparator( ref y, w, pad );

		// ── Security note ──
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( Color.Yellow.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad, y, w, 18 ), "Security Notes", TextFlag.LeftCenter );
		y += 24;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.45f ) );
		DrawWrappedText( ref y, pad, w, lineH,
			"When proxy is enabled, the host can make API calls on behalf of " +
			"any connected player. This is safe for P2P games where the host " +
			"is already trusted (they control the game session)." );
		y += 4;
		DrawWrappedText( ref y, pad, w, lineH,
			"The backend verifies both the host's auth token (Facepunch-validated) " +
			"and an HMAC signature scoped to your project and endpoint, preventing " +
			"cross-server replay attacks." );

		y += 16;

		// ── Integration hint ──
		DrawSeparator( ref y, w, pad );

		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( 0.6f ) );
		Paint.DrawText( new Rect( pad, y, w, 18 ), "Integration", TextFlag.LeftCenter );
		y += 22;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
		DrawWrappedText( ref y, pad, w, lineH,
			"Add NetworkStorageProxyComponent to each player's GameObject via " +
			"PlayerSpawner. This component registers the proxy delegates that " +
			"route non-host requests through the host via Broadcast RPC." );

		y += 20;

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

	protected override void OnWheel( WheelEvent e )
	{
		base.OnWheel( e );

		var maxScroll = Math.Max( 0f, _contentHeight - Height );
		_scrollY = Math.Clamp( _scrollY - e.Delta * 30f, 0f, maxScroll );
		Update();
	}
}
