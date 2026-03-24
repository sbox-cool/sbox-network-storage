using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;
using Editor;

/// <summary>
/// Manages the sync tool configuration for the Network Storage library.
/// Credentials and data files live in a configurable Editor subfolder (default: Editor/Network Storage/).
/// Everything in Editor/ is excluded from publishing — secrets never ship.
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
		&& !string.IsNullOrEmpty( ProjectId );

	public static bool HasPublicKey => !string.IsNullOrEmpty( PublicApiKey )
		&& PublicApiKey.StartsWith( "sbox_ns_" );

	public static bool IsFullyConfigured => IsValid && HasPublicKey;

	// ── Paths (all relative to the configurable data folder) ──

	private static BaseFileSystem Fs => Editor.FileSystem.Root;

	/// <summary>Root path for all sync data: {project}/Editor/{DataFolder}/</summary>
	public static string SyncToolsPath => $"Editor/{DataFolder}";

	/// <summary>Path to config/ directory (contains .env and other config).</summary>
	public static string ConfigPath => $"Editor/{DataFolder}/config";

	/// <summary>Path to the .env file with credentials (gitignored, never published).</summary>
	public static string EnvFilePath => $"Editor/{DataFolder}/config/.env";

	/// <summary>Path to collections/ directory (one JSON file per collection).</summary>
	public static string CollectionsPath => $"Editor/{DataFolder}/collections";

	/// <summary>Path to the endpoints directory.</summary>
	public static string EndpointsPath => $"Editor/{DataFolder}/endpoints";

	/// <summary>Path to the workflows directory.</summary>
	public static string WorkflowsPath => $"Editor/{DataFolder}/workflows";

	/// <summary>Legacy paths for auto-migration.</summary>
	public static string LegacyCollectionSchemaPath => $"Editor/{DataFolder}/collection_schema.json";
	private static string LegacySyncToolsPath => "Editor/SyncTools";

	// ──────────────────────────────────────────────────────
	//  Load / Save
	// ──────────────────────────────────────────────────────

	/// <summary>
	/// Load configuration from .env file. Call before any sync operation.
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

		// Try current path first, then old root location, then legacy Editor/SyncTools/
		var envPath = EnvFilePath;
		if ( !Fs.FileExists( envPath ) )
		{
			// Check old location (root of Network Storage folder, before config/ subfolder)
			var oldRootEnv = $"{SyncToolsPath}/.env";
			var legacyEnv = $"{LegacySyncToolsPath}/.env";

			if ( Fs.FileExists( oldRootEnv ) )
			{
				Log.Info( "[SyncTool] Found .env at root — will migrate to config/ on next save" );
				envPath = oldRootEnv;
			}
			else if ( Fs.FileExists( legacyEnv ) )
			{
				Log.Info( "[SyncTool] Found legacy .env at Editor/SyncTools/ — will migrate on next save" );
				envPath = legacyEnv;
			}
			else
			{
				ScaffoldProject();
				return;
			}
		}

		var content = Fs.ReadAllText( envPath );
		foreach ( var line in content.Split( '\n' ) )
		{
			var trimmed = line.Trim();
			if ( string.IsNullOrEmpty( trimmed ) || trimmed.StartsWith( '#' ) )
				continue;

			var eqIdx = trimmed.IndexOf( '=' );
			if ( eqIdx < 0 ) continue;

			var key = trimmed[..eqIdx].Trim();
			var val = trimmed[( eqIdx + 1 )..].Trim();

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
	/// Save all settings to the .env file. Creates the SyncTools directory if needed.
	/// The .env file is in Editor/ (gitignored, never published) — secrets are safe.
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

		var lines = new List<string>
		{
			"# Network Storage credentials",
			"# Stored in Editor/ — excluded from publishing, safe for secrets",
			"# NEVER commit this file to version control",
			"",
			"# Project identifier from sboxcool.com dashboard",
			$"SBOXCOOL_PROJECT_ID={ProjectId}",
			"",
			"# Public API key (sbox_ns_ prefix) — used by the game client at runtime",
			$"SBOXCOOL_PUBLIC_KEY={PublicApiKey}",
			"",
			"# Secret key (sbox_sk_ prefix) — used by editor sync tool only, NEVER ships",
			$"SBOXCOOL_SECRET_KEY={SecretKey}",
			"",
			"# Base URL (default: https://api.sboxcool.com)",
			$"SBOXCOOL_BASE_URL={BaseUrl}",
			"",
			"# API version (default: v3)",
			$"SBOXCOOL_API_VERSION={ApiVersion}",
			"",
			"# Editor subfolder for sync data (default: Network Storage)",
			$"SBOXCOOL_DATA_FOLDER={DataFolder}",
			"",
			"# Data source for GET requests: api_then_json (try API first, fall back to JSON), api_only, json_only",
			$"SBOXCOOL_DATA_SOURCE={DataSource switch { DataSourceMode.ApiOnly => "api_only", DataSourceMode.JsonOnly => "json_only", _ => "api_then_json" }}"
		};

		Fs.WriteAllText( EnvFilePath, string.Join( '\n', lines ) );
		Log.Info( "[SyncTool] Configuration saved to .env" );
	}

	/// <summary>
	/// Update just the data source preference without touching other fields.
	/// </summary>
	public static void SetDataSource( DataSourceMode mode )
	{
		DataSource = mode;
		if ( Fs.FileExists( EnvFilePath ) )
			Save( SecretKey, PublicApiKey, ProjectId, BaseUrl, mode );
	}

	// ──────────────────────────────────────────────────────
	//  Data file loaders
	// ──────────────────────────────────────────────────────

	/// <summary>Load all collection files from collections/ directory.</summary>
	public static List<(string Name, Dictionary<string, object> Data)> LoadCollections()
	{
		var list = new List<(string, Dictionary<string, object>)>();

		// Try collections/ folder first
		if ( Fs.DirectoryExists( CollectionsPath ) )
		{
			foreach ( var file in Fs.FindFile( CollectionsPath, "*.json" ).OrderBy( f => f ) )
			{
				var fullPath = $"{CollectionsPath}/{file}";
				var text = Fs.ReadAllText( fullPath );
				var dict = JsonSerializer.Deserialize<Dictionary<string, object>>( text,
					new JsonSerializerOptions { PropertyNameCaseInsensitive = true } );
				var name = dict?.GetValueOrDefault( "name" )?.ToString()
					?? System.IO.Path.GetFileNameWithoutExtension( file );
				list.Add( (name, dict) );
			}
		}
		// Fallback: legacy single collection_schema.json
		else if ( Fs.FileExists( LegacyCollectionSchemaPath ) )
		{
			var text = Fs.ReadAllText( LegacyCollectionSchemaPath );
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
		if ( !Fs.DirectoryExists( EndpointsPath ) ) return list;

		foreach ( var file in Fs.FindFile( EndpointsPath, "*.json" ).OrderBy( f => f ) )
		{
			var fullPath = $"{EndpointsPath}/{file}";
			var text = Fs.ReadAllText( fullPath );
			var ep = JsonSerializer.Deserialize<JsonElement>( text );

			if ( !ep.TryGetProperty( "slug", out _ ) )
			{
				var slug = System.IO.Path.GetFileNameWithoutExtension( file );
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
		if ( !Fs.DirectoryExists( WorkflowsPath ) ) return list;

		foreach ( var file in Fs.FindFile( WorkflowsPath, "*.json" ).OrderBy( f => f ) )
		{
			var fullPath = $"{WorkflowsPath}/{file}";
			var text = Fs.ReadAllText( fullPath );
			var wf = JsonSerializer.Deserialize<JsonElement>( text );

			if ( !wf.TryGetProperty( "id", out _ ) )
			{
				var id = System.IO.Path.GetFileNameWithoutExtension( file );
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

	/// <summary>
	/// Save endpoint definitions as individual JSON files in endpoints/ directory.
	/// Clears the directory first, then writes one file per endpoint (slug.json).
	/// </summary>
	public static void SaveEndpoints( List<Dictionary<string, object>> endpoints )
	{
		EnsureSyncToolsDir();

		if ( !Fs.DirectoryExists( EndpointsPath ) )
			Fs.CreateDirectory( EndpointsPath );

		// Clear existing endpoint files
		foreach ( var file in Fs.FindFile( EndpointsPath, "*.json" ) )
			Fs.DeleteFile( $"{EndpointsPath}/{file}" );

		foreach ( var ep in endpoints )
		{
			var slug = ep.TryGetValue( "slug", out var s ) ? s?.ToString() ?? "unknown" : "unknown";
			var path = $"{EndpointsPath}/{slug}.json";
			var json = JsonSerializer.Serialize( ep, _writeOptions );
			Fs.WriteAllText( path, json );
		}

		Log.Info( $"[SyncTool] Saved {endpoints.Count} endpoint files to endpoints/" );
	}

	/// <summary>
	/// Save workflow definitions as individual JSON files in workflows/ directory.
	/// </summary>
	public static void SaveWorkflows( List<Dictionary<string, object>> workflows )
	{
		EnsureSyncToolsDir();

		if ( !Fs.DirectoryExists( WorkflowsPath ) )
			Fs.CreateDirectory( WorkflowsPath );

		// Clear existing workflow files
		foreach ( var file in Fs.FindFile( WorkflowsPath, "*.json" ) )
			Fs.DeleteFile( $"{WorkflowsPath}/{file}" );

		foreach ( var wf in workflows )
		{
			var id = wf.TryGetValue( "id", out var s ) ? s?.ToString() ?? "unknown" : "unknown";
			var path = $"{WorkflowsPath}/{id}.json";
			var json = JsonSerializer.Serialize( wf, _writeOptions );
			Fs.WriteAllText( path, json );
		}

		Log.Info( $"[SyncTool] Saved {workflows.Count} workflow files to workflows/" );
	}

	/// <summary>Save a single workflow to workflows/{id}.json.</summary>
	public static void SaveWorkflow( string id, Dictionary<string, object> data )
	{
		EnsureSyncToolsDir();
		if ( !Fs.DirectoryExists( WorkflowsPath ) )
			Fs.CreateDirectory( WorkflowsPath );

		var path = $"{WorkflowsPath}/{id}.json";
		var json = JsonSerializer.Serialize( data, _writeOptions );
		Fs.WriteAllText( path, json );
		Log.Info( $"[SyncTool] Saved workflows/{id}.json ({json.Length} bytes)" );
	}

	/// <summary>Save a collection to collections/{name}.json.</summary>
	public static void SaveCollection( string name, Dictionary<string, object> data )
	{
		EnsureSyncToolsDir();
		if ( !Fs.DirectoryExists( CollectionsPath ) )
			Fs.CreateDirectory( CollectionsPath );

		var path = $"{CollectionsPath}/{name}.json";
		var json = JsonSerializer.Serialize( data, _writeOptions );
		Fs.WriteAllText( path, json );
		Log.Info( $"[SyncTool] Saved collections/{name}.json ({json.Length} bytes)" );
	}

	/// <summary>Save multiple collections to collections/ directory.</summary>
	public static void SaveCollections( List<(string Name, Dictionary<string, object> Data)> collections )
	{
		foreach ( var (name, data) in collections )
			SaveCollection( name, data );
	}

	/// <summary>Check if local SyncTools files exist (for overwrite warnings).</summary>
	public static bool HasLocalData()
	{
		return Fs.FileExists( LegacyCollectionSchemaPath )
			|| ( Fs.DirectoryExists( CollectionsPath ) && Fs.FindFile( CollectionsPath, "*.json" ).Any() )
			|| ( Fs.DirectoryExists( EndpointsPath ) && Fs.FindFile( EndpointsPath, "*.json" ).Any() )
			|| ( Fs.DirectoryExists( WorkflowsPath ) && Fs.FindFile( WorkflowsPath, "*.json" ).Any() );
	}

	private static void EnsureSyncToolsDir()
	{
		if ( !Fs.DirectoryExists( SyncToolsPath ) )
			Fs.CreateDirectory( SyncToolsPath );
		if ( !Fs.DirectoryExists( ConfigPath ) )
			Fs.CreateDirectory( ConfigPath );
		if ( !Fs.DirectoryExists( CollectionsPath ) )
			Fs.CreateDirectory( CollectionsPath );
		if ( !Fs.DirectoryExists( EndpointsPath ) )
			Fs.CreateDirectory( EndpointsPath );
		if ( !Fs.DirectoryExists( WorkflowsPath ) )
			Fs.CreateDirectory( WorkflowsPath );
	}

	// ──────────────────────────────────────────────────────
	//  First-install scaffolding
	// ──────────────────────────────────────────────────────

	/// <summary>
	/// Creates the full Editor/Network Storage/ folder structure with sample files
	/// on first install when no .env exists.
	/// </summary>
	private static void ScaffoldProject()
	{
		Log.Info( "[NetworkStorage] First install detected — scaffolding Editor/Network Storage/ ..." );

		EnsureSyncToolsDir();

		// ── .env with placeholder keys ──
		var envLines = new[]
		{
			"# Network Storage credentials",
			"# Stored in Editor/ — excluded from publishing, safe for secrets",
			"# NEVER commit this file to version control",
			"#",
			"# Get your keys from https://sbox.cool → Dashboard → API Keys",
			"",
			"# Project identifier from your sbox.cool dashboard",
			"SBOXCOOL_PROJECT_ID=your-project-id-here",
			"",
			"# Public API key (sbox_ns_ prefix) — used by the game client at runtime",
			"SBOXCOOL_PUBLIC_KEY=sbox_ns_your_public_key_here",
			"",
			"# Secret key (sbox_sk_ prefix) — used by editor sync tool only, NEVER ships",
			"SBOXCOOL_SECRET_KEY=sbox_sk_your_secret_key_here",
			"",
			"# Base URL (default: https://api.sboxcool.com)",
			"SBOXCOOL_BASE_URL=https://api.sboxcool.com",
			"",
			"# API version (default: v3)",
			"SBOXCOOL_API_VERSION=v3",
			"",
			"# Editor subfolder for sync data (default: Network Storage)",
			"SBOXCOOL_DATA_FOLDER=Network Storage",
			"",
			"# Data source for GET requests: api_then_json, api_only, json_only",
			"SBOXCOOL_DATA_SOURCE=api_then_json"
		};
		Fs.WriteAllText( EnvFilePath, string.Join( '\n', envLines ) );

		// ── Sample collection: players.json ──
		var sampleCollection = @"{
  ""name"": ""players"",
  ""scope"": ""user"",
  ""schema"": {
    ""currency"": { ""type"": ""number"", ""default"": 0 },
    ""xp"": { ""type"": ""number"", ""default"": 0 },
    ""level"": { ""type"": ""number"", ""default"": 1 }
  }
}";
		Fs.WriteAllText( $"{CollectionsPath}/players.json", sampleCollection );

		// ── Sample endpoint: init-player.json ──
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
		Fs.WriteAllText( $"{EndpointsPath}/init-player.json", sampleEndpoint );

		// ── .gitignore for .env in config/ ──
		var gitignorePath = $"{ConfigPath}/.gitignore";
		if ( !Fs.FileExists( gitignorePath ) )
		{
			Fs.WriteAllText( gitignorePath, ".env\n" );
		}

		Log.Info( "[NetworkStorage] Scaffolding complete. Open Editor → Network Storage → Setup to enter your API keys." );
	}
}
