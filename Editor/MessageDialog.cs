using System;
using Sandbox;
using Editor;

/// <summary>
/// Simple modal dialog for surfacing tool errors to the user.
/// </summary>
public class MessageDialog : DockWindow
{
	private readonly string _title;
	private readonly string _message;
	private readonly string _detail;
	private readonly string _buttonLabel;
	private Vector2 _mousePos;
	private Rect _closeRect;

	private MessageDialog( string title, string message, string detail, string buttonLabel )
	{
		_title = title;
		_message = message;
		_detail = detail;
		_buttonLabel = string.IsNullOrWhiteSpace( buttonLabel ) ? "Close" : buttonLabel;
		Title = title;
		Size = new Vector2( 560, string.IsNullOrWhiteSpace( detail ) ? 200 : 340 );
	}

	public static void Show( string title, string message, string detail = null, string buttonLabel = "Close" )
	{
		var dialog = new MessageDialog( title, message, detail, buttonLabel );
		dialog.Show();
	}

	protected override void OnPaint()
	{
		base.OnPaint();

		var pad = 20f;
		var w = Width - pad * 2;
		var y = 20f;

		Paint.SetDefaultFont( size: 13, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w, 22 ), _title, TextFlag.LeftCenter );
		y += 34;

		y = DrawWrappedText( _message, pad, y, w, 10, Color.White.WithAlpha( 0.9f ), 17f );

		if ( !string.IsNullOrWhiteSpace( _detail ) )
		{
			y += 8;
			Paint.SetBrush( Color.Black.WithAlpha( 0.18f ) );
			Paint.SetPen( Color.Red.WithAlpha( 0.2f ) );
			var detailRect = new Rect( pad, y, w, Height - y - 72 );
			Paint.DrawRect( detailRect, 4 );
			DrawWrappedText( _detail, pad + 10, y + 10, w - 20, 9, Color.Orange.WithAlpha( 0.85f ), 15f, detailRect.Bottom - 12 );
		}

		var btnW = 120f;
		var btnH = 32f;
		var btnRect = new Rect( Width - pad - btnW, Height - pad - btnH, btnW, btnH );
		var hovered = btnRect.IsInside( _mousePos );

		Paint.SetBrush( Color.White.WithAlpha( hovered ? 0.12f : 0.06f ) );
		Paint.SetPen( Color.White.WithAlpha( hovered ? 0.35f : 0.2f ) );
		Paint.DrawRect( btnRect, 4 );
		Paint.SetDefaultFont( size: 11, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( hovered ? 0.95f : 0.75f ) );
		Paint.DrawText( btnRect, _buttonLabel, TextFlag.Center );

		_closeRect = btnRect;
	}

	private float DrawWrappedText( string text, float x, float y, float width, int fontSize, Color color, float lineHeight, float maxY = float.MaxValue )
	{
		Paint.SetDefaultFont( size: fontSize );
		Paint.SetPen( color );

		foreach ( var paragraph in text.Replace( "\r", "" ).Split( '\n' ) )
		{
			if ( y > maxY )
				return y;

			if ( string.IsNullOrWhiteSpace( paragraph ) )
			{
				y += lineHeight;
				continue;
			}

			var words = paragraph.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
			var line = "";
			foreach ( var word in words )
			{
				var candidate = string.IsNullOrEmpty( line ) ? word : $"{line} {word}";
				if ( candidate.Length * 6.2f > width && !string.IsNullOrEmpty( line ) )
				{
					Paint.DrawText( new Rect( x, y, width, lineHeight ), line, TextFlag.LeftTop );
					y += lineHeight;
					if ( y > maxY )
						return y;
					line = word;
				}
				else
				{
					line = candidate;
				}
			}

			if ( !string.IsNullOrEmpty( line ) )
			{
				Paint.DrawText( new Rect( x, y, width, lineHeight ), line, TextFlag.LeftTop );
				y += lineHeight;
			}
		}

		return y;
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
