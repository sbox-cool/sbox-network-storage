using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor.Mcp;

namespace Sandbox;

/// <summary>
/// Runtime Network Storage tools for sbox-cli. Available in play mode and in games or
/// dedicated servers started with -mcp. Results are data for tests and agents: endpoint
/// and storage failures are returned in the result, not thrown.
/// API and secret keys are never returned.
/// </summary>
[McpToolset( "network_storage", "Network Storage runtime tools. Start with ns_status; use ns_configure if not configured." )]
public static class NetworkStorageMcpTools
{
	/// <summary>
	/// Reports library and configuration state for Network Storage in this runtime. Never returns keys.
	/// Check httpAllowed first: game code can only reach loopback on ports 80/443/8080/8443 and refuses IP-literal hosts.
	/// Next: ns_configure if isConfigured is false, otherwise ns_call_endpoint or ns_get_document.
	/// </summary>
	[McpTool.ReadOnly( "ns_status" )]
	public static object ns_status()
	{
		return BuildStatus();
	}

	/// <summary>
	/// Configures Network Storage. With all arguments empty, runs auto-configuration (once per session) from the
	/// project's synced settings. Otherwise projectId and apiKey are both required; baseUrl is optional.
	/// Returns the same shape as ns_status. Next: ns_status to confirm httpAllowed.
	/// </summary>
	/// <param name="projectId">Project ID from the sboxcool.com dashboard. Leave empty with apiKey to auto-configure.</param>
	/// <param name="apiKey">Public API key (sbox_ns_ prefix). Never echoed back.</param>
	/// <param name="baseUrl">Optional API base URL, for self-hosted servers. Leave empty for the default.</param>
	[McpTool( "ns_configure" )]
	public static object ns_configure( string projectId = "", string apiKey = "", string baseUrl = "" )
	{
		var hasProject = !string.IsNullOrWhiteSpace( projectId );
		var hasKey = !string.IsNullOrWhiteSpace( apiKey );

		if ( !hasProject && !hasKey && string.IsNullOrWhiteSpace( baseUrl ) )
		{
			NetworkStorage.AutoConfigure();
			if ( !NetworkStorage.IsConfigured )
				throw new InvalidOperationException( "Auto-configure found no Network Storage credentials. Pass projectId and apiKey to ns_configure, or add them in Editor → Network Storage → Setup." );
		}
		else
		{
			if ( !hasProject || !hasKey )
				throw new ArgumentException( "projectId and apiKey are both required for manual configuration." );

			NetworkStorage.Configure( projectId, apiKey, string.IsNullOrWhiteSpace( baseUrl ) ? null : baseUrl );
		}

		return BuildStatus();
	}

	/// <summary>
	/// Calls a Network Storage endpoint by slug. Returns { ok, body, errorCode, errorMessage }; endpoint errors are
	/// returned as data (ok=false), not thrown. Throws only if Network Storage is not configured.
	/// Next: ns_call_endpoint again with other input, or ns_game_values to inspect game values.
	/// </summary>
	/// <param name="slug">Endpoint slug, e.g. "get-player-stats".</param>
	/// <param name="input">Request body as JSON. Null sends no body.</param>
	[McpTool( "ns_call_endpoint" )]
	public static async Task<object> ns_call_endpoint( string slug, JsonNode input = null )
	{
		if ( string.IsNullOrWhiteSpace( slug ) )
			throw new ArgumentException( "slug is required. Use ns_status or the editor's endpoint list to find one." );

		NetworkStorage.EnsureConfigured();

		var result = await NetworkStorage.CallEndpoint( slug, input );
		return ToResult( result, "endpoint", slug );
	}

	/// <summary>
	/// Reads a document from a Network Storage collection. documentId empty reads the current player's document (their Steam ID).
	/// Returns { ok, body, errorCode, errorMessage }; storage failures are returned as data (ok=false).
	/// Next: ns_status if the request fails with a configuration error.
	/// </summary>
	/// <param name="collectionId">Collection ID (slug) to read from.</param>
	/// <param name="documentId">Document ID. Leave empty to read the current player's document (their Steam ID).</param>
	[McpTool( "ns_get_document" )]
	public static async Task<object> ns_get_document( string collectionId, string documentId = "" )
	{
		if ( string.IsNullOrWhiteSpace( collectionId ) )
			throw new ArgumentException( "collectionId is required." );

		var result = await NetworkStorage.GetDocument( collectionId, string.IsNullOrWhiteSpace( documentId ) ? null : documentId );
		return ToResult( result, "storage", collectionId );
	}

	/// <summary>
	/// Loads the project's game values (groups and tables). Returns { ok, body, errorCode, errorMessage }; failures are
	/// returned as data (ok=false). Next: ns_call_endpoint to exercise endpoints that use these values.
	/// </summary>
	[McpTool( "ns_game_values" )]
	public static async Task<object> ns_game_values()
	{
		NetworkStorage.EnsureConfigured();

		var result = await NetworkStorage.GetGameValues();
		return ToResult( result, "game-values", "game-values" );
	}

	/// <summary>Removes apiKey query parameters from a URL so it can be echoed safely.</summary>
	public static string RedactUrl( string url )
	{
		if ( string.IsNullOrEmpty( url ) )
			return url;

		var query = url.IndexOf( '?' );
		if ( query < 0 )
			return url;

		var kept = url[(query + 1)..]
			.Split( '&' )
			.Where( part => !part.StartsWith( "apiKey=", StringComparison.OrdinalIgnoreCase ) )
			.ToArray();

		return kept.Length == 0 ? url[..query] : $"{url[..(query + 1)]}{string.Join( "&", kept )}";
	}

	private static object BuildStatus()
	{
		var serverInfoUrl = NetworkStorage.BaseUrl + "/" + NetworkStorage.ApiVersion + "/server-info";
		var httpAllowed = Http.IsAllowed( new Uri( serverInfoUrl ) );

		return new
		{
			libraryVersion = NetworkStorage.LibraryVersion,
			minServerVersion = NetworkStorage.MinServerVersion,
			isConfigured = NetworkStorage.IsConfigured,
			projectId = NetworkStorage.ProjectId,
			baseUrl = RedactUrl( NetworkStorage.BaseUrl ),
			apiVersion = NetworkStorage.ApiVersion,
			publishTarget = NetworkStorage.PublishTarget,
			isEditor = Application.IsEditor,
			isDedicatedServer = Application.IsDedicatedServer,
			httpAllowed,
			hint = httpAllowed
				? null
				: "Game code HTTP is limited to loopback on ports 80/443/8080/8443 (no IP-literal hosts) or to domains on any port. Point baseUrl at a domain, or run the server on an allowed port."
		};
	}

	/// <summary>Maps a library response to { ok, body, errorCode, errorMessage }. Body is raw JSON.</summary>
	private static object ToResult( JsonElement? result, string slug, string lastErrorSlug )
	{
		if ( result.HasValue )
		{
			return new
			{
				ok = true,
				body = JsonNode.Parse( result.Value.GetRawText() ),
				errorCode = (string)null,
				errorMessage = (string)null,
			};
		}

		var hasError = NetworkStorage.TryGetLastEndpointError( lastErrorSlug, out var code, out var message );
		return new
		{
			ok = false,
			body = (JsonNode)null,
			errorCode = hasError ? code : "NO_RESPONSE",
			errorMessage = hasError ? message : $"No response for {slug} '{lastErrorSlug}'. Check the game log for [NetworkStorage] messages.",
		};
	}
}
