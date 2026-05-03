using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Renders JSON text as YAML for display in the Sync Tool's diff view.
/// Object keys are sorted recursively so that two semantically-identical
/// inputs produce identical line-by-line output regardless of original
/// key order, which is what the side-by-side diff window relies on.
///
/// This is intentionally separate from <see cref="SyncToolPullWriter"/>'s
/// file emitter: the writer must preserve the canonical insertion order of
/// authored fields, while the diff renderer must normalize to a sorted form
/// to avoid spurious diffs from key reordering.
/// </summary>
public static class SyncToolYamlRenderer
{
	/// <summary>
	/// Convert a JSON string into pretty-printed YAML with sorted keys.
	/// Returns the original string unchanged when the input is not valid JSON
	/// so the caller still has something useful to show.
	/// </summary>
	public static string RenderFromJson( string json )
	{
		if ( string.IsNullOrEmpty( json ) ) return "";

		try
		{
			var element = JsonSerializer.Deserialize<JsonElement>( json );
			var normalized = NormalizeAndSort( element );
			return RenderRoot( normalized );
		}
		catch
		{
			return json;
		}
	}

	private static object NormalizeAndSort( JsonElement element )
	{
		switch ( element.ValueKind )
		{
			case JsonValueKind.Object:
				var dict = new Dictionary<string, object>( StringComparer.Ordinal );
				foreach ( var prop in element.EnumerateObject()
					.OrderBy( p => p.Name, StringComparer.Ordinal ) )
				{
					dict[prop.Name] = NormalizeAndSort( prop.Value );
				}
				return dict;
			case JsonValueKind.Array:
				var list = new List<object>();
				foreach ( var item in element.EnumerateArray() )
					list.Add( NormalizeAndSort( item ) );
				return list;
			case JsonValueKind.String:
				return element.GetString();
			case JsonValueKind.Number:
				return element.TryGetInt64( out var l ) ? (object)l : element.GetDouble();
			case JsonValueKind.True:
				return true;
			case JsonValueKind.False:
				return false;
			default:
				return null;
		}
	}

	private static string RenderRoot( object value )
	{
		switch ( value )
		{
			case Dictionary<string, object> dict when dict.Count == 0:
				return "{}\n";
			case Dictionary<string, object> dict:
			{
				var lines = new List<string>();
				AppendMapping( lines, dict, 0 );
				lines.Add( "" );
				return string.Join( "\n", lines );
			}
			case List<object> list when list.Count == 0:
				return "[]\n";
			case List<object> list:
			{
				var lines = new List<string>();
				AppendList( lines, list, 0 );
				lines.Add( "" );
				return string.Join( "\n", lines );
			}
			default:
				return FormatScalar( value ) + "\n";
		}
	}

	private static void AppendMapping( List<string> lines, IDictionary<string, object> values, int indentLevel )
	{
		if ( values.Count == 0 )
		{
			lines.Add( $"{Indent( indentLevel )}{{}}" );
			return;
		}

		foreach ( var pair in values )
			AppendProperty( lines, pair.Key, pair.Value, indentLevel );
	}

	private static void AppendProperty( List<string> lines, string key, object value, int indentLevel )
	{
		var indent = Indent( indentLevel );
		var yamlKey = FormatKey( key );
		switch ( value )
		{
			case Dictionary<string, object> dict when dict.Count == 0:
				lines.Add( $"{indent}{yamlKey}: {{}}" );
				return;
			case Dictionary<string, object> dict:
				lines.Add( $"{indent}{yamlKey}:" );
				AppendMapping( lines, dict, indentLevel + 1 );
				return;
			case List<object> list when list.Count == 0:
				lines.Add( $"{indent}{yamlKey}: []" );
				return;
			case List<object> list:
				lines.Add( $"{indent}{yamlKey}:" );
				AppendList( lines, list, indentLevel + 1 );
				return;
			default:
				lines.Add( $"{indent}{yamlKey}: {FormatScalar( value )}" );
				return;
		}
	}

	private static void AppendList( List<string> lines, List<object> items, int indentLevel )
	{
		foreach ( var item in items )
			AppendListItem( lines, item, indentLevel );
	}

	private static void AppendListItem( List<string> lines, object item, int indentLevel )
	{
		var indent = Indent( indentLevel );
		switch ( item )
		{
			case Dictionary<string, object> dict when dict.Count == 0:
				lines.Add( $"{indent}- {{}}" );
				return;
			case Dictionary<string, object> dict:
				lines.Add( $"{indent}-" );
				AppendMapping( lines, dict, indentLevel + 1 );
				return;
			case List<object> list when list.Count == 0:
				lines.Add( $"{indent}- []" );
				return;
			case List<object> list:
				lines.Add( $"{indent}-" );
				AppendList( lines, list, indentLevel + 1 );
				return;
			default:
				lines.Add( $"{indent}- {FormatScalar( item )}" );
				return;
		}
	}

	private static string FormatKey( string key )
	{
		// Bare keys are safe when they only contain identifier-friendly characters.
		// Anything else gets JSON-quoted, which is also valid YAML.
		return !string.IsNullOrEmpty( key )
			&& key.All( ch => char.IsLetterOrDigit( ch ) || ch == '_' || ch == '-' || ch == '.' )
			? key
			: JsonSerializer.Serialize( key );
	}

	private static string FormatScalar( object value )
	{
		return value switch
		{
			null => "null",
			bool b => b ? "true" : "false",
			sbyte n => n.ToString( CultureInfo.InvariantCulture ),
			byte n => n.ToString( CultureInfo.InvariantCulture ),
			short n => n.ToString( CultureInfo.InvariantCulture ),
			ushort n => n.ToString( CultureInfo.InvariantCulture ),
			int n => n.ToString( CultureInfo.InvariantCulture ),
			uint n => n.ToString( CultureInfo.InvariantCulture ),
			long n => n.ToString( CultureInfo.InvariantCulture ),
			ulong n => n.ToString( CultureInfo.InvariantCulture ),
			float n => n.ToString( "R", CultureInfo.InvariantCulture ),
			double n => n.ToString( "R", CultureInfo.InvariantCulture ),
			decimal n => n.ToString( CultureInfo.InvariantCulture ),
			_ => JsonSerializer.Serialize( value.ToString() )
		};
	}

	private static string Indent( int indentLevel ) => new( ' ', indentLevel * 2 );
}
