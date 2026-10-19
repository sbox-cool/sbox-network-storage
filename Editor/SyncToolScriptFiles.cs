using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

/// <summary>
/// Endpoint script files (issue #45). An endpoint envelope <c>&lt;id&gt;.endpoint.yml</c> with
/// <c>runtime: javascript|typescript</c> keeps its code in a sibling <c>&lt;id&gt;.endpoint.js</c> or
/// <c>.ts</c>. Push inlines that file as <c>script</c>, the form the server stores. Pull moves an inline
/// <c>script</c> back into the file. The rules match Editor/endpoint_scripts.py.
/// </summary>
public static partial class SyncToolConfig
{
	private static readonly string[] EndpointScriptExtensions = { ".js", ".ts" };

	private static readonly HttpClient TypingsHttp = new() { Timeout = TimeSpan.FromSeconds( 15 ) };

	/// <summary>Project-relative path of the endpoint typings the Sync Tool keeps for editors.</summary>
	public static string EndpointTypingsPath => $"{SyncToolsPath}/types/ns.d.ts";

	/// <summary>The script file extension for a runtime, or null when the runtime has no script file.</summary>
	public static string EndpointScriptExtension( string runtime ) => runtime switch
	{
		"javascript" => ".js",
		"typescript" => ".ts",
		_ => null
	};

	/// <summary>
	/// Loader entry point: endpoint envelopes get their script file inlined, other kinds pass through.
	/// A script-file problem is logged and the envelope is sent as written, so the server reports it.
	/// </summary>
	public static string InlineSourceScript( string kind, string absolutePath, string sourceText )
	{
		if ( !kind.Equals( "endpoint", StringComparison.OrdinalIgnoreCase ) )
			return sourceText;

		var inlined = InlineEndpointScriptSource( absolutePath, sourceText, out var error );
		if ( error != null )
			Log.Warning( $"[SyncTool] {error}" );
		return inlined;
	}

	/// <summary>
	/// Returns the endpoint envelope text with its script file inlined as <c>script</c>. Envelopes without a
	/// script file come back unchanged. On a script-file problem the text comes back unchanged and
	/// <paramref name="error"/> says what to fix.
	/// </summary>
	public static string InlineEndpointScriptSource( string envelopePath, string sourceText, out string error )
	{
		error = null;
		if ( !TryParseSourceText( "endpoint", sourceText, envelopePath, out var root ) || root.ValueKind != JsonValueKind.Object )
			return sourceText;

		var envelopeName = Path.GetFileName( envelopePath );
		var endpointId = ResourceIdFromFilePath( envelopePath, "endpoint" );
		var directory = Path.GetDirectoryName( envelopePath );
		var runtime = ScriptStringProperty( root, "runtime" );
		var expectedExt = EndpointScriptExtension( runtime );
		var hasScriptFile = root.TryGetProperty( "scriptFile", out _ );
		var hasInlineScript = root.TryGetProperty( "script", out _ );

		if ( expectedExt == null )
		{
			if ( hasScriptFile )
			{
				error = $"{envelopeName}: scriptFile needs runtime: javascript or runtime: typescript";
				return sourceText;
			}

			var stray = EndpointScriptExtensions
				.Select( ext => Path.Combine( directory, $"{endpointId}.endpoint{ext}" ) )
				.FirstOrDefault( path => File.Exists( path ) );
			if ( stray != null )
				error = $"{envelopeName}: {Path.GetFileName( stray )} exists but runtime is not javascript or typescript";
			return sourceText;
		}

		string scriptPath;
		if ( hasScriptFile )
		{
			var scriptFile = ScriptStringProperty( root, "scriptFile" );
			if ( !IsBareScriptFileName( scriptFile ) )
			{
				error = $"{envelopeName}: scriptFile must be a file name next to the endpoint, without folders";
				return sourceText;
			}
			if ( !scriptFile.EndsWith( expectedExt, StringComparison.Ordinal ) )
			{
				error = $"{envelopeName}: scriptFile {scriptFile} must end with {expectedExt} for runtime {runtime}";
				return sourceText;
			}

			scriptPath = Path.Combine( directory, scriptFile );
			if ( !File.Exists( scriptPath ) )
			{
				error = $"{envelopeName}: scriptFile {scriptFile} not found";
				return sourceText;
			}
		}
		else
		{
			var other = EndpointScriptExtensions
				.Where( ext => ext != expectedExt )
				.Select( ext => Path.Combine( directory, $"{endpointId}.endpoint{ext}" ) )
				.FirstOrDefault( path => File.Exists( path ) );
			if ( other != null )
			{
				error = $"{envelopeName}: {Path.GetFileName( other )} exists but runtime is {runtime}; remove one of the two files";
				return sourceText;
			}

			scriptPath = Path.Combine( directory, $"{endpointId}.endpoint{expectedExt}" );
			if ( !File.Exists( scriptPath ) )
				return sourceText; // runtime without a file: the server reports MISSING_SCRIPT
		}

		var scriptName = Path.GetFileName( scriptPath );
		if ( IsJsonPath( envelopePath ) )
		{
			error = $"{envelopeName}: script files need a .endpoint.yml envelope; a JSON envelope cannot reference {scriptName}";
			return sourceText;
		}
		if ( hasInlineScript )
		{
			error = $"{envelopeName}: script is set both inline and in {scriptName}; keep one";
			return sourceText;
		}

		string script;
		try
		{
			script = new UTF8Encoding( false, true ).GetString( File.ReadAllBytes( scriptPath ) );
		}
		catch ( DecoderFallbackException )
		{
			error = $"{envelopeName}: {scriptName} is not valid UTF-8";
			return sourceText;
		}
		if ( script.Length > 0 && script[0] == '\uFEFF' )
			script = script.Substring( 1 );

		return AppendScriptLine( sourceText, script );
	}

	/// <summary>
	/// Pull side: moves an endpoint's inline <c>script</c> into its sibling script file. Returns the envelope
	/// to write, with <c>scriptFile</c> in place of <c>script</c>. Returns the data unchanged, with
	/// <paramref name="scriptFileName"/> null, when there is nothing to split. A JSON envelope keeps its script
	/// inline because a JSON envelope cannot reference a script file.
	/// </summary>
	public static Dictionary<string, object> SplitEndpointScript( string endpointId, Dictionary<string, object> data, string sourcePath, out string scriptFileName, out string scriptText )
	{
		scriptFileName = null;
		scriptText = null;
		if ( IsJsonPath( sourcePath ) )
			return data;

		var runtime = data.TryGetValue( "runtime", out var runtimeValue ) ? StringValue( runtimeValue ) : null;
		var ext = EndpointScriptExtension( runtime );
		var text = data.TryGetValue( "script", out var scriptValue ) ? StringValue( scriptValue ) : null;
		if ( ext == null || text == null )
			return data;

		scriptFileName = $"{endpointId}.endpoint{ext}";
		scriptText = text;

		var envelope = new Dictionary<string, object>( data );
		envelope.Remove( "script" );
		envelope["scriptFile"] = scriptFileName;
		return envelope;
	}

	/// <summary>
	/// Pull side for raw endpoint source text, used when the server sends no compiled view. Returns the envelope
	/// as data when it is a script endpoint whose script must move to a file. Returns null when the raw text can
	/// be saved as it is.
	/// </summary>
	public static Dictionary<string, object> RawEndpointToSplit( string sourceText, string sourcePath )
	{
		if ( IsJsonPath( sourcePath ) || !TryParseSourceText( "endpoint", sourceText, sourcePath, out var root ) )
			return null;
		if ( root.ValueKind != JsonValueKind.Object || !root.TryGetProperty( "script", out _ ) )
			return null;
		if ( EndpointScriptExtension( ScriptStringProperty( root, "runtime" ) ) == null )
			return null;

		return JsonSerializer.Deserialize<Dictionary<string, object>>( root.GetRawText() );
	}

	/// <summary>Writes a pulled script file as UTF-8 without a byte order mark, exactly as the server stored it.</summary>
	public static void WriteEndpointScriptFile( string folder, string fileName, string scriptText )
	{
		if ( !Directory.Exists( folder ) )
			Directory.CreateDirectory( folder );
		File.WriteAllText( Path.Combine( folder, fileName ), scriptText, new UTF8Encoding( false ) );
	}

	/// <summary>
	/// Encodes text as a YAML double-quoted scalar that reads back byte-for-byte. The server's YAML subset
	/// decodes double-quoted scalars as JSON strings, so JSON escapes are used, plus escapes for characters
	/// YAML 1.2 does not treat as printable or that some parsers fold as line breaks.
	/// </summary>
	public static string EncodeYamlDoubleQuoted( string text )
	{
		var builder = new StringBuilder( text.Length + 2 );
		builder.Append( '"' );
		foreach ( var c in text )
		{
			switch ( c )
			{
				case '"': builder.Append( "\\\"" ); break;
				case '\\': builder.Append( "\\\\" ); break;
				case '\n': builder.Append( "\\n" ); break;
				case '\r': builder.Append( "\\r" ); break;
				case '\t': builder.Append( "\\t" ); break;
				default:
					if ( c < 0x20 || (c >= 0x7F && c <= 0x9F) || c == '\u2028' || c == '\u2029' || c == '\uFEFF' )
						builder.Append( "\\u" ).Append( ((int)c).ToString( "X4" ) );
					else
						builder.Append( c );
					break;
			}
		}
		builder.Append( '"' );
		return builder.ToString();
	}

	/// <summary>True when any local endpoint envelope uses a script file, which is when the typings are kept.</summary>
	public static bool HasScriptEndpoints()
	{
		var dir = Abs( SourceFolderForKind( "endpoint" ) );
		return Directory.Exists( dir )
			&& FindPayloadSourceFiles( "endpoint", dir ).Any( path => EndpointScriptFileFor( path ) != null );
	}

	/// <summary>Script-file problems across all endpoint envelopes. Push refuses to run while any exist.</summary>
	public static List<string> EndpointScriptProblems()
	{
		var problems = new List<string>();
		var dir = Abs( SourceFolderForKind( "endpoint" ) );
		if ( !Directory.Exists( dir ) )
			return problems;

		foreach ( var path in FindPayloadSourceFiles( "endpoint", dir ) )
		{
			var error = EndpointScriptError( path );
			if ( error != null )
				problems.Add( error );
		}
		return problems;
	}

	/// <summary>The script-file problem in one endpoint envelope, or null when its script resolves.</summary>
	public static string EndpointScriptError( string envelopePath )
	{
		string text;
		try
		{
			text = File.ReadAllText( envelopePath );
		}
		catch ( Exception ex )
		{
			return $"{Path.GetFileName( envelopePath )}: {ex.Message}";
		}

		InlineEndpointScriptSource( envelopePath, text, out var error );
		return error;
	}

	/// <summary>
	/// Downloads the server's endpoint typings (ns.d.ts) into the project. Returns null on success, otherwise
	/// a message for the Sync log. A failed download never blocks a push or pull.
	/// </summary>
	public static async Task<string> RefreshEndpointTypingsAsync()
	{
		try
		{
			using var response = await TypingsHttp.GetAsync( $"{(BaseUrl ?? "").TrimEnd( '/' )}/owner-assets/endpoint-api/ns.d.ts" );
			if ( !response.IsSuccessStatusCode )
				return $"ns.d.ts download failed: HTTP {(int)response.StatusCode}";

			var typings = await response.Content.ReadAsStringAsync();
			var path = Abs( EndpointTypingsPath );
			var dir = Path.GetDirectoryName( path );
			if ( !Directory.Exists( dir ) )
				Directory.CreateDirectory( dir );
			File.WriteAllText( path, typings, new UTF8Encoding( false ) );
			return null;
		}
		catch ( Exception ex )
		{
			return $"ns.d.ts download failed: {ex.Message}";
		}
	}

	/// <summary>Keeps the typings in step when the project has script endpoints. Projects without them are untouched.</summary>
	public static async Task SyncEndpointTypingsAsync()
	{
		if ( !HasScriptEndpoints() )
			return;

		var warning = await RefreshEndpointTypingsAsync();
		if ( warning != null )
			Log.Warning( $"[SyncTool] {warning}" );
	}

	private static string AppendScriptLine( string sourceText, string script )
	{
		var body = sourceText ?? "";
		if ( body.Length > 0 && !body.EndsWith( "\n", StringComparison.Ordinal ) )
			body += "\n";
		return body + "script: " + EncodeYamlDoubleQuoted( script ) + "\n";
	}

	/// <summary>A file name only: no folders, no drive letters, no parent references.</summary>
	private static bool IsBareScriptFileName( string name ) =>
		!string.IsNullOrEmpty( name ) && name != "." && name != ".." && name.IndexOfAny( new[] { '/', '\\', ':' } ) < 0;

	private static string ScriptStringProperty( JsonElement obj, string name ) =>
		obj.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static bool IsJsonPath( string path ) => path != null && Path.GetExtension( path ).Equals( ".json", StringComparison.OrdinalIgnoreCase );

	/// <summary>A string from a plain string or a parsed JSON value. Anything else is null.</summary>
	private static string StringValue( object value ) => value switch
	{
		string text => text,
		JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
		_ => null
	};

	/// <summary>
	/// The script file an endpoint envelope uses: its scriptFile, or the implicit sibling for a javascript or
	/// typescript runtime. Returns null when there is none, or when the envelope cannot be read.
	/// </summary>
	public static string EndpointScriptFileFor( string envelopePath )
	{
		try
		{
			if ( !TryParseSourceText( "endpoint", File.ReadAllText( envelopePath ), envelopePath, out var root ) || root.ValueKind != JsonValueKind.Object )
				return null;

			var directory = Path.GetDirectoryName( envelopePath );
			var scriptFile = ScriptStringProperty( root, "scriptFile" );
			if ( scriptFile != null )
				return IsBareScriptFileName( scriptFile ) ? Path.Combine( directory, scriptFile ) : null;

			var ext = EndpointScriptExtension( ScriptStringProperty( root, "runtime" ) );
			return ext == null ? null : Path.Combine( directory, $"{ResourceIdFromFilePath( envelopePath, "endpoint" )}.endpoint{ext}" );
		}
		catch ( Exception )
		{
			return null;
		}
	}

	/// <summary>The same object without one property. Returns the input unchanged when the property is absent.</summary>
	public static JsonElement WithoutProperty( JsonElement obj, string name )
	{
		if ( obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty( name, out _ ) )
			return obj;

		var copy = new Dictionary<string, JsonElement>( StringComparer.Ordinal );
		foreach ( var property in obj.EnumerateObject() )
		{
			if ( property.Name != name )
				copy[property.Name] = property.Value;
		}
		return JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( copy ) );
	}
}
