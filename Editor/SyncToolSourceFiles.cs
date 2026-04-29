using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sandbox;

public static partial class SyncToolConfig
{
	public sealed class SourceSummary
	{
		public string Kind { get; set; } = "";
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		public string Description { get; set; } = "";
		public string Path { get; set; } = "";
	}

	public static string LibrariesPath => $"{SyncToolsPath}/libraries";

	public static void SetSourceExportMode( SourceExportMode mode )
	{
		SourceExport = mode;
		if ( File.Exists( Abs( ProjectConfigFile ) ) )
			Save( SecretKey, PublicApiKey, ProjectId, BaseUrl, DataSource, DataFolder, CdnUrl );
	}

	public static string SaveSourceResource( string kind, string id, string sourceText, string sourcePath = null )
	{
		if ( string.IsNullOrWhiteSpace( sourceText ) )
			return null;

		var folder = SourceFolderForKind( kind );
		var fileName = SourceFileNameForResource( kind, id, sourcePath );
		var relativePath = $"{folder}/{fileName}";
		var absoluteDir = Abs( folder );
		if ( !Directory.Exists( absoluteDir ) )
			Directory.CreateDirectory( absoluteDir );

		File.WriteAllText( Abs( relativePath ), sourceText );
		return relativePath;
	}

	public static List<SourceSummary> LoadSourceSummaries( string kind )
	{
		var folder = SourceFolderForKind( kind );
		var absoluteDir = Abs( folder );
		if ( !Directory.Exists( absoluteDir ) )
			return new List<SourceSummary>();

		return Directory.GetFiles( absoluteDir, "*.y*ml" )
			.Select( path => TryReadSourceSummary( kind, path ) )
			.Where( summary => summary != null )
			.OrderBy( summary => summary.Id, StringComparer.OrdinalIgnoreCase )
			.ToList();
	}

	public static List<JsonElement> LoadSourceCanonicalResources( string kind )
	{
		var folder = SourceFolderForKind( kind );
		var absoluteDir = Abs( folder );
		if ( !Directory.Exists( absoluteDir ) )
			return new List<JsonElement>();

		return Directory.GetFiles( absoluteDir, $"*.{kind}.yml" )
			.Concat( Directory.GetFiles( absoluteDir, $"*.{kind}.yaml" ) )
			.OrderBy( path => path, StringComparer.OrdinalIgnoreCase )
			.Select( path => TryLoadSourceCanonicalResource( kind, path, out var resource ) ? resource : default )
			.Where( resource => resource.ValueKind != JsonValueKind.Undefined )
			.ToList();
	}

	public static bool TryLoadSourceCanonicalResource( string kind, string absolutePath, out JsonElement resource )
	{
		resource = default;

		try
		{
			var sourceText = File.ReadAllText( absolutePath );
			return TryParseSourceText( kind, sourceText, absolutePath, out resource );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Failed to load source {absolutePath}: {ex.Message}" );
			return false;
		}
	}

	private static bool TryParseSourceText( string kind, string sourceText, string sourcePath, out JsonElement resource )
	{
		resource = default;

		var lines = sourceText.Replace( "\r\n", "\n" ).Replace( '\r', '\n' ).Split( '\n' );
		var values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var definitionLine = -1;

		for ( var i = 0; i < lines.Length; i++ )
		{
			var raw = lines[i];
			if ( string.IsNullOrWhiteSpace( raw ) )
				continue;
			if ( char.IsWhiteSpace( raw[0] ) )
				continue;

			var line = raw.Trim();
			if ( line.StartsWith( "#" ) )
				continue;

			var colon = FindTopLevelColon( line );
			if ( colon <= 0 )
				continue;

			var key = line[..colon].Trim();
			var value = line[(colon + 1)..].Trim();
			if ( key.Equals( "definition", StringComparison.OrdinalIgnoreCase ) )
			{
				definitionLine = i;
				if ( !string.IsNullOrWhiteSpace( value ) )
					values["definition"] = JsonSerializer.Serialize( ParseYamlScalar( value ) );
				break;
			}

			values[key] = DecodeYamlScalar( value );
		}

		if ( definitionLine < 0 && values.TryGetValue( "sourceText", out var embeddedSourceText ) )
			return TryParseSourceText( kind, embeddedSourceText, sourcePath, out resource );

		if ( !values.TryGetValue( "kind", out var actualKind )
			|| !actualKind.Equals( kind, StringComparison.OrdinalIgnoreCase ) )
			return false;

		if ( !values.TryGetValue( "id", out var id ) || string.IsNullOrWhiteSpace( id ) )
			id = ResourceIdFromFilePath( sourcePath, kind );
		if ( string.IsNullOrWhiteSpace( id ) || definitionLine < 0 )
			return false;

		var definitionJson = values.TryGetValue( "definition", out var inlineDefinition )
			? inlineDefinition
			: SerializeIndentedDefinitionYamlAsJson( sourcePath, lines, definitionLine + 1 );
		if ( string.IsNullOrWhiteSpace( definitionJson ) )
			return false;

		using var definitionDoc = JsonDocument.Parse( definitionJson );
		if ( definitionDoc.RootElement.ValueKind != JsonValueKind.Object )
			return false;

		var canonical = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
		switch ( kind )
		{
			case "collection":
				canonical["id"] = id;
				canonical["name"] = id;
				break;
			case "endpoint":
				canonical["id"] = id;
				canonical["slug"] = id;
				break;
			default:
				canonical["id"] = id;
				break;
		}

		foreach ( var key in new[] { "name", "description", "notes" } )
		{
			if ( values.TryGetValue( key, out var value ) && !string.IsNullOrWhiteSpace( value ) )
				canonical[key] = value;
		}

		canonical["authoringMode"] = "source";
		canonical["sourceFormat"] = "yaml";
		canonical["sourcePath"] = Path.GetFileName( sourcePath );
		canonical["sourceText"] = sourceText;
		if ( values.TryGetValue( "sourceVersion", out var sourceVersion ) && !string.IsNullOrWhiteSpace( sourceVersion ) )
			canonical["sourceVersion"] = sourceVersion;

		foreach ( var property in definitionDoc.RootElement.EnumerateObject() )
			canonical[property.Name] = property.Value.Clone();

		resource = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( canonical ) );
		return resource.ValueKind == JsonValueKind.Object;
	}

	public static bool HasSourceFiles()
	{
		return HasSourceFiles( CollectionsPath )
			|| HasSourceFiles( EndpointsPath )
			|| HasSourceFiles( WorkflowsPath )
			|| HasSourceFiles( TestsPath )
			|| HasSourceFiles( LibrariesPath );
	}

	private static bool HasSourceFiles( string relativeFolder )
	{
		var absoluteDir = Abs( relativeFolder );
		return Directory.Exists( absoluteDir )
			&& (Directory.GetFiles( absoluteDir, "*.yaml" ).Length > 0
				|| Directory.GetFiles( absoluteDir, "*.yml" ).Length > 0);
	}

	public static string ResourceIdFromFilePath( string filePath, string kind )
	{
		var fileName = Path.GetFileName( filePath );
		foreach ( var suffix in new[] { $".{kind}.yaml", $".{kind}.yml" } )
		{
			if ( fileName.EndsWith( suffix, StringComparison.OrdinalIgnoreCase ) )
				return fileName[..^suffix.Length];
		}

		var withoutExtension = Path.GetFileNameWithoutExtension( fileName );
		var typedSuffix = $".{kind}";
		return withoutExtension.EndsWith( typedSuffix, StringComparison.OrdinalIgnoreCase )
			? withoutExtension[..^typedSuffix.Length]
			: withoutExtension;
	}

	private static SourceSummary TryReadSourceSummary( string expectedKind, string absolutePath )
	{
		try
		{
			var values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			foreach ( var raw in File.ReadLines( absolutePath ) )
			{
				if ( string.IsNullOrWhiteSpace( raw ) )
					continue;
				if ( char.IsWhiteSpace( raw[0] ) )
					continue;

				var line = raw.Trim();
				if ( line.StartsWith( "#" ) )
					continue;

				var colon = line.IndexOf( ':' );
				if ( colon <= 0 )
					continue;

				var key = line[..colon].Trim();
				if ( key == "definition" )
					break;

				var value = UnquoteYamlScalar( line[(colon + 1)..].Trim() );
				values[key] = value;
			}

			if ( !values.TryGetValue( "kind", out var kind ) || kind != expectedKind )
				return null;

			var fallbackId = SourceIdFromFileName( expectedKind, Path.GetFileName( absolutePath ) );
			values.TryGetValue( "id", out var id );
			if ( string.IsNullOrWhiteSpace( id ) )
				id = fallbackId;
			if ( string.IsNullOrWhiteSpace( id ) )
				return null;

			values.TryGetValue( "name", out var name );
			values.TryGetValue( "description", out var description );
			return new SourceSummary
			{
				Kind = kind,
				Id = id,
				Name = name ?? "",
				Description = description ?? "",
				Path = absolutePath
			};
		}
		catch
		{
			return null;
		}
	}

	private static string SourceFolderForKind( string kind ) => kind switch
	{
		"collection" => CollectionsPath,
		"endpoint" => EndpointsPath,
		"workflow" => WorkflowsPath,
		"test" => TestsPath,
		"library" => LibrariesPath,
		_ => SyncToolsPath
	};

	private static string SourceFileNameForResource( string kind, string id, string sourcePath )
	{
		var incomingName = Path.GetFileName( (sourcePath ?? "").Replace( '\\', '/' ) );
		if ( incomingName.EndsWith( $".{kind}.yaml", StringComparison.OrdinalIgnoreCase )
			|| incomingName.EndsWith( $".{kind}.yml", StringComparison.OrdinalIgnoreCase ) )
			return incomingName;

		return $"{SafeResourceFileName( id )}.{kind}.yml";
	}

	private static string SourceIdFromFileName( string kind, string fileName )
	{
		foreach ( var suffix in new[] { $".{kind}.yaml", $".{kind}.yml" } )
		{
			if ( fileName.EndsWith( suffix, StringComparison.OrdinalIgnoreCase ) )
				return fileName[..^suffix.Length];
		}
		return Path.GetFileNameWithoutExtension( fileName );
	}

	private static string SafeResourceFileName( string value )
	{
		var chars = (value ?? "unknown")
			.Select( ch => char.IsLetterOrDigit( ch ) || ch is '_' or '-' or '.' ? ch : '_' )
			.ToArray();
		var safe = new string( chars ).Trim( '.' );
		return string.IsNullOrWhiteSpace( safe ) ? "unknown" : safe;
	}

	private static string UnquoteYamlScalar( string value )
	{
		if ( value.Length >= 2 )
		{
			var first = value[0];
			var last = value[^1];
			if ( (first == '"' && last == '"') || (first == '\'' && last == '\'') )
				return value[1..^1];
		}
		return value;
	}

	private static string DecodeYamlScalar( string value )
	{
		if ( value.Length >= 2 && value[0] == '"' && value[^1] == '"' )
		{
			try { return JsonSerializer.Deserialize<string>( value ); }
			catch { return value[1..^1]; }
		}

		if ( value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' )
			return value[1..^1].Replace( "''", "'" );

		return value;
	}

	private static string SerializeIndentedDefinitionYamlAsJson( string absolutePath, string[] lines, int startLine )
	{
		var index = startLine;
		var parsed = ParseYamlNode( absolutePath, lines, ref index, 2 );
		return parsed is null ? "" : JsonSerializer.Serialize( parsed );
	}

	private static object ParseYamlNode( string absolutePath, string[] lines, ref int index, int indent )
	{
		SkipBlankLines( lines, ref index );
		if ( index >= lines.Length )
			return null;

		var line = lines[index];
		if ( CountLeadingSpaces( line ) < indent )
			return null;

		var trimmed = line[indent..];
		return trimmed.StartsWith( "-", StringComparison.Ordinal )
			? ParseYamlList( absolutePath, lines, ref index, indent )
			: ParseYamlMap( absolutePath, lines, ref index, indent );
	}

	private static Dictionary<string, object> ParseYamlMap( string absolutePath, string[] lines, ref int index, int indent )
	{
		var result = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );

		while ( index < lines.Length )
		{
			if ( string.IsNullOrWhiteSpace( lines[index] ) )
			{
				index++;
				continue;
			}

			var currentIndent = CountLeadingSpaces( lines[index] );
			if ( currentIndent < indent )
				break;
			if ( currentIndent > indent )
				throw new FormatException( $"Unexpected indentation in {absolutePath} at line {index + 1}." );

			var content = lines[index][indent..];
			if ( content.StartsWith( "-", StringComparison.Ordinal ) )
				break;

			var colon = FindTopLevelColon( content );
			if ( colon <= 0 )
				throw new FormatException( $"Invalid YAML mapping entry in {absolutePath} at line {index + 1}: {content}" );

			var key = content[..colon].Trim();
			var valueText = content[(colon + 1)..].Trim();
			index++;

			if ( string.IsNullOrEmpty( valueText ) )
			{
				var child = ParseYamlNode( absolutePath, lines, ref index, indent + 2 );
				result[key] = child ?? new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
			}
			else
			{
				result[key] = ParseYamlScalar( valueText );
			}
		}

		return result;
	}

	private static List<object> ParseYamlList( string absolutePath, string[] lines, ref int index, int indent )
	{
		var result = new List<object>();

		while ( index < lines.Length )
		{
			if ( string.IsNullOrWhiteSpace( lines[index] ) )
			{
				index++;
				continue;
			}

			var currentIndent = CountLeadingSpaces( lines[index] );
			if ( currentIndent < indent )
				break;
			if ( currentIndent > indent )
				throw new FormatException( $"Unexpected list indentation in {absolutePath} at line {index + 1}." );

			var content = lines[index][indent..];
			if ( !content.StartsWith( "-", StringComparison.Ordinal ) )
				break;

			var itemText = content[1..].TrimStart();
			index++;

			if ( string.IsNullOrEmpty( itemText ) )
			{
				var child = ParseYamlNode( absolutePath, lines, ref index, indent + 2 );
				result.Add( child ?? new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase ) );
			}
			else if ( TryParseInlineMapEntry( itemText, out var inlineMap ) )
			{
				var continuation = ParseYamlNode( absolutePath, lines, ref index, indent + 2 );
				if ( continuation is Dictionary<string, object> continuationMap )
				{
					foreach ( var pair in continuationMap )
						inlineMap[pair.Key] = pair.Value;
				}

				result.Add( inlineMap );
			}
			else
			{
				result.Add( ParseYamlScalar( itemText ) );
			}
		}

		return result;
	}

	private static object ParseYamlScalar( string value )
	{
		if ( value == "{}" )
			return new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
		if ( value == "[]" )
			return new List<object>();
		if ( LooksLikeFlowMap( value ) )
			return ParseFlowMap( value );
		if ( LooksLikeFlowList( value ) )
			return ParseFlowList( value );
		if ( string.Equals( value, "null", StringComparison.OrdinalIgnoreCase ) )
			return null;
		if ( string.Equals( value, "true", StringComparison.OrdinalIgnoreCase ) )
			return true;
		if ( string.Equals( value, "false", StringComparison.OrdinalIgnoreCase ) )
			return false;

		var decoded = DecodeYamlScalar( value );
		if ( decoded != value )
			return decoded;

		if ( long.TryParse( value, out var integer ) )
			return integer;
		if ( double.TryParse( value, out var number ) )
			return number;

		return value;
	}

	private static bool TryParseInlineMapEntry( string value, out Dictionary<string, object> map )
	{
		map = null;
		var colon = FindTopLevelColon( value );
		if ( colon <= 0 )
			return false;

		map = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
		var key = value[..colon].Trim();
		var valueText = value[(colon + 1)..].Trim();
		map[key] = string.IsNullOrEmpty( valueText )
			? new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase )
			: ParseYamlScalar( valueText );
		return true;
	}

	private static bool LooksLikeFlowMap( string value )
	{
		return value.Length >= 2
			&& value[0] == '{'
			&& value[^1] == '}'
			&& FindTopLevelColon( value[1..^1] ) > 0;
	}

	private static bool LooksLikeFlowList( string value )
	{
		return value.Length >= 2 && value[0] == '[' && value[^1] == ']';
	}

	private static Dictionary<string, object> ParseFlowMap( string value )
	{
		var result = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
		var inner = value[1..^1].Trim();
		if ( string.IsNullOrWhiteSpace( inner ) )
			return result;

		foreach ( var entry in SplitTopLevel( inner, ',' ) )
		{
			if ( string.IsNullOrWhiteSpace( entry ) )
				continue;

			var colon = FindTopLevelColon( entry );
			if ( colon <= 0 )
				throw new FormatException( $"Invalid YAML flow mapping entry: {entry}" );

			var key = DecodeYamlScalar( entry[..colon].Trim() );
			var scalar = entry[(colon + 1)..].Trim();
			result[key] = string.IsNullOrEmpty( scalar ) ? null : ParseYamlScalar( scalar );
		}

		return result;
	}

	private static List<object> ParseFlowList( string value )
	{
		var result = new List<object>();
		var inner = value[1..^1].Trim();
		if ( string.IsNullOrWhiteSpace( inner ) )
			return result;

		foreach ( var entry in SplitTopLevel( inner, ',' ) )
			result.Add( ParseYamlScalar( entry.Trim() ) );

		return result;
	}

	private static List<string> SplitTopLevel( string value, char separator )
	{
		var parts = new List<string>();
		var start = 0;
		var depth = 0;
		var quote = '\0';
		var escape = false;

		for ( var i = 0; i < value.Length; i++ )
		{
			var ch = value[i];
			if ( quote != '\0' )
			{
				if ( escape )
				{
					escape = false;
					continue;
				}
				if ( ch == '\\' && quote == '"' )
				{
					escape = true;
					continue;
				}
				if ( ch == quote )
					quote = '\0';
				continue;
			}

			if ( ch is '"' or '\'' )
			{
				quote = ch;
				continue;
			}
			if ( ch is '{' or '[' or '(' )
			{
				depth++;
				continue;
			}
			if ( ch is '}' or ']' or ')' )
			{
				depth = Math.Max( 0, depth - 1 );
				continue;
			}
			if ( ch == separator && depth == 0 )
			{
				parts.Add( value[start..i] );
				start = i + 1;
			}
		}

		parts.Add( value[start..] );
		return parts;
	}

	private static int FindTopLevelColon( string value )
	{
		var depth = 0;
		var quote = '\0';
		var escape = false;

		for ( var i = 0; i < value.Length; i++ )
		{
			var ch = value[i];
			if ( quote != '\0' )
			{
				if ( escape )
				{
					escape = false;
					continue;
				}
				if ( ch == '\\' && quote == '"' )
				{
					escape = true;
					continue;
				}
				if ( ch == quote )
					quote = '\0';
				continue;
			}

			if ( ch is '"' or '\'' )
			{
				quote = ch;
				continue;
			}
			if ( ch is '{' or '[' or '(' )
			{
				depth++;
				continue;
			}
			if ( ch is '}' or ']' or ')' )
			{
				depth = Math.Max( 0, depth - 1 );
				continue;
			}
			if ( ch == ':' && depth == 0 )
				return i;
		}

		return -1;
	}

	private static void SkipBlankLines( string[] lines, ref int index )
	{
		while ( index < lines.Length && string.IsNullOrWhiteSpace( lines[index] ) )
			index++;
	}

	private static int CountLeadingSpaces( string value )
	{
		var count = 0;
		while ( count < value.Length && value[count] == ' ' )
			count++;
		return count;
	}
}
