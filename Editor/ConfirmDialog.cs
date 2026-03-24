using System;
using Sandbox;
using Editor;

/// <summary>
/// Modal-style confirmation dialog with Overwrite/Cancel buttons.
/// Use ConfirmDialog.Show(title, message, onConfirm) to display.
/// </summary>
public class ConfirmDialog : DockWindow
{
	private readonly string _title;
	private readonly string _message;
	private readonly string _detail;
	private readonly Action _onConfirm;
	private Vector2 _mousePos;

	private ConfirmDialog( string title, string message, string detail, Action onConfirm )
	{
		_title = title;
		_message = message;
		_detail = detail;
		_onConfirm = onConfirm;
		Title = title;
		Size = new Vector2( 420, string.IsNullOrEmpty( detail ) ? 180 : 240 );
	}

	/// <summary>
	/// Show a confirmation dialog with Overwrite and Cancel buttons.
	/// </summary>
	public static void Show( string title, string message, Action onConfirm, string detail = null )
	{
		var dialog = new ConfirmDialog( title, message, detail, onConfirm );
		dialog.Show();
	}

	protected override void OnPaint()
	{
		base.OnPaint();

		var pad = 20f;
		var w = Width - pad * 2;
		var y = 20f;

		// Title
		Paint.SetDefaultFont( size: 13, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w, 22 ), _title, TextFlag.LeftCenter );
		y += 32;

		// Message
		Paint.SetDefaultFont( size: 10 );
		Paint.SetPen( Color.White.WithAlpha( 0.85f ) );

		// Word-wrap the message manually
		var words = _message.Split( ' ' );
		var line = "";
		foreach ( var word in words )
		{
			var test = string.IsNullOrEmpty( line ) ? word : $"{line} {word}";
			if ( test.Length * 6.5f > w && !string.IsNullOrEmpty( line ) )
			{
				Paint.DrawText( new Rect( pad, y, w, 16 ), line, TextFlag.LeftCenter );
				y += 17;
				line = word;
			}
			else
			{
				line = test;
			}
		}
		if ( !string.IsNullOrEmpty( line ) )
		{
			Paint.DrawText( new Rect( pad, y, w, 16 ), line, TextFlag.LeftCenter );
			y += 20;
		}

		// Detail (smaller, dimmer)
		if ( !string.IsNullOrEmpty( _detail ) )
		{
			y += 4;
			Paint.SetDefaultFont( size: 9 );
			Paint.SetPen( Color.Orange.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad, y, w, 14 ), _detail, TextFlag.LeftCenter );
			y += 22;
		}

		y += 8;

		// Buttons row
		var btnH = 32f;
		var btnW = ( w - 12 ) / 2;

		// Cancel button (left)
		var cancelRect = new Rect( pad, y, btnW, btnH );
		var cancelHovered = cancelRect.IsInside( _mousePos );
		Paint.SetBrush( Color.White.WithAlpha( cancelHovered ? 0.1f : 0.04f ) );
		Paint.SetPen( Color.White.WithAlpha( cancelHovered ? 0.3f : 0.15f ) );
		Paint.DrawRect( cancelRect, 4 );
		Paint.SetDefaultFont( size: 11, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( cancelHovered ? 0.9f : 0.6f ) );
		Paint.DrawText( cancelRect, "Cancel", TextFlag.Center );

		// Overwrite button (right)
		var overwriteRect = new Rect( pad + btnW + 12, y, btnW, btnH );
		var overwriteHovered = overwriteRect.IsInside( _mousePos );
		Paint.SetBrush( Color.Red.WithAlpha( overwriteHovered ? 0.25f : 0.12f ) );
		Paint.SetPen( Color.Red.WithAlpha( overwriteHovered ? 0.6f : 0.3f ) );
		Paint.DrawRect( overwriteRect, 4 );
		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.Red.WithAlpha( overwriteHovered ? 1f : 0.8f ) );
		Paint.DrawText( overwriteRect, "Overwrite", TextFlag.Center );

		_cancelRect = cancelRect;
		_overwriteRect = overwriteRect;
	}

	private Rect _cancelRect;
	private Rect _overwriteRect;

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( _cancelRect.IsInside( e.LocalPosition ) )
		{
			Close();
		}
		else if ( _overwriteRect.IsInside( e.LocalPosition ) )
		{
			Close();
			_onConfirm?.Invoke();
		}
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		_mousePos = e.LocalPosition;
		Update();
	}
}
