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
	public static bool IsValid => !string.IsNullOrEmpty( SecretKey )
		&& SecretKey.StartsWith( "sbox_sk_" )
		&& !string.IsNullOrEmpty( PublicApiKey )
		&& PublicApiKey.StartsWith( "sbox_ns_" )
		&& !string.IsNullOrEmpty( ProjectId );

	public static bool HasPublicKey => !string.IsNullOrEmpty( PublicApiKey )
		&& PublicApiKey.StartsWith( "sbox_ns_" );

	public static bool IsFullyConfigured => IsValid;

	// ── Paths ──

	private static string ProjectRoot => Project.Current?.GetRootPath() ?? "";

	/// <summary>Root path for all sync data: {project}/Editor/{DataFolder}/</summary>
	public static string SyncToolsPath => Path.Combine( ProjectRoot, "Editor", DataFolder );

	/// <summary>Path to config/ directory.</summary>
	public static string ConfigPath => Path.Combine( SyncToolsPath, "config" );

	/// <summary>Path to config/public/ — safe to commit, ships with game.</summary>
	public static string PublicConfigPath => Path.Combine( ConfigPath, "public" );

	/// <summary>Path to config/secret/ — gitignored, editor-only, NEVER published.</summary>
	public static string SecretConfigPath => Path.Combine( ConfigPath, "secret" );

	/// <summary>Path to the public project config file.</summary>
	public static string ProjectConfigFile => Path.Combine( PublicConfigPath, "projectConfig.json" );

	/// <summary>Path to the secret key file.</summary>
	public static string SecretKeyFile => Path.Combine( SecretConfigPath, "secret_key.json" );

	/// <summary>Path to the runtime credentials file in Assets/ (shipped with game).</summary>
	public static string RuntimeCredentialsFile => Path.Combine( ProjectRoot, "Assets", "network-storage.credentials.json" );

	/// <summary>Path to collections/ directory.</summary>
	public static string CollectionsPath => Path.Combine( SyncToolsPath, "collections" );

	/// <summary>Path to the endpoints directory.</summary>
	public static string EndpointsPath => Path.Combine( SyncToolsPath, "endpoints" );

	/// <summary>Path to the workflows directory.</summary>
	public static string WorkflowsPath => Path.Combine( SyncToolsPath, "workflows" );

	/// <summary>Legacy paths for auto-migration.</summary>
	public static string LegacyCollectionSchemaPath => Path.Combine( SyncToolsPath, "collection_schema.json" );

	// Legacy .env locations for migration
	private static string LegacyEnvInConfig => Path.Combine( ConfigPath, ".env" );
	private static string LegacyEnvInRoot => Path.Combine( SyncToolsPath, ".env" );
	private static string LegacyEnvInSyncTools => Path.Combine( ProjectRoot, "Editor", "SyncTools", ".env" );

	// ── JSON options ──
	private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

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
		ApiVersion = "v3";
		DataSource = DataSourceMode.ApiThenJson;
		DataFolder = "Network Storage";

		// ── Try new split config first ──
		if ( File.Exists( ProjectConfigFile ) )
		{
			LoadPublicConfig( ProjectConfigFile );
			if ( File.Exists( SecretKeyFile ) )
				LoadSecretConfig( SecretKeyFile );
			return;
		}

		// ── Try legacy .env locations for migration ──
		string legacyEnv = null;
		if ( File.Exists( LegacyEnvInConfig ) )
			legacyEnv = LegacyEnvInConfig;
		else if ( File.Exists( LegacyEnvInRoot ) )
			legacyEnv = LegacyEnvInRoot;
		else if ( File.Exists( LegacyEnvInSyncTools ) )
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
		var json = JsonSerializer.Deserialize<JsonElement>( File.ReadAllText( path ) );
		ProjectId = json.TryGetProperty( "projectId", out var pid ) ? pid.GetString() ?? "" : "";
		PublicApiKey = json.TryGetProperty( "publicKey", out var pk ) ? pk.GetString() ?? "" : "";
		BaseUrl = json.TryGetProperty( "baseUrl", out var bu ) ? bu.GetString()?.TrimEnd( '/' ) ?? "https://api.sboxcool.com" : "https://api.sboxcool.com";
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
		var json = JsonSerializer.Deserialize<JsonElement>( File.ReadAllText( path ) );
		SecretKey = json.TryGetProperty( "secretKey", out var sk ) ? sk.GetString() ?? "" : "";
	}

	private static void LoadLegacyEnv( string envPath )
	{
		foreach ( var line in File.ReadAllLines( envPath ) )
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
		string baseUrl = null, DataSourceMode? dataSource = null, string dataFolder = null )
	{
		if ( dataFolder != null )
			DataFolder = dataFolder;

		EnsureSyncToolsDir();

		SecretKey = secretKey ?? "";
		PublicApiKey = publicApiKey ?? "";
		ProjectId = projectId ?? "";
		BaseUrl = ( baseUrl ?? "https://api.sboxcool.com" ).TrimEnd( '/' );
		if ( dataSource.HasValue )
			DataSource = dataSource.Value;

		// ── Write public config (safe to commit) ──
		var publicConfig = new Dictionary<string, object>
		{
			["projectId"] = ProjectId,
			["publicKey"] = PublicApiKey,
			["baseUrl"] = BaseUrl,
			["apiVersion"] = ApiVersion,
			["dataFolder"] = DataFolder,
			["dataSource"] = DataSource switch
			{
				DataSourceMode.ApiOnly => "api_only",
				DataSourceMode.JsonOnly => "json_only",
				_ => "api_then_json"
			}
		};
		File.WriteAllText( ProjectConfigFile, JsonSerializer.Serialize( publicConfig, _jsonOptions ) );
		Log.Info( "[SyncTool] Public config saved to config/public/projectConfig.json" );

		// ── Write secret key (gitignored, NEVER published) ──
		var secretConfig = new Dictionary<string, string> { ["secretKey"] = SecretKey };
		File.WriteAllText( SecretKeyFile, JsonSerializer.Serialize( secretConfig, _jsonOptions ) );
		Log.Info( "[SyncTool] Secret key saved to config/secret/secret_key.json" );

		// ── Write runtime credentials (ships with game, NO secret key) ──
		if ( !string.IsNullOrEmpty( ProjectId ) )
		{
			var runtimeCreds = new Dictionary<string, string>
			{
				["projectId"] = ProjectId,
				["publicKey"] = PublicApiKey,
				["baseUrl"] = BaseUrl,
				["apiVersion"] = ApiVersion
			};
			var credsJson = JsonSerializer.Serialize( runtimeCreds, _jsonOptions );

			// Write to Assets/ (s&box mounts this for runtime FileSystem access)
			var assetsPath = Path.Combine( ProjectRoot, "Assets", "network-storage.credentials.json" );
			File.WriteAllText( assetsPath, credsJson );
			Log.Info( "[SyncTool] Runtime credentials written to Assets/network-storage.credentials.json" );
		}

		// ── Write .gitignore in secret/ to protect the key ──
		var secretGitignore = Path.Combine( SecretConfigPath, ".gitignore" );
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
		if ( File.Exists( ProjectConfigFile ) )
			Save( SecretKey, PublicApiKey, ProjectId, BaseUrl, mode );
	}

	// ──────────────────────────────────────────────────────
	//  Data file loaders
	// ──────────────────────────────────────────────────────

	/// <summary>Load all collection files from collections/ directory.</summary>
	public static List<(string Name, Dictionary<string, object> Data)> LoadCollections()
	{
		var list = new List<(string, Dictionary<string, object>)>();

		if ( Directory.Exists( CollectionsPath ) )
		{
			foreach ( var file in Directory.GetFiles( CollectionsPath, "*.json" ).OrderBy( f => f ) )
			{
				var text = File.ReadAllText( file );
				var dict = JsonSerializer.Deserialize<Dictionary<string, object>>( text,
					new JsonSerializerOptions { PropertyNameCaseInsensitive = true } );
				var name = dict?.GetValueOrDefault( "name" )?.ToString()
					?? Path.GetFileNameWithoutExtension( file );
				list.Add( (name, dict) );
			}
		}
		else if ( File.Exists( LegacyCollectionSchemaPath ) )
		{
			var text = File.ReadAllText( LegacyCollectionSchemaPath );
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
		if ( !Directory.Exists( EndpointsPath ) ) return list;

		foreach ( var file in Directory.GetFiles( EndpointsPath, "*.json" ).OrderBy( f => f ) )
		{
			var text = File.ReadAllText( file );
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
		if ( !Directory.Exists( WorkflowsPath ) ) return list;

		foreach ( var file in Directory.GetFiles( WorkflowsPath, "*.json" ).OrderBy( f => f ) )
		{
			var text = File.ReadAllText( file );
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
		if ( !Directory.Exists( EndpointsPath ) )
			Directory.CreateDirectory( EndpointsPath );

		foreach ( var file in Directory.GetFiles( EndpointsPath, "*.json" ) )
			File.Delete( file );

		foreach ( var ep in endpoints )
		{
			var slug = ep.TryGetValue( "slug", out var s ) ? s?.ToString() ?? "unknown" : "unknown";
			var path = Path.Combine( EndpointsPath, $"{slug}.json" );
			File.WriteAllText( path, JsonSerializer.Serialize( ep, _writeOptions ) );
		}

		Log.Info( $"[SyncTool] Saved {endpoints.Count} endpoint files to endpoints/" );
	}

	/// <summary>Save workflow definitions as individual JSON files.</summary>
	public static void SaveWorkflows( List<Dictionary<string, object>> workflows )
	{
		EnsureSyncToolsDir();
		if ( !Directory.Exists( WorkflowsPath ) )
			Directory.CreateDirectory( WorkflowsPath );

		foreach ( var file in Directory.GetFiles( WorkflowsPath, "*.json" ) )
			File.Delete( file );

		foreach ( var wf in workflows )
		{
			var id = wf.TryGetValue( "id", out var s ) ? s?.ToString() ?? "unknown" : "unknown";
			var path = Path.Combine( WorkflowsPath, $"{id}.json" );
			File.WriteAllText( path, JsonSerializer.Serialize( wf, _writeOptions ) );
		}

		Log.Info( $"[SyncTool] Saved {workflows.Count} workflow files to workflows/" );
	}

	/// <summary>Save a single workflow to workflows/{id}.json.</summary>
	public static void SaveWorkflow( string id, Dictionary<string, object> data )
	{
		EnsureSyncToolsDir();
		if ( !Directory.Exists( WorkflowsPath ) )
			Directory.CreateDirectory( WorkflowsPath );

		var path = Path.Combine( WorkflowsPath, $"{id}.json" );
		File.WriteAllText( path, JsonSerializer.Serialize( data, _writeOptions ) );
		Log.Info( $"[SyncTool] Saved workflows/{id}.json" );
	}

	/// <summary>Save a collection to collections/{name}.json.</summary>
	public static void SaveCollection( string name, Dictionary<string, object> data )
	{
		EnsureSyncToolsDir();
		if ( !Directory.Exists( CollectionsPath ) )
			Directory.CreateDirectory( CollectionsPath );

		var path = Path.Combine( CollectionsPath, $"{name}.json" );
		File.WriteAllText( path, JsonSerializer.Serialize( data, _writeOptions ) );
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
		return File.Exists( LegacyCollectionSchemaPath )
			|| ( Directory.Exists( CollectionsPath ) && Directory.GetFiles( CollectionsPath, "*.json" ).Length > 0 )
			|| ( Directory.Exists( EndpointsPath ) && Directory.GetFiles( EndpointsPath, "*.json" ).Length > 0 )
			|| ( Directory.Exists( WorkflowsPath ) && Directory.GetFiles( WorkflowsPath, "*.json" ).Length > 0 );
	}

	private static void EnsureSyncToolsDir()
	{
		if ( !Directory.Exists( SyncToolsPath ) )
			Directory.CreateDirectory( SyncToolsPath );
		if ( !Directory.Exists( ConfigPath ) )
			Directory.CreateDirectory( ConfigPath );
		if ( !Directory.Exists( PublicConfigPath ) )
			Directory.CreateDirectory( PublicConfigPath );
		if ( !Directory.Exists( SecretConfigPath ) )
			Directory.CreateDirectory( SecretConfigPath );
		if ( !Directory.Exists( CollectionsPath ) )
			Directory.CreateDirectory( CollectionsPath );
		if ( !Directory.Exists( EndpointsPath ) )
			Directory.CreateDirectory( EndpointsPath );
		if ( !Directory.Exists( WorkflowsPath ) )
			Directory.CreateDirectory( WorkflowsPath );
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
			["apiVersion"] = "v3",
			["dataFolder"] = "Network Storage",
			["dataSource"] = "api_then_json"
		};
		File.WriteAllText( ProjectConfigFile, JsonSerializer.Serialize( publicConfig, _jsonOptions ) );

		// ── Secret key with placeholder ──
		var secretConfig = new Dictionary<string, string>
		{
			["secretKey"] = "sbox_sk_your_secret_key_here"
		};
		File.WriteAllText( SecretKeyFile, JsonSerializer.Serialize( secretConfig, _jsonOptions ) );

		// ── .gitignore in secret/ — ignore everything ──
		File.WriteAllText( Path.Combine( SecretConfigPath, ".gitignore" ), "*\n!.gitignore\n" );

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
		File.WriteAllText( Path.Combine( CollectionsPath, "players.json" ), sampleCollection );

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
		File.WriteAllText( Path.Combine( EndpointsPath, "init-player.json" ), sampleEndpoint );

		Log.Info( "[NetworkStorage] Scaffolding complete. Open Editor → Network Storage → Setup to enter your API keys." );
	}
}
