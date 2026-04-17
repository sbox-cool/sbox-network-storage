using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;
using Editor;

/// <summary>
/// Shows a breakdown of server-added fields after a push or check-for-updates,
/// explaining what the server added and allowing the user to merge those
/// additions into their local files with one click.
/// </summary>
public class MergeViewWindow : DockWindow
{
	public struct FieldDiff
	{
		public string Name;
		public string LocalValue;  // null if field was added (not present locally)
		public string RemoteValue;
		public string Reason;
		public bool IsAdded;       // true = field only exists on remote
	}

	private readonly string _resourceName;
	private readonly string _resourceType;
	private readonly List<FieldDiff> _addedFields;
	private readonly List<FieldDiff> _changedFields;
	private readonly Action _onMerge;
	private Vector2 _mousePos;
	private float _scroll;
	private Rect _cancelRect;
	private Rect _mergeRect;

	private const float LineH = 16f;
	private const float FieldBlockH = 56f;

	/// <summary>
	/// Known server-added fields and why they exist.
	/// </summary>
	private static readonly Dictionary<string, string> Explanations = new()
	{
		// Collection metadata
		["rateLimits"] = "Rate limit configuration. Controls how many saves per time period are allowed.",
		["rateLimitAction"] = "Action when rate limit is exceeded. 'reject' returns an error, 'clamp' caps values.",
		["webhookOnRateLimit"] = "Discord webhook notification when a rate limit is triggered.",
		["version"] = "Collection API version. Set to v3 for all new collections.",
		["accessMode"] = "Controls whether the collection is publicly readable or endpoint-only.",
		["maxRecords"] = "Maximum number of records per player in this collection.",
		["allowRecordDelete"] = "Whether players can delete their own records.",
		["requireSaveVersion"] = "Version tracking for conflict detection on saves.",
		["collectionType"] = "Whether data is stored per-player or globally.",

		// Endpoint metadata
		["input"] = "Input validation schema. Defines what parameters the endpoint accepts.",

		// Common
		["description"] = "Human-readable description of this resource.",
		["enabled"] = "Whether this resource is active.",
	};

	/// <summary>
	/// Fields that are known to be added by the server as defaults during creation.
	/// If a diff contains ONLY these fields, it is classified as server-defaults-only.
	/// </summary>
	private static readonly HashSet<string> KnownServerDefaults = new()
	{
		"rateLimits", "rateLimitAction", "webhookOnRateLimit", "version",
		"input", "accessMode", "maxRecords", "allowRecordDelete",
		"requireSaveVersion", "collectionType",
	};

	public MergeViewWindow( string resourceName, string resourceType,
		List<FieldDiff> addedFields, List<FieldDiff> changedFields, Action onMerge )
	{
		_resourceName = resourceName;
		_resourceType = resourceType;
		_addedFields = addedFields;
		_changedFields = changedFields;
		_onMerge = onMerge;
		Title = $"Remote Merge -- {resourceName}";

		var fieldCount = addedFields.Count + changedFields.Count;
		Size = new Vector2( 540, Math.Min( 640, 260 + fieldCount * FieldBlockH ) );
		MinimumSize = new Vector2( 420, 280 );
	}

	// ──────────────────────────────────────────────────────
	//  Static analysis: compare local vs remote JSON
	// ──────────────────────────────────────────────────────

	/// <summary>
	/// Compare local and remote JSON to identify server-added or changed fields.
	/// Returns (addedFields, changedFields, isServerDefaultsOnly).
	/// </summary>
	public static (List<FieldDiff> Added, List<FieldDiff> Changed, bool IsDefaultsOnly) AnalyzeDifferences(
		string localJson, string remoteJson )
	{
		var added = new List<FieldDiff>();
		var changed = new List<FieldDiff>();

		try
		{
			var local = JsonSerializer.Deserialize<JsonElement>( localJson );
			var remote = JsonSerializer.Deserialize<JsonElement>( remoteJson );

			if ( local.ValueKind != JsonValueKind.Object || remote.ValueKind != JsonValueKind.Object )
				return (added, changed, false);

			var localKeys = new HashSet<string>();
			foreach ( var prop in local.EnumerateObject() )
				localKeys.Add( prop.Name );

			foreach ( var prop in remote.EnumerateObject() )
			{
				var key = prop.Name;
				var remoteVal = FormatShort( prop.Value );

				if ( !localKeys.Contains( key ) )
				{
					added.Add( new FieldDiff
					{
						Name = key,
						LocalValue = null,
						RemoteValue = remoteVal,
						Reason = Explanations.GetValueOrDefault( key, "Added by the server when saving." ),
						IsAdded = true,
					} );
				}
				else
				{
					var localVal = local.GetProperty( key );
					var localNorm = NormalizeJson( localVal.GetRawText() );
					var remoteNorm = NormalizeJson( prop.Value.GetRawText() );

					if ( localNorm != remoteNorm )
					{
						changed.Add( new FieldDiff
						{
							Name = key,
							LocalValue = FormatShort( localVal ),
							RemoteValue = remoteVal,
							Reason = Explanations.GetValueOrDefault( key, "Modified by the server." ),
							IsAdded = false,
						} );
					}
				}
			}
		}
		catch { /* If parsing fails, return empty lists */ }

		var isDefaultsOnly = (added.Count > 0 || changed.Count > 0)
			&& added.All( f => KnownServerDefaults.Contains( f.Name ) )
			&& changed.All( f => KnownServerDefaults.Contains( f.Name ) );

		return (added, changed, isDefaultsOnly);
	}

	// ──────────────────────────────────────────────────────
	//  Rendering
	// ──────────────────────────────────────────────────────

	private float ContentHeight => 180 + ( _addedFields.Count + _changedFields.Count ) * FieldBlockH + 80;
	private float MaxScroll => Math.Max( 0, ContentHeight - Height + 40 );

	protected override void OnPaint()
	{
		base.OnPaint();

		var pad = 20f;
		var w = Width - pad * 2;
		var y = 20f - _scroll;

		// ── Title ──
		Paint.SetDefaultFont( size: 13, weight: 700 );
		Paint.SetPen( Color.White );
		Paint.DrawText( new Rect( pad, y, w, 22 ), $"Remote Merge -- {_resourceName}", TextFlag.LeftCenter );
		y += 30;

		// ── Explanation ──
		Paint.SetDefaultFont( size: 10 );
		Paint.SetPen( Color.White.WithAlpha( 0.75f ) );
		DrawWrappedText( pad, ref y, w,
			$"The server added default configuration when saving this {_resourceType}. " +
			"Your content is unchanged. Review the additions below and click Merge Changes to accept them into your local files." );
		y += 12;

		// ── Stats bar ──
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
			Paint.DrawText( new Rect( sx, y, 100, 14 ), $"~{_changedFields.Count} changed", TextFlag.LeftCenter );
		}
		y += 22;

		// ── Separator ──
		Paint.SetPen( Color.White.WithAlpha( 0.1f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 8;

		// ── Added fields ──
		if ( _addedFields.Count > 0 )
		{
			Paint.SetDefaultFont( size: 9, weight: 700 );
			Paint.SetPen( Color.Green.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad, y, w, 16 ), "ADDED BY SERVER", TextFlag.LeftCenter );
			y += 22;

			foreach ( var field in _addedFields )
			{
				DrawFieldBlock( pad, w, ref y, field, Color.Green );
			}
		}

		// ── Changed fields ──
		if ( _changedFields.Count > 0 )
		{
			Paint.SetDefaultFont( size: 9, weight: 700 );
			Paint.SetPen( Color.Yellow.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad, y, w, 16 ), "CHANGED BY SERVER", TextFlag.LeftCenter );
			y += 22;

			foreach ( var field in _changedFields )
			{
				DrawFieldBlock( pad, w, ref y, field, Color.Yellow );
			}
		}

		y += 16;

		// ── Separator ──
		Paint.SetPen( Color.White.WithAlpha( 0.1f ) );
		Paint.DrawLine( new Vector2( pad, y ), new Vector2( pad + w, y ) );
		y += 12;

		// ── Buttons row ──
		var btnH = 34f;
		var btnW = ( w - 16 ) / 2;

		// Cancel button (left)
		_cancelRect = new Rect( pad, y, btnW, btnH );
		var cancelHovered = _cancelRect.IsInside( _mousePos );
		Paint.SetBrush( Color.White.WithAlpha( cancelHovered ? 0.1f : 0.04f ) );
		Paint.SetPen( Color.White.WithAlpha( cancelHovered ? 0.3f : 0.15f ) );
		Paint.DrawRect( _cancelRect, 4 );
		Paint.SetDefaultFont( size: 11, weight: 600 );
		Paint.SetPen( Color.White.WithAlpha( cancelHovered ? 0.9f : 0.6f ) );
		Paint.DrawText( _cancelRect, "Cancel", TextFlag.Center );

		// Merge Changes button (right)
		_mergeRect = new Rect( pad + btnW + 16, y, btnW, btnH );
		var mergeHovered = _mergeRect.IsInside( _mousePos );
		Paint.SetBrush( Color.Green.WithAlpha( mergeHovered ? 0.25f : 0.12f ) );
		Paint.SetPen( Color.Green.WithAlpha( mergeHovered ? 0.6f : 0.3f ) );
		Paint.DrawRect( _mergeRect, 4 );
		Paint.SetDefaultFont( size: 11, weight: 700 );
		Paint.SetPen( Color.Green.WithAlpha( mergeHovered ? 1f : 0.85f ) );
		Paint.DrawText( _mergeRect, "Merge Changes", TextFlag.Center );
	}

	private void DrawFieldBlock( float pad, float w, ref float y, FieldDiff field, Color accentColor )
	{
		// Background
		Paint.SetBrush( accentColor.WithAlpha( 0.04f ) );
		Paint.SetPen( accentColor.WithAlpha( 0.12f ) );
		Paint.DrawRect( new Rect( pad, y, w, FieldBlockH - 6 ), 4 );

		// Field name
		Paint.SetDefaultFont( size: 10, weight: 600 );
		Paint.SetPen( accentColor.WithAlpha( 0.9f ) );
		var prefix = field.IsAdded ? "+" : "~";
		Paint.DrawText( new Rect( pad + 10, y + 4, w - 20, 16 ), $"{prefix} {field.Name}", TextFlag.LeftCenter );

		// Value
		Paint.SetDefaultFont( size: 9 );
		if ( field.IsAdded )
		{
			Paint.SetPen( Color.White.WithAlpha( 0.7f ) );
			Paint.DrawText( new Rect( pad + 10, y + 20, w - 20, 14 ), field.RemoteValue, TextFlag.LeftCenter );
		}
		else
		{
			Paint.SetPen( Color.White.WithAlpha( 0.45f ) );
			var valueText = $"{field.LocalValue ?? "null"} → {field.RemoteValue}";
			Paint.DrawText( new Rect( pad + 10, y + 20, w - 20, 14 ), valueText, TextFlag.LeftCenter );
		}

		// Reason
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

	// ──────────────────────────────────────────────────────
	//  Input
	// ──────────────────────────────────────────────────────

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( _cancelRect.IsInside( e.LocalPosition ) )
		{
			Close();
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

	protected override void OnWheel( WheelEvent e )
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

	// ──────────────────────────────────────────────────────
	//  JSON helpers
	// ──────────────────────────────────────────────────────

	private static string FormatShort( JsonElement el )
	{
		if ( el.ValueKind == JsonValueKind.String )
			return $"\"{el.GetString()}\"";

		var text = el.GetRawText();
		return text.Length > 80 ? text[..77] + "..." : text;
	}

	private static string NormalizeJson( string json )
	{
		try
		{
			var el = JsonSerializer.Deserialize<JsonElement>( json );
			return JsonSerializer.Serialize( SortElement( el ), new JsonSerializerOptions { WriteIndented = false } );
		}
		catch { return json.Trim(); }
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
			case JsonValueKind.String:
				return el.GetString();
			case JsonValueKind.Number:
				return el.TryGetInt64( out var l ) ? (object)l : el.GetDouble();
			case JsonValueKind.True:
				return true;
			case JsonValueKind.False:
				return false;
			default:
				return null;
		}
	}
}
