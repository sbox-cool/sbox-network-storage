using System;
using System.Collections.Generic;
using System.IO;
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

	private static string ProjectRoot => Project.Current?.GetRootPath() ?? "";

	/// <summary>Root path for all sync data: {project}/Editor/{DataFolder}/</summary>
	public static string SyncToolsPath => Path.Combine( ProjectRoot, "Editor", DataFolder );

	/// <summary>Path to the .env file with credentials (gitignored, never published).</summary>
	public static string EnvFilePath => Path.Combine( SyncToolsPath, ".env" );

	/// <summary>Path to collections/ directory (one JSON file per collection).</summary>
	public static string CollectionsPath => Path.Combine( SyncToolsPath, "collections" );

	/// <summary>Path to the endpoints directory.</summary>
	public static string EndpointsPath => Path.Combine( SyncToolsPath, "endpoints" );

	/// <summary>Legacy paths for auto-migration.</summary>
	public static string LegacyCollectionSchemaPath => Path.Combine( SyncToolsPath, "collection_schema.json" );
	private static string LegacySyncToolsPath => Path.Combine( ProjectRoot, "Editor", "SyncTools" );

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

		// Try current path first, fall back to legacy Editor/SyncTools/
		var envPath = EnvFilePath;
		if ( !File.Exists( envPath ) )
		{
			var legacyEnv = Path.Combine( LegacySyncToolsPath, ".env" );
			if ( File.Exists( legacyEnv ) )
			{
				Log.Info( "[SyncTool] Found legacy .env at Editor/SyncTools/ — will migrate on next save" );
				envPath = legacyEnv;
			}
			else
			{
				Log.Warning( $"[SyncTool] .env file not found at {envPath}" );
				return;
			}
		}

		foreach ( var line in File.ReadAllLines( envPath ) )
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

		var dir = SyncToolsPath;
		if ( !Directory.Exists( dir ) )
			Directory.CreateDirectory( dir );

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

		File.WriteAllLines( EnvFilePath, lines );
		Log.Info( "[SyncTool] Configuration saved to .env" );
	}

	/// <summary>
	/// Update just the data source preference without touching other fields.
	/// </summary>
	public static void SetDataSource( DataSourceMode mode )
	{
		DataSource = mode;
		if ( File.Exists( EnvFilePath ) )
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
		// Fallback: legacy single collection_schema.json
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

		if ( !Directory.Exists( EndpointsPath ) )
			Directory.CreateDirectory( EndpointsPath );

		// Clear existing endpoint files
		foreach ( var file in Directory.GetFiles( EndpointsPath, "*.json" ) )
			File.Delete( file );

		foreach ( var ep in endpoints )
		{
			var slug = ep.TryGetValue( "slug", out var s ) ? s?.ToString() ?? "unknown" : "unknown";
			var path = Path.Combine( EndpointsPath, $"{slug}.json" );
			var json = JsonSerializer.Serialize( ep, _writeOptions );
			File.WriteAllText( path, json );
		}

		Log.Info( $"[SyncTool] Saved {endpoints.Count} endpoint files to endpoints/" );
	}

	/// <summary>Save a collection to collections/{name}.json.</summary>
	public static void SaveCollection( string name, Dictionary<string, object> data )
	{
		EnsureSyncToolsDir();
		if ( !Directory.Exists( CollectionsPath ) )
			Directory.CreateDirectory( CollectionsPath );

		var path = Path.Combine( CollectionsPath, $"{name}.json" );
		var json = JsonSerializer.Serialize( data, _writeOptions );
		File.WriteAllText( path, json );
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
		return File.Exists( LegacyCollectionSchemaPath )
			|| ( Directory.Exists( CollectionsPath ) && Directory.GetFiles( CollectionsPath, "*.json" ).Length > 0 )
			|| ( Directory.Exists( EndpointsPath ) && Directory.GetFiles( EndpointsPath, "*.json" ).Length > 0 );
	}

	private static void EnsureSyncToolsDir()
	{
		if ( !Directory.Exists( SyncToolsPath ) )
			Directory.CreateDirectory( SyncToolsPath );
	}
}
