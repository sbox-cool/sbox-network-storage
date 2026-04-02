using Sandbox;
using Editor;

/// <summary>
/// Info popup explaining the Multiplayer Auth Proxy feature.
/// </summary>
public class ProxyInfoDialog : DockWindow
{
	private Vector2 _mousePos;
	private Rect _closeRect;

	public ProxyInfoDialog()
	{
		Title = "Multiplayer Auth Proxy";
		Size = new Vector2( 460, 380 );
	}

	public static void Open()
	{
		var dialog = new ProxyInfoDialog();
		dialog.Show();
	}

	protected override void OnPaint()
	{
		base.OnPaint();

		var pad = 20f;
		var w = Width - pad * 2;
		var y = 20f;
		var lineH = 15f;

		// Title
		Paint.SetDefaultFont( size: 13, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w, 22 ), "Multiplayer Auth Proxy", TextFlag.LeftCenter );
		y += 32;

		// What this does
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad, y, w, lineH ), "What this does:", TextFlag.LeftCenter );
		y += lineH + 2;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
		DrawWrappedText( ref y, pad, w, lineH,
			"Steam auth tokens are tied to the Steam session. When running " +
			"multiple editor instances on the same machine (same Steam account), " +
			"only the primary session can generate valid tokens \u2014 so editor " +
			"clients fail to authenticate directly." );
		y += 4;
		DrawWrappedText( ref y, pad, w, lineH,
			"When enabled, non-host clients route API calls through the host " +
			"via RPC. The host authenticates with its own valid token and calls " +
			"the backend on the client's behalf using a signed proxy request." );
		y += 4;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.45f ) );
		DrawWrappedText( ref y, pad, w, lineH,
			"Enable for local development and editor testing. In production, " +
			"each player uses their own Steam account and can authenticate " +
			"directly \u2014 you can safely disable this then." );

		y += 12;

		// Security notes
		Paint.SetPen( Color.White.WithAlpha( 0.08f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 10;

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

		y += 12;

		// Integration hint
		Paint.SetPen( Color.White.WithAlpha( 0.08f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 10;

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

		y += 16;

		// Close button
		var btnW = 100f;
		var btnH = 30f;
		var btnRect = new Rect( ( Width - btnW ) / 2, y, btnW, btnH );
		var btnHovered = btnRect.IsInside( _mousePos );

		Paint.SetBrush( Color.White.WithAlpha( btnHovered ? 0.12f : 0.05f ) );
		Paint.SetPen( Color.White.WithAlpha( btnHovered ? 0.4f : 0.2f ) );
		Paint.DrawRect( btnRect, 4 );
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( btnHovered ? 0.9f : 0.6f ) );
		Paint.DrawText( btnRect, "Close", TextFlag.Center );

		_closeRect = btnRect;
	}

	private void DrawWrappedText( ref float y, float x, float w, float lineH, string text )
	{
		var charsPerLine = System.Math.Max( 40, (int)( w / 6.2f ) );
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

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );
		if ( _closeRect.IsInside( e.LocalPosition ) )
			Close();
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		_mousePos = e.LocalPosition;
		Update();
	}
}
