#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using Sandbox;
using Editor;

public class PullPreviewWindow : DockWindow
{
	private readonly string _name;
	private readonly string _localText;
	private readonly string _remoteText;
	private readonly string _warning;
	private readonly Action _apply;
	private Vector2 _mousePos;
	private Rect _applyRect;
	private Rect _cancelRect;
	private Rect _copyRect;
	private float _scroll;

	public PullPreviewWindow( string name, string localText, string remoteText, string warning, Action apply )
	{
		_name = name;
		_localText = localText ?? "";
		_remoteText = remoteText ?? "";
		_warning = warning ?? "";
		_apply = apply;
		Title = $"Pull Preview - {name}";
		Size = new Vector2( 820, 620 );
		MinimumSize = new Vector2( 620, 380 );
		MouseTracking = true;
	}

	protected override void OnPaint()
	{
		base.OnPaint();
		var pad = 14f;
		var y = 16f;
		var w = Width - pad * 2;
		var half = (w - 8) / 2;

		Paint.SetDefaultFont( size: 13, weight: 800 );
		Paint.SetPen( Color.Cyan.WithAlpha( 0.95f ) );
		Paint.DrawText( new Rect( pad, y, w, 22 ), $"Pull remote YAML: {_name}", TextFlag.LeftCenter );
		y += 26;
		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( string.IsNullOrWhiteSpace( _warning ) ? Color.White.WithAlpha( 0.58f ) : Color.Yellow.WithAlpha( 0.9f ) );
		Paint.DrawText( new Rect( pad, y, w, 16 ), string.IsNullOrWhiteSpace( _warning ) ? "Review the diff before applying remote to local." : _warning, TextFlag.LeftCenter );
		y += 24;

		Paint.SetDefaultFont( size: 9, weight: 700 );
		Paint.SetPen( Color.White.WithAlpha( 0.55f ) );
		Paint.DrawText( new Rect( pad, y, half, 16 ), "LOCAL", TextFlag.LeftCenter );
		Paint.DrawText( new Rect( pad + half + 8, y, half, 16 ), "REMOTE", TextFlag.LeftCenter );
		y += 18;

		var linesL = _localText.Replace( "\r\n", "\n" ).Split( '\n' );
		var linesR = _remoteText.Replace( "\r\n", "\n" ).Split( '\n' );
		var max = Math.Max( linesL.Length, linesR.Length );
		var clipTop = y;
		var clipH = Height - clipTop - 62;
		var start = Math.Max( 0, (int)(_scroll / 15) );
		var visible = Math.Max( 1, (int)(clipH / 15) );
		Paint.SetDefaultFont( size: 8 );
		for ( var i = start; i < max && i < start + visible; i++ )
		{
			var ly = clipTop + (i - start) * 15;
			var l = i < linesL.Length ? linesL[i] : "";
			var r = i < linesR.Length ? linesR[i] : "";
			var changed = l != r;
			if ( changed )
			{
				Paint.SetBrush( Color.Yellow.WithAlpha( 0.05f ) );
				Paint.SetPen( Color.Transparent );
				Paint.DrawRect( new Rect( pad, ly, w, 15 ) );
			}
			Paint.SetPen( changed ? Color.Yellow.WithAlpha( 0.75f ) : Color.White.WithAlpha( 0.55f ) );
			Paint.DrawText( new Rect( pad, ly, half - 4, 14 ), l, TextFlag.LeftCenter );
			Paint.DrawText( new Rect( pad + half + 8, ly, half - 4, 14 ), r, TextFlag.LeftCenter );
		}

		var btnY = Height - 44;
		_cancelRect = new Rect( pad, btnY, 100, 30 );
		_copyRect = new Rect( pad + 112, btnY, 130, 30 );
		_applyRect = new Rect( Width - pad - 165, btnY, 165, 30 );
		DrawButton( _cancelRect, "Cancel", Color.White );
		DrawButton( _copyRect, "Copy remote YAML", Color.Cyan );
		DrawButton( _applyRect, "Apply remote to local", Color.Green );
	}

	private void DrawButton( Rect rect, string label, Color color )
	{
		var hover = rect.IsInside( _mousePos );
		Paint.SetBrush( color.WithAlpha( hover ? 0.16f : 0.07f ) );
		Paint.SetPen( color.WithAlpha( hover ? 0.5f : 0.24f ) );
		Paint.DrawRect( rect, 4 );
		Paint.SetDefaultFont( size: 9, weight: 800 );
		Paint.SetPen( color.WithAlpha( hover ? 1 : 0.78f ) );
		Paint.DrawText( rect, label, TextFlag.Center );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( _cancelRect.IsInside( e.LocalPosition ) ) Close();
		else if ( _copyRect.IsInside( e.LocalPosition ) ) TryCopy( _remoteText );
		else if ( _applyRect.IsInside( e.LocalPosition ) ) { Close(); _apply?.Invoke(); }
	}

	private static void TryCopy( string text )
	{
		try
		{
			var psi = new ProcessStartInfo { FileName = "powershell.exe", Arguments = "-NoProfile -Command Set-Clipboard", RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
			using var p = Process.Start( psi );
			p.StandardInput.Write( text ?? "" );
			p.StandardInput.Close();
		}
		catch
		{
			var path = Path.Combine( Path.GetTempPath(), "network-storage-remote.yml" );
			File.WriteAllText( path, text ?? "" );
			EditorUtility.OpenFile( path );
		}
	}

	protected override void OnMouseMove( MouseEvent e ) { _mousePos = e.LocalPosition; Update(); }
	protected override void OnMouseWheel( WheelEvent e ) { _scroll = Math.Max( 0, _scroll + (e.Delta > 0 ? -45 : 45) ); Update(); e.Accept(); }
}
