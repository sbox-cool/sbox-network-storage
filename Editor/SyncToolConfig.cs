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
/// so the runtime client can auto-configure via s&amp;box's sandboxed FileSystem.
/// </summary>
public static partial class SyncToolConfig
{
	public enum DataSourceMode { ApiOnly }
	public enum SourceExportMode { SourceOnly }

	// ── Credentials ──
	public static string SecretKey { get; private set; } = "";
	public static string PublicApiKey { get; private set; } = "";
	public static string ProjectId { get; private set; } = "";
	public static string BaseUrl { get; private set; } = "https://api.sboxcool.com";
	public static string CdnUrl { get; private set; } = "";
	public static string ApiVersion { get; private set; } = "v3";

	// ── Preferences ──
	public static DataSourceMode DataSource { get; private set; } = DataSourceMode.ApiOnly;
	public static SourceExportMode SourceExport { get; private set; } = SourceExportMode.SourceOnly;
	public static bool EnableAuthSessions { get; private set; }
	public static bool EnableEncryptedRequests { get; private set; }

	/// <summary>
	/// When true, the game host proxies Network Storage API calls on behalf of non-host clients.
	/// Required when non-host clients can't generate valid s&amp;box auth tokens (P2P multiplayer).
	/// </summary>
	public static bool ProxyEnabled { get; set; }

	// ── Sync Mappings (C# data files → collection YAML source) ──

	/// <summary>
	/// Configured mappings from C# data files to collection YAML source files.
	/// Used by sync.py to generate collection data from code.
	/// </summary>
	public static List<SyncMapping> SyncMappings { get; private set; } = new();

	public class SyncMapping
	{
		public string CsFile { get; set; } = "";
		public string Collection { get; set; } = "";
		public string Description { get; set; } = "";
	}

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

	/// <summary>Path to the tests directory.</summary>
	public static string TestsPath => $"{SyncToolsPath}/tests";

	// Legacy .env locations for migration
	private static string LegacyEnvInConfig => $"{ConfigPath}/.env";
	private static string LegacyEnvInRoot => $"{SyncToolsPath}/.env";
	private static string LegacyEnvInSyncTools => "Editor/SyncTools/.env";

	// ── JSON options ──
	private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
	private static readonly JsonSerializerOptions _readOptions = new()
	{
		AllowTrailingCommas = true,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};

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
		DataSource = DataSourceMode.ApiOnly;
		SourceExport = SourceExportMode.SourceOnly;
		EnableAuthSessions = false;
		EnableEncryptedRequests = false;
		DataFolder = "Network Storage";
		ProxyEnabled = true;

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
		var json = JsonSerializer.Deserialize<JsonElement>( File.ReadAllText( Abs( path ) ), _readOptions );
		ProjectId = json.TryGetProperty( "projectId", out var pid ) ? pid.GetString() ?? "" : "";
		PublicApiKey = json.TryGetProperty( "publicKey", out var pk ) ? pk.GetString() ?? "" : "";
		BaseUrl = json.TryGetProperty( "baseUrl", out var bu ) ? bu.GetString()?.TrimEnd( '/' ) ?? "https://api.sboxcool.com" : "https://api.sboxcool.com";
		CdnUrl = json.TryGetProperty( "cdnUrl", out var cu ) ? cu.GetString() ?? "" : "";
		ApiVersion = json.TryGetProperty( "apiVersion", out var av ) ? av.GetString()?.Trim( '/' ) ?? "v3" : "v3";
		DataFolder = json.TryGetProperty( "dataFolder", out var df ) ? df.GetString() ?? "Network Storage" : "Network Storage";
		if ( json.TryGetProperty( "dataSource", out var ds ) )
		{
			var configured = ds.GetString();
			if ( !string.IsNullOrWhiteSpace( configured ) &&
				!string.Equals( configured, "api_only", StringComparison.OrdinalIgnoreCase ) )
				Log.Warning( $"[SyncTool] dataSource '{configured}' is no longer supported; using api_only." );
		}
		DataSource = DataSourceMode.ApiOnly;
		if ( json.TryGetProperty( "sourceExportMode", out var sem ) )
		{
			var configured = sem.GetString();
			if ( !string.IsNullOrWhiteSpace( configured ) &&
				!string.Equals( configured, "source_only", StringComparison.OrdinalIgnoreCase ) )
				Log.Warning( $"[SyncTool] sourceExportMode '{configured}' is no longer supported; using source_only." );
		}
		SourceExport = SourceExportMode.SourceOnly;
		// Only override the default when the property is explicitly present in the file.
		// If absent, leave ProxyEnabled at its default (true) set in Load().
		if ( json.TryGetProperty( "proxyEnabled", out var pe ) )
			ProxyEnabled = pe.GetBoolean();
		if ( json.TryGetProperty( "enableAuthSessions", out var eas ) )
			EnableAuthSessions = eas.ValueKind == JsonValueKind.True;
		if ( json.TryGetProperty( "enableEncryptedRequests", out var eer ) )
			EnableEncryptedRequests = eer.ValueKind == JsonValueKind.True;

		// Sync mappings
		SyncMappings.Clear();
		if ( json.TryGetProperty( "syncMappings", out var mappings ) && mappings.ValueKind == JsonValueKind.Array )
		{
			foreach ( var m in mappings.EnumerateArray() )
			{
				SyncMappings.Add( new SyncMapping
				{
					CsFile = m.TryGetProperty( "csFile", out var cs ) ? cs.GetString() ?? "" : "",
					Collection = m.TryGetProperty( "collection", out var col ) ? col.GetString() ?? "" : "",
					Description = m.TryGetProperty( "description", out var desc ) ? desc.GetString() ?? "" : ""
				} );
			}
		}
	}

	private static void LoadSecretConfig( string path )
	{
		var json = JsonSerializer.Deserialize<JsonElement>( File.ReadAllText( Abs( path ) ), _readOptions );
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
					if ( !string.Equals( val, "api_only", StringComparison.OrdinalIgnoreCase ) )
						Log.Warning( $"[SyncTool] legacy SBOXCOOL_DATA_SOURCE '{val}' is no longer supported; using api_only." );
					DataSource = DataSourceMode.ApiOnly;
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
		DataSource = DataSourceMode.ApiOnly;
		SourceExport = SourceExportMode.SourceOnly;

		// ── Write public config (safe to commit) ──
		var publicConfig = new Dictionary<string, object>
		{
			["projectId"] = ProjectId,
			["publicKey"] = PublicApiKey,
			["baseUrl"] = BaseUrl,
			["cdnUrl"] = CdnUrl,
			["apiVersion"] = ApiVersion,
			["dataFolder"] = DataFolder,
			["dataSource"] = "api_only",
			["sourceExportMode"] = "source_only",
			["enableAuthSessions"] = EnableAuthSessions,
			["enableEncryptedRequests"] = EnableEncryptedRequests,
			["proxyEnabled"] = ProxyEnabled
		};

		if ( SyncMappings.Count > 0 )
		{
			publicConfig["syncMappings"] = SyncMappings.Select( m => new Dictionary<string, string>
			{
				["csFile"] = m.CsFile,
				["collection"] = m.Collection,
				["description"] = m.Description
			} ).ToList();
		}

		File.WriteAllText( Abs( ProjectConfigFile ), JsonSerializer.Serialize( publicConfig, _jsonOptions ) );
		Log.Info( "[SyncTool] Public config saved to config/public/projectConfig.json" );

		// ── Write secret key (gitignored, NEVER published) ──
		var secretConfig = new Dictionary<string, string> { ["secretKey"] = SecretKey };
		File.WriteAllText( Abs( SecretKeyFile ), JsonSerializer.Serialize( secretConfig, _jsonOptions ) );
		Log.Info( "[SyncTool] Secret key saved to config/secret/secret_key.json" );

		// ── Write runtime credentials (ships with game, NO secret key) ──
		// Merges into existing file — preserves any fields set by other tools.
		if ( !string.IsNullOrEmpty( ProjectId ) )
		{
			var credsPath = Abs( RuntimeCredentialsFile );
			var merged = new Dictionary<string, object>();

			// Read existing file to preserve fields we don't own
			if ( File.Exists( credsPath ) )
			{
				try
				{
					var existing = JsonSerializer.Deserialize<JsonElement>( File.ReadAllText( credsPath ), _readOptions );
					foreach ( var prop in existing.EnumerateObject() )
					{
						merged[prop.Name] = prop.Value.ValueKind switch
						{
							JsonValueKind.True => (object)true,
							JsonValueKind.False => false,
							JsonValueKind.Number => prop.Value.TryGetInt64( out var l ) ? l : prop.Value.GetDouble(),
							_ => prop.Value.GetString() ?? ""
						};
					}
				}
				catch { /* Corrupt file — start fresh */ }
			}

			// Overwrite only the fields this save owns
			merged["projectId"] = ProjectId;
			merged["publicKey"] = PublicApiKey;
			merged["baseUrl"] = BaseUrl;
			merged["cdnUrl"] = CdnUrl ?? "";
			merged["apiVersion"] = ApiVersion;
			merged["proxyEnabled"] = ProxyEnabled;
			merged["enableAuthSessions"] = EnableAuthSessions;
			merged["enableEncryptedRequests"] = EnableEncryptedRequests;

			File.WriteAllText( credsPath, JsonSerializer.Serialize( merged, _jsonOptions ) );
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
		DataSource = DataSourceMode.ApiOnly;
		if ( File.Exists( Abs( ProjectConfigFile ) ) )
			Save( SecretKey, PublicApiKey, ProjectId, BaseUrl, DataSource );
	}

	/// <summary>
	/// Update sync mappings and re-save config.
	/// </summary>
	public static void SaveSyncMappings( List<SyncMapping> mappings )
	{
		SyncMappings = mappings ?? new();
		if ( File.Exists( Abs( ProjectConfigFile ) ) )
			Save( SecretKey, PublicApiKey, ProjectId, BaseUrl, DataSource, DataFolder, CdnUrl );
		Log.Info( $"[SyncTool] Saved {SyncMappings.Count} sync mapping(s)" );
	}

	/// <summary>
	/// Get the absolute path to a sync mapping's C# file.
	/// The csFile path is relative to the project root.
	/// </summary>
	public static string GetMappingCsPath( SyncMapping mapping )
		=> Abs( mapping.CsFile );

	/// <summary>
	/// Get the absolute path to a sync mapping's collection YAML source file.
	/// </summary>
	public static string GetMappingCollectionPath( SyncMapping mapping )
		=> Abs( $"{CollectionsPath}/{mapping.Collection}.collection.yml" );

	/// <summary>Path to sync.py script inside the network-storage library.</summary>
	public static string SyncPyPath => "Libraries/sboxcool.network-storage/Editor/sync.py";

	/// <summary>
	/// Update the proxy-enabled preference and re-save config.
	/// Also pushes the setting to the runtime client immediately.
	/// </summary>
	public static void SetProxyEnabled( bool enabled )
	{
		ProxyEnabled = enabled;
		NetworkStorage.ProxyEnabled = enabled;
		if ( File.Exists( Abs( ProjectConfigFile ) ) )
			Save( SecretKey, PublicApiKey, ProjectId, BaseUrl, DataSource, DataFolder, CdnUrl );
		Log.Info( $"[SyncTool] Proxy mode {( enabled ? "ENABLED" : "DISABLED" )}" );
	}

	// ──────────────────────────────────────────────────────
	//  Data file loaders
	// ──────────────────────────────────────────────────────

	/// <summary>Load all collection files from collections/ directory.</summary>
	public static List<(string Name, Dictionary<string, object> Data)> LoadCollections()
	{
		var list = new List<(string, Dictionary<string, object>)>();

		foreach ( var source in LoadSourceCanonicalResources( "collection" ) )
		{
			var dict = JsonSerializer.Deserialize<Dictionary<string, object>>( source.GetRawText(), _readOptions );
			var name = source.TryGetProperty( "name", out var nameProperty )
				? nameProperty.GetString()
				: null;
			if ( string.IsNullOrWhiteSpace( name ) )
				name = dict?.GetValueOrDefault( "name" )?.ToString();
			if ( string.IsNullOrWhiteSpace( name ) && source.TryGetProperty( "id", out var idProperty ) )
				name = idProperty.GetString();
			if ( !string.IsNullOrWhiteSpace( name ) && dict != null )
				list.Add( (name, dict) );
		}
		if ( list.Count > 0 )
			return list;

		return list;
	}

	/// <summary>Return true when an endpoint JSON object is marked deprecated.</summary>
	public static bool IsEndpointDeprecated( JsonElement ep )
	{
		return IsTruthyFlag( ep, "_deprecated" )
			|| IsTruthyFlag( ep, "deprecated" )
			|| IsTruthyFlag( ep, "depreciated" )
			|| IsTruthyFlag( ep, "depricated" );
	}

	private static bool IsTruthyFlag( JsonElement ep, string propertyName )
	{
		if ( !ep.TryGetProperty( propertyName, out var flag ) )
			return false;

		return flag.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.Number => flag.TryGetInt32( out var n ) && n != 0,
			JsonValueKind.String => IsTruthyString( flag.GetString() ),
			_ => false
		};
	}

	private static bool IsTruthyString( string value )
	{
		return value != null && (value.Equals( "true", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "on", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "yes", StringComparison.OrdinalIgnoreCase )
			|| value == "1");
	}

	/// <summary>Load all active endpoint definitions from the endpoints/ directory.</summary>
	public static List<JsonElement> LoadEndpoints( bool includeDeprecated = false )
	{
		var list = new List<JsonElement>();

		var sourceEndpoints = LoadSourceCanonicalResources( "endpoint" );
		if ( sourceEndpoints.Count > 0 )
		{
			foreach ( var ep in sourceEndpoints )
			{
				if ( !includeDeprecated && IsEndpointDeprecated( ep ) )
					continue;

				list.Add( ep );
			}

			return list;
		}

		return list;
	}

	/// <summary>Load all workflow definitions from the workflows/ directory.</summary>
	public static List<JsonElement> LoadWorkflows()
	{
		var sourceWorkflows = LoadSourceCanonicalResources( "workflow" );
		if ( sourceWorkflows.Count > 0 )
			return sourceWorkflows;

		return new List<JsonElement>();
	}

	// ──────────────────────────────────────────────────────
	//  Data file writers (for Pull)
	// ──────────────────────────────────────────────────────

	/// <summary>Save endpoint definitions as individual YAML source files.</summary>
	public static void SaveEndpoints( List<Dictionary<string, object>> endpoints )
	{
		EnsureSyncToolsDir();
		EnsureDir( EndpointsPath );

		foreach ( var file in FindFiles( EndpointsPath, "*.json" ) )
			File.Delete( Abs( $"{EndpointsPath}/{file}" ) );

		foreach ( var ep in endpoints )
		{
			var epJson = JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( ep ) );
			if ( IsEndpointDeprecated( epJson ) )
				continue;

			var slug = ep.TryGetValue( "slug", out var s ) ? s?.ToString() ?? "unknown" : "unknown";
			SyncToolPullWriter.WriteSource( "endpoint", slug, ep );
		}

		Log.Info( $"[SyncTool] Saved {endpoints.Count} endpoint files to endpoints/" );
	}

	/// <summary>Save workflow definitions as individual YAML source files.</summary>
	public static void SaveWorkflows( List<Dictionary<string, object>> workflows )
	{
		EnsureSyncToolsDir();
		EnsureDir( WorkflowsPath );

		foreach ( var file in FindFiles( WorkflowsPath, "*.json" ) )
			File.Delete( Abs( $"{WorkflowsPath}/{file}" ) );

		foreach ( var wf in workflows )
		{
			var id = wf.TryGetValue( "id", out var s ) ? s?.ToString() ?? "unknown" : "unknown";
			SyncToolPullWriter.WriteSource( "workflow", id, wf );
		}

		Log.Info( $"[SyncTool] Saved {workflows.Count} workflow files to workflows/" );
	}

	/// <summary>Save a single workflow to workflows/{id}.workflow.yml.</summary>
	public static void SaveWorkflow( string id, Dictionary<string, object> data )
	{
		EnsureSyncToolsDir();
		EnsureDir( WorkflowsPath );
		SyncToolPullWriter.WriteSource( "workflow", id, data );
		Log.Info( $"[SyncTool] Saved workflows/{id}.workflow.yml" );
	}

	/// <summary>Load all test definitions from the tests/ directory.</summary>
	public static List<JsonElement> LoadTests()
	{
		var list = new List<JsonElement>();
		foreach ( var source in LoadSourceCanonicalResources( "test" ) )
		{
			if ( source.ValueKind == JsonValueKind.Object )
				list.Add( source );
		}

		return list;
	}

	/// <summary>Save test definitions as individual YAML source files.</summary>
	public static void SaveTests( List<Dictionary<string, object>> tests )
	{
		EnsureSyncToolsDir();
		EnsureDir( TestsPath );

		foreach ( var test in tests )
		{
			var id = test.TryGetValue( "id", out var s ) ? s?.ToString() ?? "unknown" : "unknown";
			SyncToolPullWriter.WriteSource( "test", id, test );
		}

		Log.Info( $"[SyncTool] Saved {tests.Count} test files to tests/" );
	}

	/// <summary>Save a collection to collections/{name}.collection.yml.</summary>
	public static void SaveCollection( string name, Dictionary<string, object> data )
	{
		EnsureSyncToolsDir();
		EnsureDir( CollectionsPath );
		SyncToolPullWriter.WriteSource( "collection", name, data );
		Log.Info( $"[SyncTool] Saved collections/{name}.collection.yml" );
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
		return HasSourceFiles();
	}

	/// <summary>Find the local workflow file whose embedded id matches the given workflow id.</summary>
	public static string FindWorkflowFileById( string id )
	{
		var files = FindFiles( WorkflowsPath, "*.workflow.yml" )
			.Concat( FindFiles( WorkflowsPath, "*.workflow.yaml" ) )
			.ToArray();
		var canonical = files.FirstOrDefault( f => string.Equals( ResourceIdFromFilePath( f, "workflow" ), id, StringComparison.OrdinalIgnoreCase ) );
		if ( canonical != null )
			return Abs( $"{WorkflowsPath}/{canonical}" );

		foreach ( var file in files )
		{
			if ( WorkflowFileMatchesId( file, id ) )
				return Abs( $"{WorkflowsPath}/{file}" );
		}

		return null;
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
		EnsureDir( TestsPath );
	}

	private static void DeleteDuplicateWorkflowFiles( string id, string keepFile )
	{
		foreach ( var file in FindFiles( WorkflowsPath, "*.workflow.yml" )
			.Concat( FindFiles( WorkflowsPath, "*.workflow.yaml" ) ) )
		{
			if ( string.Equals( file, keepFile, StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( WorkflowFileMatchesId( file, id ) )
				File.Delete( Abs( $"{WorkflowsPath}/{file}" ) );
		}
	}

	private static bool WorkflowFileMatchesId( string file, string id )
	{
		var fullPath = Abs( $"{WorkflowsPath}/{file}" );
		try
		{
			var wf = TryLoadSourceCanonicalResource( "workflow", fullPath, out var sourceWorkflow ) ? sourceWorkflow : default;
			if ( wf.TryGetProperty( "id", out var wfId ) )
				return string.Equals( wfId.GetString(), id, StringComparison.OrdinalIgnoreCase );
		}
		catch
		{
		}

		return string.Equals( ResourceIdFromFilePath( file, "workflow" ), id, StringComparison.OrdinalIgnoreCase );
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
			["dataSource"] = "api_only",
			["sourceExportMode"] = "source_only",
			["enableAuthSessions"] = false,
			["enableEncryptedRequests"] = false,
			["proxyEnabled"] = true
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
		SyncToolPullWriter.WriteSource( "collection", "players",
			JsonSerializer.Deserialize<Dictionary<string, object>>( sampleCollection, _readOptions ) );

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
		SyncToolPullWriter.WriteSource( "endpoint", "init-player",
			JsonSerializer.Deserialize<Dictionary<string, object>>( sampleEndpoint, _readOptions ) );

		Log.Info( "[NetworkStorage] Scaffolding complete. Open Editor → Network Storage → Setup to enter your API keys." );
	}
}
