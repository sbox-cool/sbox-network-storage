#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox;
using Editor;

public class StepIdAutoFixWindow : DockWindow
{
	private readonly List<StepIdFixPlan> _plans;
	private readonly Action _afterApply;
	private Vector2 _mousePos;
	private Rect _applyRect;
	private Rect _cancelRect;
	private float _scroll;

	private StepIdAutoFixWindow( List<StepIdFixPlan> plans, Action afterApply )
	{
		_plans = plans ?? new();
		_afterApply = afterApply;
		Title = "Auto-add step IDs";
		Size = new Vector2( 760, 560 );
		MinimumSize = new Vector2( 560, 360 );
		MouseTracking = true;
	}

	public static void Show( List<StepIdFixPlan> plans, Action afterApply )
	{
		new StepIdAutoFixWindow( plans, afterApply ).Show();
	}

	protected override void OnPaint()
	{
		base.OnPaint();
		var pad = 18f;
		var w = Width - pad * 2;
		var y = 18f;

		Paint.SetDefaultFont( size: 14, weight: 800 );
		Paint.SetPen( new Color( 1f, 0.75f, 0.28f ) );
		Paint.DrawText( new Rect( pad, y, w, 24 ), "Some endpoint steps are missing IDs.", TextFlag.LeftCenter );
		y += 30;

		Paint.SetDefaultFont( size: 9 );
		Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
		Paint.DrawText( new Rect( pad, y, w, 18 ), "Preview the deterministic IDs below. Applying writes the updated .endpoint.yml and creates a .bak backup first.", TextFlag.LeftCenter );
		y += 28;

		var contentTop = y;
		var drawY = contentTop - _scroll;
		foreach ( var plan in _plans )
		{
			var added = ExtractAddedIdLines( plan ).ToList();
			var h = 44 + Math.Max( 1, added.Count ) * 16;
			if ( drawY + h > contentTop && drawY < Height - 70 )
			{
				Paint.SetBrush( Color.White.WithAlpha( 0.04f ) );
				Paint.SetPen( Color.White.WithAlpha( 0.10f ) );
				Paint.DrawRect( new Rect( pad, drawY, w, h ), 6 );

				Paint.SetDefaultFont( size: 10, weight: 700 );
				Paint.SetPen( Color.White.WithAlpha( 0.9f ) );
				Paint.DrawText( new Rect( pad + 10, drawY + 8, w - 20, 16 ), Path.GetFileName( plan.FilePath ), TextFlag.LeftCenter );

				var lineY = drawY + 28;
				Paint.SetDefaultFont( size: 9 );
				Paint.SetPen( new Color( 0.55f, 1f, 0.65f, 0.86f ) );
				foreach ( var line in added )
				{
					Paint.DrawText( new Rect( pad + 18, lineY, w - 36, 15 ), $"+ {line.Trim()}", TextFlag.LeftCenter );
					lineY += 16;
				}
			}
			drawY += h + 10;
		}

		var totalHeight = drawY + _scroll - contentTop;
		var maxScroll = Math.Max( 0, totalHeight - (Height - contentTop - 78) );
		_scroll = Math.Clamp( _scroll, 0, maxScroll );

		var btnY = Height - 48;
		_cancelRect = new Rect( pad, btnY, 120, 32 );
		_applyRect = new Rect( Width - pad - 180, btnY, 180, 32 );
		DrawButton( _cancelRect, "Cancel", Color.White );
		DrawButton( _applyRect, "Auto-add step IDs", Color.Green );
	}

	private static IEnumerable<string> ExtractAddedIdLines( StepIdFixPlan plan )
	{
		var oldLines = new HashSet<string>( (plan.OriginalText ?? "").Replace( "\r\n", "\n" ).Split( '\n' ).Select( x => x.Trim() ) );
		foreach ( var line in (plan.UpdatedText ?? "").Replace( "\r\n", "\n" ).Split( '\n' ) )
		{
			var trimmed = line.Trim();
			if ( trimmed.StartsWith( "id:" ) && !oldLines.Contains( trimmed ) ) yield return line;
			else if ( trimmed.StartsWith( "- id:" ) && !oldLines.Contains( trimmed ) ) yield return line;
		}
	}

	private void DrawButton( Rect rect, string label, Color color )
	{
		var hovered = rect.IsInside( _mousePos );
		Paint.SetBrush( color.WithAlpha( hovered ? 0.18f : 0.08f ) );
		Paint.SetPen( color.WithAlpha( hovered ? 0.55f : 0.25f ) );
		Paint.DrawRect( rect, 5 );
		Paint.SetDefaultFont( size: 10, weight: 800 );
		Paint.SetPen( color.WithAlpha( hovered ? 1f : 0.8f ) );
		Paint.DrawText( rect, label, TextFlag.Center );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );
		if ( _cancelRect.IsInside( e.LocalPosition ) ) Close();
		if ( _applyRect.IsInside( e.LocalPosition ) )
		{
			foreach ( var plan in _plans ) StepIdAutoFixer.ApplyPlanWithBackup( plan );
			Close();
			_afterApply?.Invoke();
		}
	}

	protected override void OnMouseMove( MouseEvent e ) { _mousePos = e.LocalPosition; Update(); }
	protected override void OnMouseWheel( WheelEvent e ) { _scroll = Math.Max( 0, _scroll + (e.Delta > 0 ? -48 : 48) ); Update(); e.Accept(); }
}
