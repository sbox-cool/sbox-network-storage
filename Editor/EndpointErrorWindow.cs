using System;
using System.IO;
using System.Text.Json;
using Sandbox;
using Editor;

/// <summary>
/// Modal window that displays endpoint validation errors with full response JSON.
/// Shows when network-storage endpoints fail validation.
/// </summary>
public class EndpointErrorWindow : DockWindow
{
	private readonly string _slug;
	private readonly string _code;
	private readonly string _message;
	private readonly string _rawJson;
	private Vector2 _mousePos;
	private Rect _copyRect;
	private Rect _closeRect;
	private float _scrollY;
	private float _contentHeight;
	private bool _copied;
	private float _copiedTimer;

	private EndpointErrorWindow( string slug, string code, string message, JsonElement raw )
	{
		_slug = slug ?? "unknown";
		_code = code ?? "UNKNOWN";
		_message = message ?? "";
		_rawJson = FormatJson( raw );

		Title = $"Endpoint Error: {_slug}";
		Size = new Vector2( 620, 480 );
		MinimumSize = new Vector2( 500, 300 );
	}

	/// <summary>Show the error window with endpoint failure details.</summary>
	public static void Show( string slug, string code, string message, JsonElement raw )
	{
		var window = new EndpointErrorWindow( slug, code, message, raw );
		window.Show();
	}

	/// <summary>Show the error window from just slug and raw JSON (extracts code/message).</summary>
	public static void Show( string slug, JsonElement raw )
	{
		var code = "UNKNOWN";
		var message = "";

		if ( raw.TryGetProperty( "error", out var err ) )
		{
			if ( err.ValueKind == JsonValueKind.Object )
			{
				code = err.TryGetProperty( "code", out var c ) && c.ValueKind == JsonValueKind.String
					? c.GetString() ?? "UNKNOWN"
					: "UNKNOWN";
				message = err.TryGetProperty( "message", out var m ) && m.ValueKind == JsonValueKind.String
					? m.GetString() ?? ""
					: "";
			}
			else if ( err.ValueKind == JsonValueKind.String )
			{
				code = err.GetString() ?? "UNKNOWN";
			}
		}

		if ( string.IsNullOrEmpty( message ) && raw.TryGetProperty( "message", out var topMsg ) && topMsg.ValueKind == JsonValueKind.String )
			message = topMsg.GetString() ?? "";

		Show( slug, code, message, raw );
	}

	private static string FormatJson( JsonElement raw )
	{
		if ( raw.ValueKind == JsonValueKind.Undefined )
			return "(no response data)";

		try
		{
			return JsonSerializer.Serialize( raw, new JsonSerializerOptions { WriteIndented = true } );
		}
		catch
		{
			return raw.GetRawText();
		}
	}

	protected override void OnPaint()
	{
		base.OnPaint();

		var pad = 20f;
		var w = Width - pad * 2;
		var y = 20f;

		// Title
		Paint.SetDefaultFont( size: 14, weight: 700 );
		Paint.SetPen( new Color( 1f, 0.3f, 0.3f ) );
		Paint.DrawText( new Rect( pad, y, w, 24 ), "⚠ Endpoint Error", TextFlag.LeftCenter );
		y += 32;

		// Endpoint info
		Paint.SetDefaultFont( size: 11, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
		Paint.DrawText( new Rect( pad, y, 80, 18 ), "Endpoint:", TextFlag.LeftCenter );
		Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( pad + 80, y, w - 80, 18 ), _slug, TextFlag.LeftCenter );
		y += 22;

		Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
		Paint.DrawText( new Rect( pad, y, 80, 18 ), "Code:", TextFlag.LeftCenter );
		Paint.SetPen( new Color( 1f, 0.5f, 0.3f ) );
		Paint.DrawText( new Rect( pad + 80, y, w - 80, 18 ), _code, TextFlag.LeftCenter );
		y += 22;

		if ( !string.IsNullOrWhiteSpace( _message ) )
		{
			Paint.SetPen( Color.White.WithAlpha( 0.5f ) );
			Paint.DrawText( new Rect( pad, y, 80, 18 ), "Message:", TextFlag.LeftCenter );
			Paint.SetPen( Color.White.WithAlpha( 0.85f ) );
			var msgLines = WrapText( _message, w - 80, 10 );
			foreach ( var line in msgLines )
			{
				Paint.DrawText( new Rect( pad + 80, y, w - 80, 18 ), line, TextFlag.LeftCenter );
				y += 16;
			}
			y += 4;
		}

		y += 8;

		// Response header
		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.6f ) );
		Paint.DrawText( new Rect( pad, y, w, 18 ), "Response:", TextFlag.LeftCenter );
		y += 22;

		// JSON box
		var jsonBoxTop = y;
		var jsonBoxHeight = Height - y - 70;
		var jsonBoxRect = new Rect( pad, jsonBoxTop, w, jsonBoxHeight );

		Paint.SetBrush( Color.Black.WithAlpha( 0.3f ) );
		Paint.SetPen( Color.White.WithAlpha( 0.1f ) );
		Paint.DrawRect( jsonBoxRect, 4 );

		// Clip and draw JSON
		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( new Color( 1f, 0.6f, 0.3f ).WithAlpha( 0.9f ) );

		var jsonY = jsonBoxTop + 8 - _scrollY;
		var lineHeight = 14f;
		var lines = _rawJson.Split( '\n' );
		foreach ( var line in lines )
		{
			if ( jsonY >= jsonBoxTop - lineHeight && jsonY < jsonBoxTop + jsonBoxHeight )
			{
				Paint.DrawText( new Rect( pad + 10, jsonY, w - 20, lineHeight ), line, TextFlag.LeftCenter );
			}
			jsonY += lineHeight;
		}
		_contentHeight = lines.Length * lineHeight + 16;

		// Buttons
		y = Height - pad - 32;
		var btnW = 100f;
		var btnH = 32f;

		// Copy button
		_copyRect = new Rect( pad, y, btnW, btnH );
		var copyHovered = _copyRect.IsInside( _mousePos );
		Paint.SetBrush( Color.White.WithAlpha( copyHovered ? 0.12f : 0.06f ) );
		Paint.SetPen( Color.White.WithAlpha( copyHovered ? 0.35f : 0.2f ) );
		Paint.DrawRect( _copyRect, 4 );
		Paint.SetDefaultFont( size: 11, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( copyHovered ? 0.95f : 0.75f ) );
		Paint.DrawText( _copyRect, _copied ? "Copied!" : "📋 Copy", TextFlag.Center );

		// Close button
		_closeRect = new Rect( Width - pad - btnW, y, btnW, btnH );
		var closeHovered = _closeRect.IsInside( _mousePos );
		Paint.SetBrush( Color.White.WithAlpha( closeHovered ? 0.12f : 0.06f ) );
		Paint.SetPen( Color.White.WithAlpha( closeHovered ? 0.35f : 0.2f ) );
		Paint.DrawRect( _closeRect, 4 );
		Paint.SetPen( Color.White.WithAlpha( closeHovered ? 0.95f : 0.75f ) );
		Paint.DrawText( _closeRect, "Dismiss", TextFlag.Center );

		// Reset copied state after a bit
		if ( _copied )
		{
			_copiedTimer += 0.016f;
			if ( _copiedTimer > 2f )
			{
				_copied = false;
				_copiedTimer = 0f;
			}
		}
	}

	private static string[] WrapText( string text, float width, int fontSize )
	{
		var result = new System.Collections.Generic.List<string>();
		var charWidth = fontSize * 0.6f;
		var maxChars = (int)(width / charWidth);

		foreach ( var paragraph in text.Split( '\n' ) )
		{
			if ( paragraph.Length <= maxChars )
			{
				result.Add( paragraph );
				continue;
			}

			var remaining = paragraph;
			while ( remaining.Length > maxChars )
			{
				var breakAt = remaining.LastIndexOf( ' ', maxChars );
				if ( breakAt <= 0 ) breakAt = maxChars;
				result.Add( remaining[..breakAt] );
				remaining = remaining[breakAt..].TrimStart();
			}
			if ( remaining.Length > 0 )
				result.Add( remaining );
		}

		return result.ToArray();
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( _copyRect.IsInside( e.LocalPosition ) )
		{
			var copyText = $"[{_slug}] {_code}: {_message}\n\n{_rawJson}";
			var tmpPath = Path.Combine( Path.GetTempPath(), "ns_endpoint_error.json" );
			File.WriteAllText( tmpPath, copyText );
			EditorUtility.OpenFile( tmpPath );
			_copied = true;
			_copiedTimer = 0f;
			Update();
		}

		if ( _closeRect.IsInside( e.LocalPosition ) )
			Close();
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
		_scrollY = Math.Clamp( _scrollY - e.Delta * 30, 0, Math.Max( 0, _contentHeight - 200 ) );
		Update();
	}
}
