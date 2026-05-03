using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Canonical normalizer for endpoint and workflow step routing.
///
/// Mirrors <c>tools/sbox/flow-routing.js</c> (<c>normalizeStepRoutes</c>,
/// <c>normalizeRoutes</c>, <c>normalizeRouteOutcome</c>, <c>legacyOnFailToRoute</c>)
/// from the website so the Sync Tool's diff classifier and diff view see the
/// same canonical shape regardless of authoring shape.
///
/// Without this, a local file using canonical <c>routes.true</c> /
/// <c>routes.false</c> compares as fully different from a remote definition
/// that still carries legacy <c>onFail</c>, even when the two are
/// behaviorally identical. Keeping the normalizer in sync with the JS
/// implementation is important — drift would silently re-introduce false
/// diffs in the editor.
/// </summary>
public static class SyncToolFlowCanonicalizer
{
	private static readonly HashSet<string> RouteActions = new( StringComparer.Ordinal )
	{
		"continue",
		"return",
		"reject",
		"goto",
		"run",
	};

	/// <summary>
	/// Normalize the steps array of an endpoint or workflow. Returns null when
	/// the input is not an array so callers can leave the original value in place.
	/// </summary>
	public static List<object> NormalizeSteps( object steps )
	{
		var list = ToList( steps );
		if ( list == null ) return null;

		var result = new List<object>( list.Count );
		foreach ( var step in list )
			result.Add( NormalizeStepRoutes( step ) );
		return result;
	}

	/// <summary>
	/// Normalize one step. If the step has any routing fields (<c>routes</c>,
	/// <c>onTrue</c>, <c>onFalse</c>, <c>onFail</c>) or is a condition step,
	/// rewrite into canonical <c>routes.{true,false}</c> form and drop the
	/// legacy fields. Nested step lists are normalized recursively.
	/// </summary>
	public static object NormalizeStepRoutes( object step )
	{
		var dict = ToDictionary( step );
		if ( dict == null ) return step;

		if ( dict.TryGetValue( "steps", out var nested ) )
		{
			var nestedList = NormalizeSteps( nested );
			if ( nestedList != null ) dict["steps"] = nestedList;
		}

		var typeStr = dict.TryGetValue( "type", out var typeValue ) ? typeValue as string : null;
		var hasCondition = string.Equals( typeStr, "condition", StringComparison.Ordinal );
		var hasRouteFields =
			dict.ContainsKey( "routes" )
			|| dict.ContainsKey( "onTrue" )
			|| dict.ContainsKey( "onFalse" )
			|| dict.ContainsKey( "onFail" );

		if ( hasCondition || hasRouteFields )
		{
			dict["routes"] = NormalizeRoutes( dict );
			dict.Remove( "onTrue" );
			dict.Remove( "onFalse" );
			dict.Remove( "onFail" );
		}

		return dict;
	}

	private static Dictionary<string, object> NormalizeRoutes( IDictionary<string, object> step )
	{
		var existing = step.TryGetValue( "routes", out var routes )
			? ToDictionary( routes ) ?? new Dictionary<string, object>()
			: new Dictionary<string, object>();

		var trueSource = FirstNonNull(
			step.TryGetValue( "onTrue", out var onTrue ) ? onTrue : null,
			existing.TryGetValue( "true", out var existingTrue ) ? existingTrue : null,
			existing.TryGetValue( "pass", out var existingPass ) ? existingPass : null,
			DefaultRoute( "continue" )
		);

		var legacyFalse = LegacyOnFailToRoute(
			step.TryGetValue( "onFail", out var onFail ) ? onFail : null );

		var falseSource = FirstNonNull(
			step.TryGetValue( "onFalse", out var onFalse ) ? onFalse : null,
			existing.TryGetValue( "false", out var existingFalse ) ? existingFalse : null,
			existing.TryGetValue( "fail", out var existingFail ) ? existingFail : null,
			legacyFalse
		);

		return new Dictionary<string, object>
		{
			["true"] = NormalizeRouteOutcome( trueSource, "continue" ),
			["false"] = NormalizeRouteOutcome( falseSource, "reject" ),
		};
	}

	private static object NormalizeRouteOutcome( object route, string defaultAction )
	{
		// undefined / null / true → defaultAction (matches JS `route ?? true`).
		if ( route is null ) return DefaultRoute( defaultAction );
		if ( route is bool boolean ) return DefaultRoute( boolean ? defaultAction : "reject" );

		if ( route is string text )
		{
			if ( RouteActions.Contains( text ) ) return DefaultRoute( text );
			if ( text == "skip" )
				return new Dictionary<string, object> { ["action"] = "skip", ["count"] = 1L };
			return new Dictionary<string, object> { ["action"] = "goto", ["step"] = text };
		}

		var dict = ToDictionary( route );
		if ( dict == null ) return DefaultRoute( defaultAction );

		var rawAction = (dict.TryGetValue( "action", out var actionValue ) ? actionValue as string : null)
			?? (dict.TryGetValue( "type", out var typeValue ) ? typeValue as string : null)
			?? defaultAction;

		// Preserve all extra fields so behavior-relevant data (status, error,
		// message, retry, etc.) survives canonicalization.
		var copy = new Dictionary<string, object>( dict );

		if ( rawAction == "step" )
		{
			copy["action"] = "goto";
			copy["step"] = FirstNonNull(
				dict.TryGetValue( "step", out var s ) ? s : null,
				dict.TryGetValue( "target", out var t ) ? t : null,
				dict.TryGetValue( "goto", out var g ) ? g : null
			);
			return copy;
		}

		if ( rawAction == "workflow" || rawAction == "flow" || rawAction == "endpoint" )
		{
			copy["action"] = "run";
			copy["flow"] = FirstNonNull(
				dict.TryGetValue( "flow", out var f ) ? f : null,
				dict.TryGetValue( "workflow", out var w ) ? w : null,
				dict.TryGetValue( "endpoint", out var e ) ? e : null,
				dict.TryGetValue( "target", out var tg ) ? tg : null
			);
			return copy;
		}

		if ( rawAction == "skip" )
		{
			copy["action"] = "skip";
			return copy;
		}

		copy["action"] = RouteActions.Contains( rawAction ) ? rawAction : defaultAction;
		return copy;
	}

	private static object LegacyOnFailToRoute( object onFail )
	{
		if ( onFail is string str )
		{
			if ( str == "skip" )
				return new Dictionary<string, object> { ["action"] = "skip", ["count"] = 1L };
			// Bare strings other than "skip" aren't a documented legacy form.
			// Fall through to the default reject so we don't fabricate routing.
			return DefaultRoute( "reject" );
		}

		var dict = ToDictionary( onFail );
		if ( dict == null ) return DefaultRoute( "reject" );

		var skip = FirstNonNull(
			dict.TryGetValue( "skip", out var s ) ? s : null,
			dict.TryGetValue( "skipSteps", out var ss ) ? ss : null,
			dict.TryGetValue( "steps", out var st ) ? st : null
		);
		var actionIsSkip = dict.TryGetValue( "action", out var actionValue )
			&& string.Equals( actionValue as string, "skip", StringComparison.Ordinal );

		if ( actionIsSkip || skip != null )
		{
			if ( skip is string skipStr && skipStr != "next" )
				return new Dictionary<string, object>
				{
					["action"] = "goto",
					["step"] = skipStr,
					["legacySkip"] = true,
				};

			var skipResult = new Dictionary<string, object> { ["action"] = "skip" };
			if ( skip != null ) skipResult["skip"] = skip;
			skipResult["count"] = skip switch
			{
				long l => l,
				int i => (long)i,
				double d => (long)d,
				_ => 1L,
			};
			skipResult["legacySkip"] = true;
			return skipResult;
		}

		var copy = new Dictionary<string, object>( dict );
		var rejectFalse = copy.TryGetValue( "reject", out var rejectValue )
			&& rejectValue is bool rb && !rb;
		copy["action"] = rejectFalse ? "continue" : "reject";
		return copy;
	}

	private static Dictionary<string, object> DefaultRoute( string action ) =>
		new() { ["action"] = action };

	private static Dictionary<string, object> ToDictionary( object value )
	{
		if ( value is Dictionary<string, object> dict )
			return new Dictionary<string, object>( dict );

		if ( value is IDictionary<string, object> idict )
			return new Dictionary<string, object>( idict );

		if ( value is JsonElement el && el.ValueKind == JsonValueKind.Object )
		{
			var result = new Dictionary<string, object>();
			foreach ( var prop in el.EnumerateObject() )
				result[prop.Name] = JsonElementToObject( prop.Value );
			return result;
		}

		return null;
	}

	private static List<object> ToList( object value )
	{
		if ( value is List<object> list ) return list;

		if ( value is JsonElement el && el.ValueKind == JsonValueKind.Array )
		{
			var result = new List<object>();
			foreach ( var item in el.EnumerateArray() )
				result.Add( JsonElementToObject( item ) );
			return result;
		}

		return null;
	}

	private static object JsonElementToObject( JsonElement el )
	{
		switch ( el.ValueKind )
		{
			case JsonValueKind.Object:
				var dict = new Dictionary<string, object>();
				foreach ( var prop in el.EnumerateObject() )
					dict[prop.Name] = JsonElementToObject( prop.Value );
				return dict;
			case JsonValueKind.Array:
				var list = new List<object>();
				foreach ( var item in el.EnumerateArray() )
					list.Add( JsonElementToObject( item ) );
				return list;
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

	private static object FirstNonNull( params object[] candidates )
	{
		foreach ( var candidate in candidates )
		{
			if ( candidate != null ) return candidate;
		}
		return null;
	}
}
