using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Editor;

/// <summary>
/// Shows additive remote-only fields after a push or remote check so the user can
/// review and pull those semantics into their local files.
/// </summary>
public class MergeViewWindow : DockWindow
{
	public struct FieldDiff
	{
		public string Name;
		public string LocalValue;
		public string RemoteValue;
		public string Reason;
		public bool IsAdded;
	}

	private readonly string _resourceName;
	private readonly string _resourceType;
	private readonly List<FieldDiff> _addedFields;
	private readonly List<FieldDiff> _changedFields;
	private readonly Action _onMerge;
	// Optional inspect-only callback. When set, the window renders a
	// "View Diff" button so reviewers can drill into the full local-vs-remote
	// content before deciding whether to pull. Useful when the additive-fields
	// summary keeps reappearing after pull/push/sync and the reviewer wants
	// to confirm what the classifier is actually seeing.
	private readonly Action _onViewDiff;
	private Vector2 _mousePos;
	private float _scroll;
	private Rect _cancelRect;
	private Rect _viewDiffRect;
	private Rect _mergeRect;

	private const float LineH = 16f;
	private const float FieldBlockH = 56f;

	private static readonly Dictionary<string, string> Explanations = new()
	{
		["rateLimits"] = "Rate limit configuration. Controls how many saves per time period are allowed.",
		["rateLimitAction"] = "Action when rate limit is exceeded. 'reject' returns an error, 'clamp' caps values.",
		["webhookOnRateLimit"] = "Discord webhook notification when a rate limit is triggered.",
		["version"] = "Collection API version. Set to v3 for all new collections.",
		["accessMode"] = "Controls whether the collection is publicly readable or endpoint-only.",
		["maxRecords"] = "Maximum number of records per player in this collection.",
		["allowRecordDelete"] = "Whether players can delete their own records.",
		["requireSaveVersion"] = "Version tracking for conflict detection on saves.",
		["collectionType"] = "Whether data is stored per-player or globally.",
		["input"] = "Input validation schema. Defines what parameters the endpoint accepts.",
		["description"] = "Human-readable description of this resource.",
		["notes"] = "Editor notes saved with this resource.",
		["builtIn"] = "System flag added by the remote project metadata.",
		["enabled"] = "Whether this resource is active.",
	};

	public MergeViewWindow( string resourceName, string resourceType,
		List<FieldDiff> addedFields, List<FieldDiff> changedFields,
		Action onMerge, Action onViewDiff = null )
	{
		_resourceName = resourceName;
		_resourceType = resourceType;
		_addedFields = addedFields;
		_changedFields = changedFields;
		_onMerge = onMerge;
		_onViewDiff = onViewDiff;
		Title = $"Pull Remote Semantics - {resourceName}";

		var fieldCount = addedFields.Count + changedFields.Count;
		Size = new Vector2( 540, Math.Min( 640, 260 + fieldCount * FieldBlockH ) );
		MinimumSize = new Vector2( 420, 280 );
	}

	/// <summary>
	/// Compare local and remote JSON to identify additive remote fields versus real content changes.
	/// Returns (addedFields, changedFields, isRemoteAdditiveOnly).
	/// </summary>
	public static (List<FieldDiff> Added, List<FieldDiff> Changed, bool IsRemoteAdditiveOnly) AnalyzeDifferences(
		string localJson, string remoteJson )
	{
		var analysis = JsonDiffUtilities.Analyze( localJson, remoteJson );

		return (
			analysis.Added.Select( ToFieldDiff ).ToList(),
			analysis.Changed.Select( ToFieldDiff ).ToList(),
			analysis.IsRemoteAdditiveOnly
		);
	}

	private float ContentHeight => 180 + ( _addedFields.Count + _changedFields.Count ) * FieldBlockH + 80;
	private float MaxScroll => Math.Max( 0, ContentHeight - Height + 40 );

	protected override void OnPaint()
	{
		base.OnPaint();

		var pad = 20f;
		var w = Width - pad * 2;
		var y = 20f - _scroll;

		Paint.SetDefaultFont( size: 13, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w, 22 ), $"Pull Remote Semantics - {_resourceName}", TextFlag.LeftCenter );
		y += 30;

		Paint.SetDefaultFont( size: 10 );
		Paint.SetPen( Color.White.WithAlpha( 0.75f ) );
		DrawWrappedText( pad, ref y, w,
			$"The remote version of this {_resourceType} contains additive fields only. " +
			"Your local content still matches what is stored remotely. Review the remote-only semantics below and pull them into your local files if you want to keep those additions locally." );
		y += 12;

		Paint.SetDefaultFont( size: 9, weight: 600 );
		var sx = pad;
		if ( _addedFields.Count > 0 )
		{
			Paint.SetPen( Color.Green.WithAlpha( 0.8f ) );
			Paint.DrawText( new Rect( sx, y, 100, 14 ), $"+{_addedFields.Count} added", TextFlag.LeftCenter );
			sx += 80;
		}
		if ( _changedFields.Count > 0 )
		{
			Paint.SetPen( Color.Yellow.WithAlpha( 0.8f ) );
			Paint.DrawText( new Rect( sx, y, 110, 14 ), $"~{_changedFields.Count} changed", TextFlag.LeftCenter );
		}
		y += 22;

		Paint.SetPen( Color.White.WithAlpha( 0.1f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 8;

		if ( _addedFields.Count > 0 )
		{
			Paint.SetDefaultFont( size: 9, weight: 700 );
			Paint.SetPen( Color.Green.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad, y, w, 16 ), "ADDED ON REMOTE", TextFlag.LeftCenter );
			y += 22;

			foreach ( var field in _addedFields )
				DrawFieldBlock( pad, w, ref y, field, Color.Green );
		}

		if ( _changedFields.Count > 0 )
		{
			Paint.SetDefaultFont( size: 9, weight: 700 );
			Paint.SetPen( Color.Yellow.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad, y, w, 16 ), "DIFFERENT ON REMOTE", TextFlag.LeftCenter );
			y += 22;

			foreach ( var field in _changedFields )
				DrawFieldBlock( pad, w, ref y, field, Color.Yellow );
		}

		y += 16;

		Paint.SetPen( Color.White.WithAlpha( 0.1f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 12;

		var btnH = 34f;
		var gapW = 12f;
		var showViewDiff = _onViewDiff != null;

		// Layout: Cancel | (View Diff)? | Pull Remote Semantics
		// View Diff is optional so the inspect-only callback stays opt-in for
		// callers that don't have YAML strings to hand over.
		var cancelW = 80f;
		var viewDiffW = showViewDiff ? 100f : 0f;
		var viewDiffSlot = showViewDiff ? viewDiffW + gapW : 0f;
		var mergeW = w - cancelW - viewDiffSlot - gapW;

		_cancelRect = new Rect( pad, y, cancelW, btnH );
		var cancelHovered = _cancelRect.IsInside( _mousePos );
		Paint.SetBrush( Color.White.WithAlpha( cancelHovered ? 0.1f : 0.04f ) );
		Paint.SetPen( Color.White.WithAlpha( cancelHovered ? 0.3f : 0.15f ) );
		Paint.DrawRect( _cancelRect, 4 );
		Paint.SetDefaultFont( size: 11, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( cancelHovered ? 0.9f : 0.6f ) );
		Paint.DrawText( _cancelRect, "Cancel", TextFlag.Center );

		var mergeX = pad + cancelW + gapW;
		if ( showViewDiff )
		{
			_viewDiffRect = new Rect( mergeX, y, viewDiffW, btnH );
			var diffHovered = _viewDiffRect.IsInside( _mousePos );
			Paint.SetBrush( Color.Cyan.WithAlpha( diffHovered ? 0.18f : 0.06f ) );
			Paint.SetPen( Color.Cyan.WithAlpha( diffHovered ? 0.55f : 0.25f ) );
			Paint.DrawRect( _viewDiffRect, 4 );
			Paint.SetDefaultFont( size: 11, weight: 600 );
			Paint.SetPen( Color.Cyan.WithAlpha( diffHovered ? 1f : 0.75f ) );
			Paint.DrawText( _viewDiffRect, "View Diff", TextFlag.Center );
			mergeX += viewDiffW + gapW;
		}

		_mergeRect = new Rect( mergeX, y, mergeW, btnH );
		var mergeHovered = _mergeRect.IsInside( _mousePos );
		Paint.SetBrush( Color.Green.WithAlpha( mergeHovered ? 0.25f : 0.12f ) );
		Paint.SetPen( Color.Green.WithAlpha( mergeHovered ? 0.6f : 0.3f ) );
		Paint.DrawRect( _mergeRect, 4 );
		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.Green.WithAlpha( mergeHovered ? 1f : 0.85f ) );
		Paint.DrawText( _mergeRect, "Pull Remote Semantics", TextFlag.Center );
	}

	private void DrawFieldBlock( float pad, float w, ref float y, FieldDiff field, Color accentColor )
	{
		Paint.SetBrush( accentColor.WithAlpha( 0.04f ) );
		Paint.SetPen( accentColor.WithAlpha( 0.12f ) );
		Paint.DrawRect( new Rect( pad, y, w, FieldBlockH - 6 ), 4 );

		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( accentColor.WithAlpha( 0.9f ) );
		var prefix = field.IsAdded ? "+" : "~";
		Paint.DrawText( new Rect( pad + 10, y + 4, w - 20, 16 ), $"{prefix} {field.Name}", TextFlag.LeftCenter );

		Paint.SetDefaultFont( size: 9 );
		if ( field.IsAdded )
		{
			Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad + 10, y + 20, w - 20, 14 ), field.RemoteValue, TextFlag.LeftCenter );
		}
		else
		{
			Paint.SetPen( Color.White.WithAlpha( 0.45f ) );
			var valueText = $"{field.LocalValue ?? "null"} -> {field.RemoteValue}";
			Paint.DrawText( new Rect( pad + 10, y + 20, w - 20, 14 ), valueText, TextFlag.LeftCenter );
		}

		Paint.SetDefaultFont( size: 8 );
		Paint.SetPen( Color.White.WithAlpha( 0.4f ) );
		Paint.DrawText( new Rect( pad + 10, y + 34, w - 20, 12 ), field.Reason, TextFlag.LeftCenter );

		y += FieldBlockH;
	}

	private void DrawWrappedText( float x, ref float y, float maxW, string text )
	{
		var words = text.Split( ' ' );
		var line = "";
		foreach ( var word in words )
		{
			var test = string.IsNullOrEmpty( line ) ? word : $"{line} {word}";
			if ( test.Length * 6.2f > maxW && !string.IsNullOrEmpty( line ) )
			{
				Paint.DrawText( new Rect( x, y, maxW, 16 ), line, TextFlag.LeftCenter );
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
			Paint.DrawText( new Rect( x, y, maxW, 16 ), line, TextFlag.LeftCenter );
			y += 17;
		}
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( _cancelRect.IsInside( e.LocalPosition ) )
		{
			Close();
		}
		else if ( _onViewDiff != null && _viewDiffRect.IsInside( e.LocalPosition ) )
		{
			// Inspect-only — keep the merge window open so the reviewer can
			// flip back and forth between the field summary and the full diff.
			_onViewDiff();
		}
		else if ( _mergeRect.IsInside( e.LocalPosition ) )
		{
			Close();
			_onMerge?.Invoke();
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
		var direction = e.Delta > 0 ? -1 : 1;
		_scroll = Math.Clamp( _scroll + direction * LineH * 3, 0, MaxScroll );
		Update();
		e.Accept();
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		switch ( e.Key )
		{
			case KeyCode.Escape: Close(); break;
			case KeyCode.Up: _scroll = Math.Max( 0, _scroll - LineH ); Update(); break;
			case KeyCode.Down: _scroll = Math.Min( MaxScroll, _scroll + LineH ); Update(); break;
			case KeyCode.PageUp: _scroll = Math.Max( 0, _scroll - LineH * 10 ); Update(); break;
			case KeyCode.PageDown: _scroll = Math.Min( MaxScroll, _scroll + LineH * 10 ); Update(); break;
			default: base.OnKeyPress( e ); break;
		}
	}

	private static FieldDiff ToFieldDiff( JsonDiffUtilities.FieldDifference diff )
	{
		return new FieldDiff
		{
			Name = diff.Path,
			LocalValue = diff.LocalValue,
			RemoteValue = diff.RemoteValue,
			Reason = GetReason( diff.Path, diff.IsAdded ),
			IsAdded = diff.IsAdded
		};
	}

	private static string GetReason( string path, bool isAdded )
	{
		var key = path?.Split( '.' ).LastOrDefault() ?? "";
		if ( Explanations.TryGetValue( key, out var explanation ) )
			return explanation;

		return isAdded
			? "Present on remote but missing locally."
			: "Value differs between local and remote.";
	}
}
