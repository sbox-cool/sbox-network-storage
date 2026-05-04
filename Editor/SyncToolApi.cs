using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

/// <summary>
/// HTTP client for the Network Storage v3 management API.
/// Used by the SyncTool editor window to push/pull game data.
/// All requests are authenticated with the secret key from .env.
/// </summary>
public static class SyncToolApi
{
	private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds( 30 ) };
	private static readonly ConcurrentDictionary<string, string> _lastErrorMessagesByPath = new( StringComparer.OrdinalIgnoreCase );
	private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _lastResourceErrorMessagesByPath = new( StringComparer.OrdinalIgnoreCase );
	private static readonly JsonSerializerOptions _readOptions = new()
	{
		AllowTrailingCommas = true,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};

	/// <summary>
	/// Last error code from the server (e.g., "KEY_UPGRADE_REQUIRED", "FORBIDDEN").
	/// Reset on each request. Null if the last request succeeded or had no structured error.
	/// </summary>
	public static string LastErrorCode { get; private set; }

	/// <summary>
	/// Last error message from the server.
	/// </summary>
	public static string LastErrorMessage { get; private set; }

	/// <summary>
	/// Last error message for a specific management API path.
	/// Push All runs resource groups in parallel, so the global message can be
	/// cleared by a sibling request before the UI reads it.
	/// </summary>
	public static string GetLastErrorMessage( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return null;

		return _lastErrorMessagesByPath.TryGetValue( path, out var message ) ? message : null;
	}

	/// <summary>
	/// Last structured error message for a specific resource inside a failed batch request.
	/// </summary>
	public static string GetLastResourceErrorMessage( string path, string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( path ) || string.IsNullOrWhiteSpace( resourceId ) )
			return null;

		return _lastResourceErrorMessagesByPath.TryGetValue( path, out var messages ) &&
			messages.TryGetValue( resourceId, out var message )
				? message
				: null;
	}

	public static void ReportLocalError( string path, string message, Exception ex = null )
	{
		if ( string.IsNullOrWhiteSpace( path ) || string.IsNullOrWhiteSpace( message ) )
			return;

		LastErrorCode = "LOCAL_ERROR";
		LastErrorMessage = message;
		_lastErrorMessagesByPath[path] = message;
		if ( ex != null )
			Log.Warning( $"[SyncTool] {path}: {message}\n{ex}" );
		else
			Log.Warning( $"[SyncTool] {path}: {message}" );
	}

	/// <summary>
	/// Make an authenticated request to the management API.
	/// </summary>
	public static async Task<JsonElement?> Request( string method, string path, JsonElement? body = null, Dictionary<string, string> extraHeaders = null )
	{
		_lastErrorMessagesByPath.TryRemove( path, out _ );
		_lastResourceErrorMessagesByPath.TryRemove( path, out _ );
		LastErrorCode = null;
		LastErrorMessage = null;

		if ( !SyncToolConfig.IsValid )
		{
			Log.Warning( "[SyncTool] Config not valid - load .env first" );
			return null;
		}

		var url = $"{SyncToolConfig.BaseUrl}/{SyncToolConfig.ApiVersion}/manage/{SyncToolConfig.ProjectId}/{path}";

		var sk = SyncToolConfig.SecretKey ?? "";
		var pk = SyncToolConfig.PublicApiKey ?? "";
		Log.Info( $"[SyncTool] {method} {path}" );

		var request = new HttpRequestMessage( new HttpMethod( method ), url );
		request.Headers.Add( "x-api-key", sk );
		request.Headers.Add( "x-public-key", pk );
		request.Headers.Add( "User-Agent", "SyncTool-sbox/2.0" );

		if ( extraHeaders != null )
		{
			foreach ( var header in extraHeaders )
				request.Headers.TryAddWithoutValidation( header.Key, header.Value );
		}

		if ( body.HasValue )
		{
			var json = JsonSerializer.Serialize( body.Value );
			request.Content = new StringContent( json, Encoding.UTF8, "application/json" );
		}

		try
		{
			var response = await _http.SendAsync( request );
			var text = await response.Content.ReadAsStringAsync();

			if ( !response.IsSuccessStatusCode )
			{
				Log.Warning( $"[SyncTool] {method} {path}: HTTP {(int)response.StatusCode}" );
				try
				{
					var errJson = JsonSerializer.Deserialize<JsonElement>( text, _readOptions );
					if ( errJson.TryGetProperty( "error", out var errCode ) )
						LastErrorCode = errCode.GetString();
					if ( errJson.TryGetProperty( "message", out var errMsg ) )
						LastErrorMessage = errMsg.GetString();

					var msg = LastErrorMessage ?? text;
					var detail = !string.IsNullOrEmpty( LastErrorCode )
						? $"{LastErrorCode}: {msg}"
						: msg;
					LastErrorMessage = detail;
					_lastErrorMessagesByPath[path] = detail;

					CaptureStructuredErrors( path, errJson );
					LogStructuredErrors( path );

					// Skip error window for 404/405 - these are expected "endpoint not available" errors handled by callers
					var statusCode = (int)response.StatusCode;
					var isExpectedNotFound = statusCode == 404 || statusCode == 405;
					if ( !isExpectedNotFound && (!string.IsNullOrEmpty( LastErrorCode ) || !string.IsNullOrEmpty( LastErrorMessage )) )
						EndpointErrorWindow.Show( path, errJson );

					if ( LastErrorCode == "KEY_UPGRADE_REQUIRED" )
					{
						Log.Warning( "[SyncTool] Your secret key uses an old format. Generate a new one at sbox.cool." );
					}
					else if ( LastErrorCode == "FORBIDDEN" )
					{
						Log.Warning( $"[SyncTool] Permission denied: {LastErrorMessage}" );
					}
					else if ( LastErrorCode == "COLLECTION_DELETE_WEBSITE_ONLY" )
					{
						Log.Warning( "[SyncTool] Collection definitions can only be deleted from the website dashboard after verification. Row/document deletion through the runtime API is still supported." );
					}
				}
				catch
				{
					var detail = TruncateForLog( text, 500 );
					LastErrorMessage = string.IsNullOrWhiteSpace( detail )
						? $"HTTP {(int)response.StatusCode}"
						: $"HTTP {(int)response.StatusCode}: {detail}";
					_lastErrorMessagesByPath[path] = LastErrorMessage;
					Log.Warning( $"[SyncTool] -> {detail}" );
					MessageDialog.Show( $"Sync Error: {path}", LastErrorMessage, text );
				}
				return null;
			}

			var result = JsonSerializer.Deserialize<JsonElement>( text, _readOptions );
			LogStructuredWarnings( path, result );

			// Check for validation errors in successful HTTP responses (ok: false)
			if ( result.TryGetProperty( "ok", out var okProp ) && okProp.ValueKind == JsonValueKind.False )
			{
				if ( result.TryGetProperty( "error", out var errCode ) )
					LastErrorCode = errCode.GetString();
				if ( result.TryGetProperty( "message", out var errMsg ) )
					LastErrorMessage = errMsg.GetString();
				_lastErrorMessagesByPath[path] = LastErrorMessage ?? LastErrorCode ?? "Validation failed";
				CaptureStructuredErrors( path, result );
				LogStructuredErrors( path );

				// Only show the error window if there's an actual error code or message
				if ( !string.IsNullOrEmpty( LastErrorCode ) || !string.IsNullOrEmpty( LastErrorMessage ) )
					EndpointErrorWindow.Show( path, result );
			}

			return result;
		}
		catch ( Exception ex )
		{
			LastErrorMessage = ex.Message;
			_lastErrorMessagesByPath[path] = ex.Message;
			Log.Warning( $"[SyncTool] {method} {path}: {ex.Message}" );
			return null;
		}
	}

	/// <summary>Fetch current server endpoints (includes staged/next-revision endpoints).</summary>
	public static Task<JsonElement?> GetEndpoints() => Request( "GET", "endpoints?includeStaged=true" );

	/// <summary>Push endpoints to server.</summary>
	public static Task<JsonElement?> PushEndpoints( JsonElement data ) => Request( "PUT", "endpoints", data );

	/// <summary>Push endpoints to server with publish-target support.</summary>
	public static Task<JsonElement?> PushEndpoints( JsonElement data, string publishTarget )
	{
		var headers = publishTarget != "live" ? new Dictionary<string, string> { ["x-ns-publish-target"] = publishTarget } : null;
		return Request( "PUT", "endpoints", data, headers );
	}
 

	/// <summary>Upsert a single endpoint. Server handles merge.</summary>
	public static Task<JsonElement?> PatchEndpoint( JsonElement data, string publishTarget = "live" )
	{
		var headers = publishTarget != "live" ? new Dictionary<string, string> { ["x-ns-publish-target"] = publishTarget } : null;
		return Request( "PATCH", "endpoints", data, headers );
	}

	/// <summary>Upsert a single collection. Server handles merge.</summary>
	public static Task<JsonElement?> PatchCollection( JsonElement data, string publishTarget = "live" )
	{
		var headers = publishTarget != "live" ? new Dictionary<string, string> { ["x-ns-publish-target"] = publishTarget } : null;
		return Request( "PATCH", "collections", data, headers );
	}

	/// <summary>Upsert a single workflow. Server handles merge.</summary>
	public static Task<JsonElement?> PatchWorkflow( JsonElement data )
	{
		return Request( "PATCH", "workflows", data );
	}
 	/// <summary>Push endpoints to server asynchronously. Returns { jobId } immediately, processes in background.</summary>
 	public static Task<JsonElement?> PushEndpointsAsync( JsonElement data, string publishTarget = "live" )
 	{
 		var headers = new Dictionary<string, string>
 		{
 			["x-ns-publish-target"] = publishTarget ?? "live"
 		};
 		return Request( "PUT", "endpoints?async=true", data, headers );
 	}
 
 	/// <summary>Poll async sync job status.</summary>
 	public static Task<JsonElement?> GetSyncJobStatus( string jobId )
 	{
 		return Request( "GET", $"sync-jobs/{jobId}" );
 	}

	/// <summary>Fetch current server collections (includes staged/next-revision collections).</summary>
	public static Task<JsonElement?> GetCollections() => Request( "GET", "collections?includeStaged=true" );

	/// <summary>Push collection schemas to server.</summary>
	public static Task<JsonElement?> PushCollections( JsonElement data ) => Request( "PUT", "collections", data );

	/// <summary>Push collection schemas to server with publish-target support.</summary>
	public static Task<JsonElement?> PushCollections( JsonElement data, string publishTarget )
	{
		var headers = publishTarget != "live" ? new Dictionary<string, string> { ["x-ns-publish-target"] = publishTarget } : null;
		return Request( "PUT", "collections", data, headers );
	}

	/// <summary>Fetch current server workflows.</summary>
	public static Task<JsonElement?> GetWorkflows() => Request( "GET", "workflows" );

	/// <summary>Push workflows to server.</summary>
	public static Task<JsonElement?> PushWorkflows( JsonElement data ) => Request( "PUT", "workflows", data );

	/// <summary>Fetch read-only project settings used by the editor/runtime config.</summary>
	public static Task<JsonElement?> GetProjectSettings() => Request( "GET", "settings" );

	/// <summary>Fetch current server tests.</summary>
	public static Task<JsonElement?> GetTests() => Request( "GET", "tests" );

	/// <summary>Push tests to server.</summary>
	public static Task<JsonElement?> PushTests( JsonElement data ) => Request( "PUT", "tests", data );

 	/// <summary>Ask backend compiler to canonicalize and safely upgrade one source file.</summary>
	public static Task<JsonElement?> UpgradeSource( JsonElement data ) => Request( "POST", "source-upgrade", data );

	/// <summary>Run a single test via dry-run.</summary>
	public static Task<JsonElement?> RunTest( JsonElement data ) => Request( "POST", "test-endpoint", data );

	/// <summary>Run all tests via dry-run.</summary>
	public static Task<JsonElement?> RunAllTests( JsonElement data ) => Request( "POST", "run-tests", data );

	/// <summary>Suggest tests for an endpoint.</summary>
	public static Task<JsonElement?> SuggestTests( JsonElement data ) => Request( "POST", "suggest-tests", data );

	/// <summary>Auto-test one or all endpoints (no saved tests needed). Pass { slug } for one, {} for all.</summary>
	public static Task<JsonElement?> AutoTest( JsonElement data ) => Request( "POST", "auto-test", data );

	/// <summary>
	/// Push all resources (endpoints, collections, workflows) in a single batch request.
	/// Server processes synchronously and returns combined results.
	/// </summary>
	public static Task<JsonElement?> PushSync( JsonElement data, string publishTarget = "live" )
	{
		var headers = publishTarget != "live" ? new Dictionary<string, string> { ["x-ns-publish-target"] = publishTarget } : null;
		return Request( "PUT", "sync", data, headers );
	}

	/// <summary>Sync package/revision info with backend.</summary>
	public static Task<JsonElement?> SyncPackageInfo( JsonElement data ) => Request( "POST", "package-sync", data );

	/// <summary>Fetch stored game-package/revision state from backend.</summary>
	public static Task<JsonElement?> GetGamePackage() => Request( "GET", "game-package" );

	/// <summary>
	/// Validate credentials against the server.
	/// Sends secret key via x-api-key header and optionally public key via x-public-key header.
	/// Returns { ok, project, checks, permissions? }
	/// </summary>
	public static async Task<JsonElement?> Validate( string publicKey = null )
	{
		LastErrorCode = null;
		LastErrorMessage = null;

		if ( !SyncToolConfig.IsValid )
		{
			Log.Warning( "[SyncTool] Validate: config not valid" );
			return null;
		}

		var url = $"{SyncToolConfig.BaseUrl}/{SyncToolConfig.ApiVersion}/manage/{SyncToolConfig.ProjectId}/validate";
		var sk = SyncToolConfig.SecretKey ?? "";
		var pk = publicKey ?? "";

		Log.Info( $"[SyncTool] Validating credentials..." );

		var request = new HttpRequestMessage( HttpMethod.Get, url );
		request.Headers.Add( "x-api-key", sk );
		request.Headers.Add( "User-Agent", "SyncTool-sbox/2.0" );

		if ( !string.IsNullOrEmpty( publicKey ) )
			request.Headers.Add( "x-public-key", publicKey );

		try
		{
			var response = await _http.SendAsync( request );
			var text = await response.Content.ReadAsStringAsync();

			if ( !response.IsSuccessStatusCode )
			{
				LastErrorCode = $"HTTP_{(int)response.StatusCode}";
				LastErrorMessage = $"Server returned HTTP {(int)response.StatusCode}";
				Log.Warning( $"[SyncTool] Validate failed: HTTP {(int)response.StatusCode}" );
				return null;
			}

			var result = JsonSerializer.Deserialize<JsonElement>( text, _readOptions );

			if ( result.TryGetProperty( "error", out var errCode ) )
			{
				LastErrorCode = errCode.GetString();
				if ( result.TryGetProperty( "message", out var errMsg ) )
					LastErrorMessage = errMsg.GetString();
				Log.Warning( $"[SyncTool] Validate error: {LastErrorCode} - {LastErrorMessage}" );
			}

			return result;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SyncTool] Validate: {ex.Message}" );
			return null;
		}
	}

	private static void CaptureStructuredErrors( string path, JsonElement payload )
	{
		var resourceArray = GetStructuredResourceArray( payload );
		if ( !resourceArray.HasValue || resourceArray.Value.ValueKind != JsonValueKind.Array )
			return;

		var messages = new ConcurrentDictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var item in resourceArray.Value.EnumerateArray() )
		{
			if ( item.ValueKind != JsonValueKind.Object )
				continue;

			if ( item.TryGetProperty( "ok", out var ok ) && ok.ValueKind == JsonValueKind.True )
				continue;

			var resourceId = GetStringProperty( item, "resourceId", "slug", "name", "id" );
			if ( string.IsNullOrWhiteSpace( resourceId ) )
				continue;

			var detail = BuildStructuredErrorDetail( item );
			if ( !string.IsNullOrWhiteSpace( detail ) )
				messages[resourceId] = detail;
		}

		if ( messages.Count > 0 )
			_lastResourceErrorMessagesByPath[path] = messages;
	}

	private static void LogStructuredErrors( string path )
	{
		if ( !_lastResourceErrorMessagesByPath.TryGetValue( path, out var messages ) )
			return;

		foreach ( var pair in messages )
			Log.Warning( $"[SyncTool] {path}:{pair.Key} -> {pair.Value}" );
	}

	private static void LogStructuredWarnings( string path, JsonElement payload )
	{
		var resourceArray = GetStructuredResourceArray( payload );
		if ( !resourceArray.HasValue || resourceArray.Value.ValueKind != JsonValueKind.Array )
			return;

		foreach ( var item in resourceArray.Value.EnumerateArray() )
		{
			if ( item.ValueKind != JsonValueKind.Object )
				continue;

			var resourceId = GetStringProperty( item, "resourceId", "slug", "name", "id" ) ?? "(unknown)";
			if ( !item.TryGetProperty( "diagnostics", out var diagnostics ) || diagnostics.ValueKind != JsonValueKind.Array )
				continue;

			foreach ( var diagnostic in diagnostics.EnumerateArray() )
			{
				var severity = GetStringProperty( diagnostic, "severity" );
				if ( string.Equals( severity, "error", StringComparison.OrdinalIgnoreCase ) )
					continue;

				var formatted = FormatDiagnostic( diagnostic );
				if ( string.IsNullOrWhiteSpace( formatted ) )
					continue;

				// Only log warnings, skip info-level messages
				if ( !string.Equals( severity, "info", StringComparison.OrdinalIgnoreCase ) )
					Log.Warning( $"[SyncTool] {path}:{resourceId} {severity} -> {formatted}" );
			}
		}
	}

	private static JsonElement? GetStructuredResourceArray( JsonElement payload )
	{
		foreach ( var key in new[] { "results", "resources", "items" } )
		{
			if ( payload.TryGetProperty( key, out var value ) && value.ValueKind == JsonValueKind.Array )
				return value;
		}

		return null;
	}

	private static string BuildStructuredErrorDetail( JsonElement item )
	{
		var parts = new List<string>();
		var message = GetStringProperty( item, "message" );
		var error = GetStringProperty( item, "error" );
		if ( !string.IsNullOrWhiteSpace( message ) &&
			!string.Equals( message, "One or more endpoints failed validation.", StringComparison.OrdinalIgnoreCase ) &&
			!string.Equals( message, "One or more collections failed validation.", StringComparison.OrdinalIgnoreCase ) &&
			!string.Equals( message, "One or more workflows failed validation.", StringComparison.OrdinalIgnoreCase ) )
		{
			parts.Add( message );
		}
		else if ( !string.IsNullOrWhiteSpace( error ) && !string.Equals( error, "VALIDATION_FAILED", StringComparison.OrdinalIgnoreCase ) )
		{
			parts.Add( error );
		}

		if ( item.TryGetProperty( "diagnostics", out var diagnostics ) && diagnostics.ValueKind == JsonValueKind.Array )
		{
			foreach ( var diagnostic in diagnostics.EnumerateArray() )
			{
				var formatted = FormatDiagnostic( diagnostic );
				if ( !string.IsNullOrWhiteSpace( formatted ) )
					parts.Add( formatted );
			}
		}

		if ( parts.Count == 0 && item.TryGetProperty( "errors", out var errors ) && errors.ValueKind == JsonValueKind.Array )
		{
			foreach ( var errorItem in errors.EnumerateArray() )
			{
				var formatted = FormatDiagnostic( errorItem );
				if ( !string.IsNullOrWhiteSpace( formatted ) )
					parts.Add( formatted );
			}
		}

		return parts.Count > 0 ? string.Join( " | ", parts ) : GetStringProperty( item, "message", "error" );
	}

	private static string FormatDiagnostic( JsonElement diagnostic )
	{
		if ( diagnostic.ValueKind == JsonValueKind.String )
			return diagnostic.GetString();

		if ( diagnostic.ValueKind != JsonValueKind.Object )
			return diagnostic.ToString();

		var code = GetStringProperty( diagnostic, "code", "type", "budgetCategory" );
		var message = GetStringProperty( diagnostic, "message", "detail", "reason", "error" );
		var head = !string.IsNullOrWhiteSpace( code ) && !string.IsNullOrWhiteSpace( message )
			? $"{code}: {message}"
			: !string.IsNullOrWhiteSpace( message ) ? message : code;

		var context = new List<string>();
		var sourcePath = GetStringProperty( diagnostic, "sourcePath", "path", "file" );
		var sourcePointer = GetStringProperty( diagnostic, "sourcePointer" );
		if ( !string.IsNullOrWhiteSpace( sourcePath ) )
		{
			var line = GetIntProperty( diagnostic, "line", "sourceLine" );
			var column = GetIntProperty( diagnostic, "column", "sourceColumn" );
			var location = sourcePath;
			if ( line.HasValue )
				location += column.HasValue ? $":{line}:{column}" : $":{line}";
			context.Add( location );
		}
		else if ( !string.IsNullOrWhiteSpace( sourcePointer ) )
		{
			context.Add( sourcePointer );
		}

		var nodeId = GetStringProperty( diagnostic, "nodeId", "canonicalNode", "stepId" );
		if ( !string.IsNullOrWhiteSpace( nodeId ) )
			context.Add( $"node={nodeId}" );

		var suggestion = GetStringProperty( diagnostic, "suggestedFix", "suggestion", "fix" );
		if ( !string.IsNullOrWhiteSpace( suggestion ) )
			context.Add( $"fix={suggestion}" );

		if ( string.IsNullOrWhiteSpace( head ) )
			head = diagnostic.ToString();

		return context.Count > 0 ? $"{head} ({string.Join( ", ", context )})" : head;
	}

	private static string GetStringProperty( JsonElement element, params string[] keys )
	{
		foreach ( var key in keys )
		{
			if ( element.TryGetProperty( key, out var value ) && value.ValueKind == JsonValueKind.String )
				return value.GetString();
		}

		return null;
	}

	private static int? GetIntProperty( JsonElement element, params string[] keys )
	{
		foreach ( var key in keys )
		{
			if ( !element.TryGetProperty( key, out var value ) || value.ValueKind != JsonValueKind.Number )
				continue;

			if ( value.TryGetInt32( out var number ) )
				return number;
		}

		return null;
	}

	private static string PrettyPrintJson( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return text;

		try
		{
			var parsed = JsonSerializer.Deserialize<JsonElement>( text, _readOptions );
			return JsonSerializer.Serialize( parsed, new JsonSerializerOptions { WriteIndented = true } );
		}
		catch
		{
			return text;
		}
	}

	private static string TruncateForLog( string text, int maxLength )
	{
		if ( string.IsNullOrEmpty( text ) || text.Length <= maxLength )
			return text;

		return $"{text[..maxLength]}... (truncated, {text.Length} chars)";
	}
}
