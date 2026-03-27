using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sandbox;
using Editor;

/// <summary>
/// Manages the sync tool configuration for the Network Storage library.
///
/// Config is split into two files for security:
///   config/public/projectConfig.json  — project ID, public key, base URL, API version, preferences
///                                       (safe to commit, ships with published game)
///   config/secret/secret_key.json     — secret key ONLY (gitignored, editor-only, NEVER published)
///
/// The public config is also written to the project root as network-storage.credentials.json
/// so the runtime client can auto-configure via s&box's sandboxed FileSystem.
/// </summary>
public static class SyncToolConfig
{
	public enum DataSourceMode { ApiThenJson, ApiOnly, JsonOnly }

	// ── Credentials ──
	public static string SecretKey { get; private set; } = "";
	public static string PublicApiKey { get; private set; } = "";
	public static string ProjectId { get; private set; } = "";
	public static string BaseUrl { get; private set; } = "https://api.sboxcool.com";
	public static string CdnUrl { get; private set; } = "";
	public static string ApiVersion { get; private set; } = "v3";

	// ── Preferences ──
	public static DataSourceMode DataSource { get; private set; } = DataSourceMode.ApiThenJson;

	// ── Configurable data folder ──
	private static string _dataFolder = "Network Storage";

	/// <summary>The subfolder name under Editor/ where sync data lives. Configurable in Setup.</summary>
	public static string DataFolder
	{
		get => _dataFolder;
		set => _dataFolder = string.IsNullOrWhiteSpace( value ) ? "Network Storage" : value.Trim();
	}

	// ── Validation ──

	/// <summary>True when all required credentials are present (project ID + both keys). Does not enforce key prefixes.</summary>
	public static bool IsValid => !string.IsNullOrEmpty( SecretKey )
		&& !string.IsNullOrEmpty( PublicApiKey )
		&& !string.IsNullOrEmpty( ProjectId );

	/// <summary>True when keys use the standard sbox_sk_ / sbox_ns_ prefixes.</summary>
	public static bool HasStandardPrefixes =>
		( string.IsNullOrEmpty( SecretKey ) || SecretKey.StartsWith( "sbox_sk_" ) )
		&& ( string.IsNullOrEmpty( PublicApiKey ) || PublicApiKey.StartsWith( "sbox_ns_" ) );

	public static bool HasPublicKey => !string.IsNullOrEmpty( PublicApiKey );

	public static bool IsFullyConfigured => IsValid;

	// ── Paths ──

	/// <summary>
	/// Absolute path to the project root directory.
	/// Derived from Sandbox.FileSystem.Mounted (which points to {project}/assets).
	/// Editor.FileSystem.Root is the ENGINE dir and must NOT be used for project files.
	/// </summary>
	private static string _projectRoot;
	public static string ProjectRoot
	{
		get
		{
			if ( _projectRoot == null )
			{
				var assetsPath = Sandbox.FileSystem.Mounted.GetFullPath( "" );
				_projectRoot = Path.GetDirectoryName( assetsPath );
			}
			return _projectRoot;
		}
	}

	/// <summary>Resolve a project-relative path to an absolute path.</summary>
	public static string Abs( string relativePath ) => Path.Combine( ProjectRoot, relativePath.Replace( '/', Path.DirectorySeparatorChar ) );

	/// <summary>Root path for all sync data: Editor/{DataFolder}/</summary>
	public static string SyncToolsPath => $"Editor/{DataFolder}";

	/// <summary>Path to config/ directory.</summary>
	public static string ConfigPath => $"{SyncToolsPath}/config";

	/// <summary>Path to config/public/ — safe to commit, ships with game.</summary>
	public static string PublicConfigPath => $"{ConfigPath}/public";

	/// <summary>Path to config/secret/ — gitignored, editor-only, NEVER published.</summary>
	public static string SecretConfigPath => $"{ConfigPath}/secret";

	/// <summary>Path to the public project config file.</summary>
	public static string ProjectConfigFile => $"{PublicConfigPath}/projectConfig.json";

	/// <summary>Path to the secret key file.</summary>
	public static string SecretKeyFile => $"{SecretConfigPath}/secret_key.json";

	/// <summary>Path to the runtime credentials file in Assets/ (shipped with game).</summary>
	public static string RuntimeCredentialsFile => "Assets/network-storage.credentials.json";

	/// <summary>Path to collections/ directory.</summary>
	public static string CollectionsPath => $"{SyncToolsPath}/collections";

	/// <summary>Path to the endpoints directory.</summary>
	public static string EndpointsPath => $"{SyncToolsPath}/endpoints";

	/// <summary>Path to the workflows directory.</summary>
	public static string WorkflowsPath => $"{SyncToolsPath}/workflows";

	/// <summary>Legacy paths for auto-migration.</summary>
	public static string LegacyCollectionSchemaPath => $"{SyncToolsPath}/collection_schema.json";

	// Legacy .env locations for migration
	private static string LegacyEnvInConfig => $"{ConfigPath}/.env";
	private static string LegacyEnvInRoot => $"{SyncToolsPath}/.env";
	private static string LegacyEnvInSyncTools => "Editor/SyncTools/.env";

	// ── JSON options ──
	private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

	// ── Filesystem helpers (System.IO with absolute paths) ──

	private static string[] FindFiles( string relativeDir, string pattern )
	{
		var absDir = Abs( relativeDir );
		if ( !Directory.Exists( absDir ) ) return Array.Empty<string>();
		return Directory.GetFiles( absDir, pattern ).Select( f => Path.GetFileName( f ) ).OrderBy( f => f ).ToArray();
	}

	private static void EnsureDir( string relativePath )
	{
		var absPath = Abs( relativePath );
		if ( !Directory.Exists( absPath ) )
			Directory.CreateDirectory( absPath );
	}

	// ──────────────────────────────────────────────────────
	//  Load / Save
	// ──────────────────────────────────────────────────────

	/// <summary>
	/// Load configuration from the split config files.
	/// Falls back to legacy .env files for migration.
	/// </summary>
	public static void Load()
	{
		SecretKey = "";
		PublicApiKey = "";
		ProjectId = "";
		BaseUrl = "https://api.sboxcool.com";
		CdnUrl = "";
		ApiVersion = "v3";
		DataSource = DataSourceMode.ApiThenJson;
		DataFolder = "Network Storage";

		// ── Try new split config first ──
		if ( File.Exists( Abs( ProjectConfigFile ) ) )
		{
			LoadPublicConfig( ProjectConfigFile );
			if ( File.Exists( Abs( SecretKeyFile ) ) )
				LoadSecretConfig( SecretKeyFile );
			return;
		}

		// ── Try legacy .env locations for migration ──
		string legacyEnv = null;
		if ( File.Exists( Abs( LegacyEnvInConfig ) ) )
			legacyEnv = LegacyEnvInConfig;
		else if ( File.Exists( Abs( LegacyEnvInRoot ) ) )
			legacyEnv = LegacyEnvInRoot;
		else if ( File.Exists( Abs( LegacyEnvInSyncTools ) ) )
			legacyEnv = LegacyEnvInSyncTools;

		if ( legacyEnv != null )
		{
			Log.Info( $"[SyncTool] Found legacy .env — migrating to split config on next save" );
			LoadLegacyEnv( legacyEnv );
			return;
		}

		// ── First install — scaffold ──
		ScaffoldProject();
	}

	private static void LoadPublicConfig( string path )
	{
		var json = JsonSerializer.Deserialize<JsonElement>( File.ReadAllText( Abs( path ) ) );
		ProjectId = json.TryGetProperty( "projectId", out var pid ) ? pid.GetString() ?? "" : "";
		PublicApiKey = json.TryGetProperty( "publicKey", out var pk ) ? pk.GetString() ?? "" : "";
		BaseUrl = json.TryGetProperty( "baseUrl", out var bu ) ? bu.GetString()?.TrimEnd( '/' ) ?? "https://api.sboxcool.com" : "https://api.sboxcool.com";
		CdnUrl = json.TryGetProperty( "cdnUrl", out var cu ) ? cu.GetString() ?? "" : "";
		ApiVersion = json.TryGetProperty( "apiVersion", out var av ) ? av.GetString()?.Trim( '/' ) ?? "v3" : "v3";
		DataFolder = json.TryGetProperty( "dataFolder", out var df ) ? df.GetString() ?? "Network Storage" : "Network Storage";
		if ( json.TryGetProperty( "dataSource", out var ds ) )
		{
			DataSource = ds.GetString()?.ToLowerInvariant() switch
			{
				"api_only" => DataSourceMode.ApiOnly,
				"json_only" => DataSourceMode.JsonOnly,
				_ => DataSourceMode.ApiThenJson
			};
		}
	}

	private static void LoadSecretConfig( string path )
	{
		var json = JsonSerializer.Deserialize<JsonElement>( File.ReadAllText( Abs( path ) ) );
		SecretKey = json.TryGetProperty( "secretKey", out var sk ) ? sk.GetString() ?? "" : "";
	}

	private static void LoadLegacyEnv( string envPath )
	{
		foreach ( var line in File.ReadAllText( Abs( envPath ) ).Split( '\n' ) )
		{
			var trimmed = line.Trim();
			if ( string.IsNullOrEmpty( trimmed ) || trimmed.StartsWith( '#' ) ) continue;
			var eq = trimmed.IndexOf( '=' );
			if ( eq < 0 ) continue;

			var key = trimmed[..eq].Trim();
			var val = trimmed[( eq + 1 )..].Trim();

			switch ( key )
			{
				case "SBOXCOOL_SECRET_KEY": SecretKey = val; break;
				case "SBOXCOOL_PUBLIC_KEY": PublicApiKey = val; break;
				case "SBOXCOOL_PROJECT_ID": ProjectId = val; break;
				case "SBOXCOOL_BASE_URL": BaseUrl = val.TrimEnd( '/' ); break;
			case "SBOXCOOL_CDN_URL": CdnUrl = val; break;
				case "SBOXCOOL_API_VERSION": ApiVersion = val.Trim( '/' ); break;
				case "SBOXCOOL_DATA_FOLDER": DataFolder = val; break;
				case "SBOXCOOL_DATA_SOURCE":
					DataSource = val.ToLowerInvariant() switch
					{
						"api_only" => DataSourceMode.ApiOnly,
						"json_only" => DataSourceMode.JsonOnly,
						_ => DataSourceMode.ApiThenJson
					};
					break;
			}
		}
	}

	/// <summary>
	/// Save configuration to split config files.
	/// Public config → config/public/projectConfig.json (safe to commit)
	/// Secret key   → config/secret/secret_key.json (gitignored, editor-only)
	/// Runtime copy → {project root}/network-storage.credentials.json (ships with game)
	/// </summary>
	public static void Save( string secretKey, string publicApiKey, string projectId,
		string baseUrl = null, DataSourceMode? dataSource = null, string dataFolder = null, string cdnUrl = null )
	{
		if ( dataFolder != null )
			DataFolder = dataFolder;

		EnsureSyncToolsDir();

		SecretKey = secretKey ?? "";
		PublicApiKey = publicApiKey ?? "";
		ProjectId = projectId ?? "";
		BaseUrl = ( baseUrl ?? "https://api.sboxcool.com" ).TrimEnd( '/' );
		CdnUrl = ( cdnUrl ?? "" ).TrimEnd( '/' );
		if ( dataSource.HasValue )
			DataSource = dataSource.Value;

		// ── Write public config (safe to commit) ──
		var publicConfig = new Dictionary<string, object>
		{
			["projectId"] = ProjectId,
			["publicKey"] = PublicApiKey,
			["baseUrl"] = BaseUrl,
			["cdnUrl"] = CdnUrl,
			["apiVersion"] = ApiVersion,
			["dataFolder"] = DataFolder,
			["dataSource"] = DataSource switch
			{
				DataSourceMode.ApiOnly => "api_only",
				DataSourceMode.JsonOnly => "json_only",
				_ => "api_then_json"
			}
		};
		File.WriteAllText( Abs( ProjectConfigFile ), JsonSerializer.Serialize( publicConfig, _jsonOptions ) );
		Log.Info( "[SyncTool] Public config saved to config/public/projectConfig.json" );

		// ── Write secret key (gitignored, NEVER published) ──
		var secretConfig = new Dictionary<string, string> { ["secretKey"] = SecretKey };
		File.WriteAllText( Abs( SecretKeyFile ), JsonSerializer.Serialize( secretConfig, _jsonOptions ) );
		Log.Info( "[SyncTool] Secret key saved to config/secret/secret_key.json" );

		// ── Write runtime credentials (ships with game, NO secret key) ──
		if ( !string.IsNullOrEmpty( ProjectId ) )
		{
			var runtimeCreds = new Dictionary<string, string>
			{
				["projectId"] = ProjectId,
				["publicKey"] = PublicApiKey,
				["baseUrl"] = BaseUrl,
				["cdnUrl"] = CdnUrl,
				["apiVersion"] = ApiVersion
			};
			var credsJson = JsonSerializer.Serialize( runtimeCreds, _jsonOptions );

			// Write to Assets/ (s&box mounts this for runtime FileSystem access)
			File.WriteAllText( Abs( RuntimeCredentialsFile ), credsJson );
			Log.Info( "[SyncTool] Runtime credentials written to Assets/network-storage.credentials.json" );
		}

		// ── Write .gitignore in secret/ to protect the key ──
		var secretGitignore = Abs( $"{SecretConfigPath}/.gitignore" );
		if ( !File.Exists( secretGitignore ) )
			File.WriteAllText( secretGitignore, "*\n!.gitignore\n" );
	}

	/// <summary>
	/// For display in the Setup window — show the path to the secret key file.
	/// </summary>
	public static string EnvFilePath => SecretKeyFile;

	/// <summary>
	/// Update just the data source preference without touching other fields.
	/// </summary>
	public static void SetDataSource( DataSourceMode mode )
	{
		DataSource = mode;
		if ( File.Exists( Abs( ProjectConfigFile ) ) )
			Save( SecretKey, PublicApiKey, ProjectId, BaseUrl, mode );
	}

	// ──────────────────────────────────────────────────────
	//  Data file loaders
	// ──────────────────────────────────────────────────────

	/// <summary>Load all collection files from collections/ directory.</summary>
	public static List<(string Name, Dictionary<string, object> Data)> LoadCollections()
	{
		var list = new List<(string, Dictionary<string, object>)>();

		var files = FindFiles( CollectionsPath, "*.json" );
		if ( files.Length > 0 )
		{
			foreach ( var file in files )
			{
				var fullPath = Abs( $"{CollectionsPath}/{file}" );
				var text = File.ReadAllText( fullPath );
				var dict = JsonSerializer.Deserialize<Dictionary<string, object>>( text,
					new JsonSerializerOptions { PropertyNameCaseInsensitive = true } );
				var name = dict?.GetValueOrDefault( "name" )?.ToString()
					?? Path.GetFileNameWithoutExtension( file );
				list.Add( (name, dict) );
			}
		}
		else if ( File.Exists( Abs( LegacyCollectionSchemaPath ) ) )
		{
			var text = File.ReadAllText( Abs( LegacyCollectionSchemaPath ) );
			var schema = JsonSerializer.Deserialize<JsonElement>( text );
			var dict = new Dictionary<string, object>
			{
				["name"] = "player_data",
				["schema"] = schema
			};
			list.Add( ("player_data", dict) );
		}

		return list;
	}

	/// <summary>Load all endpoint definitions from the endpoints/ directory.</summary>
	public static List<JsonElement> LoadEndpoints()
	{
		var list = new List<JsonElement>();
		var files = FindFiles( EndpointsPath, "*.json" );

		foreach ( var file in files )
		{
			var fullPath = Abs( $"{EndpointsPath}/{file}" );
			var text = File.ReadAllText( fullPath );
			var ep = JsonSerializer.Deserialize<JsonElement>( text );

			if ( !ep.TryGetProperty( "slug", out _ ) )
			{
				var slug = Path.GetFileNameWithoutExtension( file );
				var dict = JsonSerializer.Deserialize<Dictionary<string, object>>( text );
				dict["slug"] = slug;
				ep = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( dict ) );
			}

			list.Add( ep );
		}

		return list;
	}

	/// <summary>Load all workflow definitions from the workflows/ directory.</summary>
	public static List<JsonElement> LoadWorkflows()
	{
		var list = new List<JsonElement>();
		var files = FindFiles( WorkflowsPath, "*.json" );

		foreach ( var file in files )
		{
			var fullPath = Abs( $"{WorkflowsPath}/{file}" );
			var text = File.ReadAllText( fullPath );
			var wf = JsonSerializer.Deserialize<JsonElement>( text );

			if ( !wf.TryGetProperty( "id", out _ ) )
			{
				var id = Path.GetFileNameWithoutExtension( file );
				var dict = JsonSerializer.Deserialize<Dictionary<string, object>>( text );
				dict["id"] = id;
				wf = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( dict ) );
			}

			list.Add( wf );
		}

		return list;
	}

	// ──────────────────────────────────────────────────────
	//  Data file writers (for Pull)
	// ──────────────────────────────────────────────────────

	private static readonly JsonSerializerOptions _writeOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	/// <summary>Save endpoint definitions as individual JSON files.</summary>
	public static void SaveEndpoints( List<Dictionary<string, object>> endpoints )
	{
		EnsureSyncToolsDir();
		EnsureDir( EndpointsPath );

		// Clear existing endpoint files
		foreach ( var file in FindFiles( EndpointsPath, "*.json" ) )
			File.Delete( Abs( $"{EndpointsPath}/{file}" ) );

		foreach ( var ep in endpoints )
		{
			var slug = ep.TryGetValue( "slug", out var s ) ? s?.ToString() ?? "unknown" : "unknown";
			File.WriteAllText( Abs( $"{EndpointsPath}/{slug}.json" ), JsonSerializer.Serialize( ep, _writeOptions ) );
		}

		Log.Info( $"[SyncTool] Saved {endpoints.Count} endpoint files to endpoints/" );
	}

	/// <summary>Save workflow definitions as individual JSON files.</summary>
	public static void SaveWorkflows( List<Dictionary<string, object>> workflows )
	{
		EnsureSyncToolsDir();
		EnsureDir( WorkflowsPath );

		// Clear existing workflow files
		foreach ( var file in FindFiles( WorkflowsPath, "*.json" ) )
			File.Delete( Abs( $"{WorkflowsPath}/{file}" ) );

		foreach ( var wf in workflows )
		{
			var id = wf.TryGetValue( "id", out var s ) ? s?.ToString() ?? "unknown" : "unknown";
			File.WriteAllText( Abs( $"{WorkflowsPath}/{id}.json" ), JsonSerializer.Serialize( wf, _writeOptions ) );
		}

		Log.Info( $"[SyncTool] Saved {workflows.Count} workflow files to workflows/" );
	}

	/// <summary>Save a single workflow to workflows/{id}.json.</summary>
	public static void SaveWorkflow( string id, Dictionary<string, object> data )
	{
		EnsureSyncToolsDir();
		EnsureDir( WorkflowsPath );
		File.WriteAllText( Abs( $"{WorkflowsPath}/{id}.json" ), JsonSerializer.Serialize( data, _writeOptions ) );
		Log.Info( $"[SyncTool] Saved workflows/{id}.json" );
	}

	/// <summary>Save a collection to collections/{name}.json.</summary>
	public static void SaveCollection( string name, Dictionary<string, object> data )
	{
		EnsureSyncToolsDir();
		EnsureDir( CollectionsPath );
		File.WriteAllText( Abs( $"{CollectionsPath}/{name}.json" ), JsonSerializer.Serialize( data, _writeOptions ) );
		Log.Info( $"[SyncTool] Saved collections/{name}.json" );
	}

	/// <summary>Save multiple collections.</summary>
	public static void SaveCollections( List<(string Name, Dictionary<string, object> Data)> collections )
	{
		foreach ( var (name, data) in collections )
			SaveCollection( name, data );
	}

	/// <summary>Check if local data files exist.</summary>
	public static bool HasLocalData()
	{
		return File.Exists( Abs( LegacyCollectionSchemaPath ) )
			|| FindFiles( CollectionsPath, "*.json" ).Length > 0
			|| FindFiles( EndpointsPath, "*.json" ).Length > 0
			|| FindFiles( WorkflowsPath, "*.json" ).Length > 0;
	}

	private static void EnsureSyncToolsDir()
	{
		EnsureDir( SyncToolsPath );
		EnsureDir( ConfigPath );
		EnsureDir( PublicConfigPath );
		EnsureDir( SecretConfigPath );
		EnsureDir( CollectionsPath );
		EnsureDir( EndpointsPath );
		EnsureDir( WorkflowsPath );
	}

	// ──────────────────────────────────────────────────────
	//  First-install scaffolding
	// ──────────────────────────────────────────────────────

	/// <summary>
	/// Creates the full folder structure with sample files on first install.
	/// </summary>
	private static void ScaffoldProject()
	{
		Log.Info( "[NetworkStorage] First install detected — scaffolding Editor/Network Storage/ ..." );

		EnsureSyncToolsDir();

		// ── Public config with placeholders ──
		var publicConfig = new Dictionary<string, object>
		{
			["projectId"] = "your-project-id-here",
			["publicKey"] = "sbox_ns_your_public_key_here",
			["baseUrl"] = "https://api.sboxcool.com",
			["cdnUrl"] = "",
			["apiVersion"] = "v3",
			["dataFolder"] = "Network Storage",
			["dataSource"] = "api_then_json"
		};
		File.WriteAllText( Abs( ProjectConfigFile ), JsonSerializer.Serialize( publicConfig, _jsonOptions ) );

		// ── Secret key with placeholder ──
		var secretConfig = new Dictionary<string, string>
		{
			["secretKey"] = "sbox_sk_your_secret_key_here"
		};
		File.WriteAllText( Abs( SecretKeyFile ), JsonSerializer.Serialize( secretConfig, _jsonOptions ) );

		// ── .gitignore in secret/ — ignore everything ──
		File.WriteAllText( Abs( $"{SecretConfigPath}/.gitignore" ), "*\n!.gitignore\n" );

		// ── Sample collection ──
		var sampleCollection = @"{
  ""name"": ""players"",
  ""scope"": ""user"",
  ""schema"": {
    ""currency"": { ""type"": ""number"", ""default"": 0 },
    ""xp"": { ""type"": ""number"", ""default"": 0 },
    ""level"": { ""type"": ""number"", ""default"": 1 }
  }
}";
		File.WriteAllText( Abs( $"{CollectionsPath}/players.json" ), sampleCollection );

		// ── Sample endpoint ──
		var sampleEndpoint = @"{
  ""slug"": ""init-player"",
  ""description"": ""Initialize a new player with default values"",
  ""steps"": [
    {
      ""type"": ""read"",
      ""collection"": ""players"",
      ""as"": ""player""
    },
    {
      ""type"": ""condition"",
      ""field"": ""player"",
      ""operator"": ""is_null"",
      ""onFail"": ""return""
    },
    {
      ""type"": ""write"",
      ""collection"": ""players"",
      ""data"": {
        ""currency"": 0,
        ""xp"": 0,
        ""level"": 1
      }
    }
  ]
}";
		File.WriteAllText( Abs( $"{EndpointsPath}/init-player.json" ), sampleEndpoint );

		Log.Info( "[NetworkStorage] Scaffolding complete. Open Editor → Network Storage → Setup to enter your API keys." );
	}
}
