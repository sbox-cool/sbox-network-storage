using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

public static class SyncToolPullWriter
{
	public static bool SaveEndpoint( string slug, JsonElement remoteEndpoint )
	{
		var local = SyncToolTransforms.ServerEndpointToLocal( remoteEndpoint );
		return SaveResource(
			"endpoint",
			slug,
			remoteEndpoint,
			local );
	}

	public static bool SaveCollection( string name, JsonElement remoteCollection )
	{
		var local = SyncToolTransforms.ServerCollectionToLocal( remoteCollection );
		return SaveResource(
			"collection",
			name,
			remoteCollection,
			local );
	}

	public static bool SaveWorkflow( string id, JsonElement remoteWorkflow )
	{
		var local = SyncToolTransforms.ServerWorkflowToLocal( remoteWorkflow );
		return SaveResource(
			"workflow",
			id,
			remoteWorkflow,
			local );
	}

	public static int SaveCollections( JsonElement serverResponse )
	{
		var data = serverResponse;
		if ( serverResponse.TryGetProperty( "data", out var d ) )
			data = d;
		if ( data.ValueKind != JsonValueKind.Array )
			return 0;

		var count = 0;
		foreach ( var collection in data.EnumerateArray() )
		{
			var local = SyncToolTransforms.ServerCollectionToLocal( collection );
			var name = local.TryGetValue( "name", out var value ) ? value?.ToString() : null;
			if ( string.IsNullOrWhiteSpace( name ) || name == "unknown" )
				continue;

			if ( SaveCollection( name, collection ) )
				count++;
		}

		return count;
	}

	private static bool SaveResource( string kind, string id, JsonElement remote, Dictionary<string, object> local )
	{
		var hasSource = SyncToolTransforms.TryGetSourceText( remote, out var sourceText );
		var sourcePath = SyncToolTransforms.GetSourcePath( remote );

		if ( hasSource )
			SyncToolConfig.SaveSourceResource( kind, id, sourceText, sourcePath );
		else
			WriteSource( kind, id, local );

		return true;
	}

	public static void WriteSource( string kind, string id, Dictionary<string, object> data )
	{
		var folder = kind switch
		{
			"collection" => SyncToolConfig.CollectionsPath,
			"endpoint" => SyncToolConfig.EndpointsPath,
			"workflow" => SyncToolConfig.WorkflowsPath,
			"test" => SyncToolConfig.TestsPath,
			_ => SyncToolConfig.SyncToolsPath
		};
		var path = Path.Combine( SyncToolConfig.Abs( folder ), $"{id}.{kind}.yml" );
		var dir = Path.GetDirectoryName( path );
		if ( !Directory.Exists( dir ) )
			Directory.CreateDirectory( dir );

		File.WriteAllText( path, BuildSourceText( kind, id, data ) );
	}

	private static string BuildSourceText( string kind, string id, Dictionary<string, object> data )
	{
		var topLevelKeys = kind switch
		{
			"collection" => new HashSet<string>( StringComparer.OrdinalIgnoreCase ) { "id", "name", "description", "notes" },
			"endpoint" => new HashSet<string>( StringComparer.OrdinalIgnoreCase ) { "id", "slug", "name", "description", "notes" },
			"workflow" => new HashSet<string>( StringComparer.OrdinalIgnoreCase ) { "id", "name", "description", "notes", "legacyJson" },
			_ => new HashSet<string>( StringComparer.OrdinalIgnoreCase ) { "id", "name", "description", "notes" }
		};
		var definition = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
		foreach ( var pair in data )
		{
			if ( !topLevelKeys.Contains( pair.Key ) )
				definition[pair.Key] = pair.Value;
		}

		var header = new List<string>
		{
			"sourceVersion: 1",
			$"kind: {kind}",
			$"id: {id}"
		};
		foreach ( var key in new[] { "name", "description", "notes" } )
		{
			if ( data.TryGetValue( key, out var value ) && value != null )
				header.Add( $"{key}: {JsonSerializer.Serialize( value.ToString() )}" );
		}

		var lines = new List<string>( header );
		lines.Add( "definition:" );
		AppendYamlMapping( lines, definition, 1 );
		lines.Add( "" );
		return string.Join( "\n", lines );
	}

	private static void AppendYamlMapping( List<string> lines, IDictionary<string, object> values, int indentLevel )
	{
		if ( values.Count == 0 )
		{
			lines.Add( $"{Indent( indentLevel )}{{}}" );
			return;
		}

		foreach ( var pair in values )
		{
			AppendYamlProperty( lines, pair.Key, pair.Value, indentLevel );
		}
	}

	private static void AppendYamlProperty( List<string> lines, string key, object value, int indentLevel )
	{
		var indent = Indent( indentLevel );
		var yamlKey = FormatYamlKey( key );
		var normalized = NormalizeYamlValue( value );
		switch ( normalized )
		{
			case Dictionary<string, object> dict when dict.Count == 0:
				lines.Add( $"{indent}{yamlKey}: {{}}" );
				return;
			case Dictionary<string, object> dict:
				lines.Add( $"{indent}{yamlKey}:" );
				AppendYamlMapping( lines, dict, indentLevel + 1 );
				return;
			case List<object> list when list.Count == 0:
				lines.Add( $"{indent}{yamlKey}: []" );
				return;
			case List<object> list:
				lines.Add( $"{indent}{yamlKey}:" );
				AppendYamlList( lines, list, indentLevel + 1 );
				return;
			default:
				lines.Add( $"{indent}{yamlKey}: {FormatYamlScalar( normalized )}" );
				return;
		}
	}

	private static void AppendYamlList( List<string> lines, List<object> items, int indentLevel )
	{
		foreach ( var item in items )
		{
			AppendYamlListItem( lines, item, indentLevel );
		}
	}

	private static void AppendYamlListItem( List<string> lines, object item, int indentLevel )
	{
		var indent = Indent( indentLevel );
		var normalized = NormalizeYamlValue( item );
		switch ( normalized )
		{
			case Dictionary<string, object> dict when dict.Count == 0:
				lines.Add( $"{indent}- {{}}" );
				return;
			case Dictionary<string, object> dict:
				lines.Add( $"{indent}-" );
				AppendYamlMapping( lines, dict, indentLevel + 1 );
				return;
			case List<object> list when list.Count == 0:
				lines.Add( $"{indent}- []" );
				return;
			case List<object> list:
				lines.Add( $"{indent}-" );
				AppendYamlList( lines, list, indentLevel + 1 );
				return;
			default:
				lines.Add( $"{indent}- {FormatYamlScalar( normalized )}" );
				return;
		}
	}

	private static object NormalizeYamlValue( object value )
	{
		if ( value is null )
			return null;

		if ( value is JsonElement element )
		{
			return element.ValueKind switch
			{
				JsonValueKind.Object => element.EnumerateObject()
					.ToDictionary( prop => prop.Name, prop => NormalizeYamlValue( prop.Value ), StringComparer.OrdinalIgnoreCase ),
				JsonValueKind.Array => element.EnumerateArray().Select( item => NormalizeYamlValue( item ) ).ToList(),
				JsonValueKind.String => element.GetString(),
				JsonValueKind.Number when element.TryGetInt64( out var l ) => l,
				JsonValueKind.Number when element.TryGetDouble( out var d ) => d,
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				_ => null
			};
		}

		if ( value is Dictionary<string, object> dict )
			return dict.ToDictionary( pair => pair.Key, pair => NormalizeYamlValue( pair.Value ), StringComparer.OrdinalIgnoreCase );

		if ( value is IDictionary<string, object> otherDict )
			return otherDict.ToDictionary( pair => pair.Key, pair => NormalizeYamlValue( pair.Value ), StringComparer.OrdinalIgnoreCase );

		if ( value is IEnumerable<object> sequence && value is not string )
			return sequence.Select( item => NormalizeYamlValue( item ) ).ToList();

		return value;
	}

	private static string FormatYamlKey( string key )
	{
		return key.All( ch => char.IsLetterOrDigit( ch ) || ch == '_' || ch == '-' || ch == '.' )
			? key
			: JsonSerializer.Serialize( key );
	}

	private static string FormatYamlScalar( object value )
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
