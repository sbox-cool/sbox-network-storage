using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;
using Editor;

/// <summary>
/// Side-by-side diff view window. Shows local (left) vs remote (right) JSON
/// with changed/added/removed lines highlighted. Scrollable via keyboard and drag.
/// </summary>
public class DiffViewWindow : DockWindow
{
	private readonly string _name;
	private readonly string[] _localLines;
	private readonly string[] _remoteLines;
	private readonly DiffLine[] _diffLines;
	private float _scroll;
	private Vector2 _mousePos;
	private bool _dragging;
	private float _dragStartY;
	private float _dragStartScroll;

	private enum LineKind { Same, Changed, Added, Removed }

	private struct DiffLine
	{
		public int? LocalNum;
		public int? RemoteNum;
		public string LocalText;
		public string RemoteText;
		public LineKind Kind;
	}

	private const float LineH = 16f;
	private const float HeaderH = 90f;

	public DiffViewWindow( string name, string localJson, string remoteJson )
	{
		_name = name;
		Title = $"Diff — {name}";
		Size = new Vector2( 820, 600 );
		MinimumSize = new Vector2( 600, 300 );

		// Normalize both to sorted keys so key ordering doesn't cause false diffs
		_localLines = NormalizePretty( localJson ).Split( '\n' );
		_remoteLines = NormalizePretty( remoteJson ).Split( '\n' );
		_diffLines = BuildDiff( _localLines, _remoteLines );
	}

	/// <summary>
	/// Re-serialize JSON with sorted keys and consistent indentation
	/// so the line-by-line diff only shows actual value changes.
	/// </summary>
	private static string NormalizePretty( string json )
	{
		if ( string.IsNullOrEmpty( json ) ) return "";
		try
		{
			var el = JsonSerializer.Deserialize<JsonElement>( json );
			var sorted = SortElement( el );
			return JsonSerializer.Serialize( sorted, new JsonSerializerOptions { WriteIndented = true } );
		}
		catch { return json; }
	}

	private static object SortElement( JsonElement el )
	{
		switch ( el.ValueKind )
		{
			case JsonValueKind.Object:
				var dict = new SortedDictionary<string, object>();
				foreach ( var prop in el.EnumerateObject() )
					dict[prop.Name] = SortElement( prop.Value );
				return dict;
			case JsonValueKind.Array:
				var arr = new List<object>();
				foreach ( var item in el.EnumerateArray() )
					arr.Add( SortElement( item ) );
				return arr;
			case JsonValueKind.String: return el.GetString();
			case JsonValueKind.Number: return el.TryGetInt64( out var l ) ? (object)l : el.GetDouble();
			case JsonValueKind.True: return true;
			case JsonValueKind.False: return false;
			default: return null;
		}
	}

	private float MaxScroll => Math.Max( 0, _diffLines.Length * LineH - ( Height - HeaderH - 20 ) );

	protected override void OnPaint()
	{
		base.OnPaint();

		var y = 38f;
		var pad = 12f;
		var w = Width - pad * 2;
		var halfW = ( w - 8 ) / 2;
		var gutterW = 32f;

		// Header
		Paint.SetDefaultFont( size: 12, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w, 20 ), $"Diff — {_name}", TextFlag.LeftCenter );
		y += 28;

		// Column headers
		Paint.SetDefaultFont( size: 9, weight: 600 );
		Paint.SetPen( Color.Cyan.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad, y, halfW, 16 ), "LOCAL (your files)", TextFlag.LeftCenter );
		Paint.SetPen( Color.Orange.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad + halfW + 8, y, halfW, 16 ), "REMOTE (project dashboard)", TextFlag.LeftCenter );
		y += 20;

		// Stats bar
		var added = _diffLines.Count( d => d.Kind == LineKind.Added );
		var removed = _diffLines.Count( d => d.Kind == LineKind.Removed );
		var changed = _diffLines.Count( d => d.Kind == LineKind.Changed );

		Paint.SetDefaultFont( size: 8 );
		var statsX = pad;
		if ( changed > 0 ) { Paint.SetPen( Color.Yellow.WithAlpha( 0.7f ) ); Paint.DrawText( new Rect( statsX, y, 80, 14 ), $"~{changed} changed", TextFlag.LeftCenter ); statsX += 75; }
		if ( added > 0 ) { Paint.SetPen( Color.Green.WithAlpha( 0.7f ) ); Paint.DrawText( new Rect( statsX, y, 70, 14 ), $"+{added} added", TextFlag.LeftCenter ); statsX += 65; }
		if ( removed > 0 ) { Paint.SetPen( Color.Red.WithAlpha( 0.7f ) ); Paint.DrawText( new Rect( statsX, y, 80, 14 ), $"-{removed} removed", TextFlag.LeftCenter ); }
		if ( added == 0 && removed == 0 && changed == 0 ) { Paint.SetPen( Color.Green.WithAlpha( 0.7f ) ); Paint.DrawText( new Rect( statsX, y, 200, 14 ), "Files are identical", TextFlag.LeftCenter ); }

		// Scroll info
		var totalLines = _diffLines.Length;
		var visibleStart = (int)( _scroll / LineH ) + 1;
		var visibleLines = (int)( ( Height - HeaderH - 20 ) / LineH );
		var visibleEnd = Math.Min( visibleStart + visibleLines, totalLines );
		Paint.SetPen( Color.White.WithAlpha( 0.3f ) );
		Paint.DrawText( new Rect( pad + w - 120, y, 120, 14 ), $"Lines {visibleStart}-{visibleEnd} of {totalLines}", TextFlag.RightCenter );

		y += 18;

		// Separator
		Paint.SetPen( Color.White.WithAlpha( 0.1f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 4;

		// Clip area for diff lines
		var clipTop = y;
		var clipH = Height - clipTop - 10;
		var startLine = Math.Max( 0, (int)( _scroll / LineH ) );
		var maxVisibleLines = (int)( clipH / LineH );

		Paint.SetDefaultFont( size: 9 );

		for ( int i = startLine; i < _diffLines.Length && i < startLine + maxVisibleLines; i++ )
		{
			var line = _diffLines[i];
			var ly = clipTop + ( i - startLine ) * LineH;

			// Background highlight
			var bgColor = line.Kind switch
			{
				LineKind.Changed => Color.Yellow.WithAlpha( 0.06f ),
				LineKind.Added => Color.Green.WithAlpha( 0.06f ),
				LineKind.Removed => Color.Red.WithAlpha( 0.06f ),
				_ => Color.Transparent
			};

			if ( bgColor.a > 0 )
			{
				Paint.SetBrush( bgColor );
				Paint.SetPen( Color.Transparent );
				Paint.DrawRect( new Rect( pad, ly, w, LineH ) );
			}

			// Center divider
			Paint.SetPen( Color.White.WithAlpha( 0.06f ) );
			var divX = pad + halfW + 4;
			Paint.DrawLine( new Vector2( divX, ly ), new Vector2( divX, ly + LineH ) );

			// Line numbers
			Paint.SetDefaultFont( size: 8 );
			Paint.SetPen( Color.White.WithAlpha( 0.2f ) );
			if ( line.LocalNum.HasValue )
				Paint.DrawText( new Rect( pad, ly, gutterW - 4, LineH ), line.LocalNum.Value.ToString(), TextFlag.RightCenter );
			if ( line.RemoteNum.HasValue )
				Paint.DrawText( new Rect( pad + halfW + 8, ly, gutterW - 4, LineH ), line.RemoteNum.Value.ToString(), TextFlag.RightCenter );

			// Content
			Paint.SetDefaultFont( size: 9 );

			var localColor = line.Kind switch
			{
				LineKind.Removed => Color.Red.WithAlpha( 0.8f ),
				LineKind.Changed => Color.Yellow.WithAlpha( 0.7f ),
				_ => Color.White.WithAlpha( 0.6f )
			};
			Paint.SetPen( localColor );
			var localPrefix = line.Kind == LineKind.Removed ? "- " : line.Kind == LineKind.Changed ? "~ " : "  ";
			Paint.DrawText( new Rect( pad + gutterW, ly, halfW - gutterW, LineH ),
				localPrefix + ( line.LocalText ?? "" ), TextFlag.LeftCenter );

			var remoteColor = line.Kind switch
			{
				LineKind.Added => Color.Green.WithAlpha( 0.8f ),
				LineKind.Changed => Color.Yellow.WithAlpha( 0.7f ),
				_ => Color.White.WithAlpha( 0.6f )
			};
			Paint.SetPen( remoteColor );
			var remotePrefix = line.Kind == LineKind.Added ? "+ " : line.Kind == LineKind.Changed ? "~ " : "  ";
			Paint.DrawText( new Rect( pad + halfW + 8 + gutterW, ly, halfW - gutterW, LineH ),
				remotePrefix + ( line.RemoteText ?? "" ), TextFlag.LeftCenter );
		}

		// Scrollbar track
		if ( MaxScroll > 0 )
		{
			var trackX = pad + w - 4;
			var trackH = clipH;
			var thumbH = Math.Max( 20, trackH * ( clipH / ( _diffLines.Length * LineH ) ) );
			var thumbY = clipTop + ( _scroll / MaxScroll ) * ( trackH - thumbH );

			Paint.SetBrush( Color.White.WithAlpha( 0.05f ) );
			Paint.SetPen( Color.Transparent );
			Paint.DrawRect( new Rect( trackX, clipTop, 4, trackH ) );

			Paint.SetBrush( Color.White.WithAlpha( 0.2f ) );
			Paint.DrawRect( new Rect( trackX, thumbY, 4, thumbH ), 2 );
		}
	}

	// ── Scrolling via keyboard, mouse wheel, and drag ──

	protected override void OnWheel( WheelEvent e )
	{
		var direction = e.Delta > 0 ? -1 : 1;
		_scroll = Math.Clamp( _scroll + direction * LineH * 3, 0, MaxScroll );
		_dragging = false;
		Update();
		e.Accept();
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		_dragging = false;
		var handled = true;
		switch ( e.Key )
		{
			case KeyCode.Up: _scroll = Math.Max( 0, _scroll - LineH ); break;
			case KeyCode.Down: _scroll = Math.Min( MaxScroll, _scroll + LineH ); break;
			case KeyCode.PageUp: _scroll = Math.Max( 0, _scroll - LineH * 20 ); break;
			case KeyCode.PageDown: _scroll = Math.Min( MaxScroll, _scroll + LineH * 20 ); break;
			case KeyCode.Home: _scroll = 0; break;
			case KeyCode.End: _scroll = MaxScroll; break;
			default: handled = false; break;
		}

		if ( handled ) Update();
		else base.OnKeyPress( e );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );
		if ( _dragging )
		{
			_dragging = false;
		}
		else
		{
			_dragging = true;
			_dragStartY = e.LocalPosition.y;
			_dragStartScroll = _scroll;
		}
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		if ( _dragging )
		{
			var delta = _dragStartY - e.LocalPosition.y;
			_scroll = Math.Clamp( _dragStartScroll + delta, 0, MaxScroll );
		}
		_mousePos = e.LocalPosition;
		Update();
	}

	// No OnMouseRelease on DockWindow — stop drag on next click or key
	private void StopDrag() => _dragging = false;

	[Button( "Page Up", Icon = "arrow_upward" )]
	public void PageUp()
	{
		_scroll = Math.Max( 0, _scroll - LineH * 20 );
		Update();
	}

	[Button( "Page Down", Icon = "arrow_downward" )]
	public void PageDown()
	{
		_scroll = Math.Min( MaxScroll, _scroll + LineH * 20 );
		Update();
	}

	// ── Diff algorithm ──

	private static DiffLine[] BuildDiff( string[] localLines, string[] remoteLines )
	{
		var result = new List<DiffLine>();
		var maxLines = Math.Max( localLines.Length, remoteLines.Length );

		for ( int i = 0; i < maxLines; i++ )
		{
			var hasLocal = i < localLines.Length;
			var hasRemote = i < remoteLines.Length;
			var localText = hasLocal ? localLines[i].TrimEnd( '\r' ) : null;
			var remoteText = hasRemote ? remoteLines[i].TrimEnd( '\r' ) : null;

			if ( hasLocal && hasRemote )
			{
				result.Add( new DiffLine
				{
					LocalNum = i + 1, RemoteNum = i + 1,
					LocalText = localText, RemoteText = remoteText,
					Kind = localText == remoteText ? LineKind.Same : LineKind.Changed
				} );
			}
			else if ( hasLocal )
			{
				result.Add( new DiffLine
				{
					LocalNum = i + 1, RemoteNum = null,
					LocalText = localText, RemoteText = null,
					Kind = LineKind.Removed
				} );
			}
			else
			{
				result.Add( new DiffLine
				{
					LocalNum = null, RemoteNum = i + 1,
					LocalText = null, RemoteText = remoteText,
					Kind = LineKind.Added
				} );
			}
		}

		return result.ToArray();
	}
}
