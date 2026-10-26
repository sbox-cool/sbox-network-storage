using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

/// <summary>
/// Editor-only compatibility prompts for Setup and the Sync Tool. Reads <c>/v3/server-info</c>,
/// the <c>x-ns-library-status</c> header and the newest published package revision, and turns them
/// into one notice. Nothing here runs in play mode or in a published game (this assembly is editor-only),
/// and every lookup failure (offline, older server, no package) leaves the state silent and non-blocking.
/// </summary>
public static class EditorCompatibility
{
	/// <summary>Capabilities this library version needs from the server, with the feature that depends on each.</summary>
	private static readonly (string Capability, string Feature)[] RequiredCapabilities =
	{
		( "records", "document storage" ),
		( "endpoints", "endpoints" ),
		( "queries", "queries" ),
		( "game-values", "game values" ),
		( "auth-sessions", "auth sessions" ),
		( "package-sync", "package sync" ),
	};

	private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds( 5 ) };
	private static bool _deprecatedShown;
	private static Notice _dismissedBanner;
	private static int _generation;
	private static string _lastRoot;

	public sealed record Notice( string Level, string Title, string Message );

	public static string Status { get; private set; } = "unknown";
	public static string ServerVersion { get; private set; }
	public static Notice Current { get; private set; }

	/// <summary>Push All is refused for unsupported and blocked libraries. Pull and Test Live keep working.</summary>
	public static bool PushBlocked => Status is "unsupported" or "blocked";
	/// <summary>Greedy word wrap for the Paint-based windows; roughly 6.2 px per character at the 9 pt font.</summary>
	public static List<string> Wrap( string text, float width )
	{
		var limit = Math.Max( 30, (int)(width / 6.2f) );
		var lines = new List<string>();
		var line = "";
		foreach ( var word in (text ?? "").Split( ' ' ) )
		{
			if ( line.Length > 0 && line.Length + 1 + word.Length > limit )
			{
				lines.Add( line );
				line = word;
			}
			else line = line.Length == 0 ? word : line + " " + word;
		}
		if ( line.Length > 0 ) lines.Add( line );
		return lines;
	}

	/// <summary>Why Push All is refused; <see cref="PushBlockedSteps"/> says how to fix it.</summary>
	public static string PushBlockedMessage =>
		$"This library ({NetworkStorage.LibraryVersion}) is {Status} for this server, so Push All is disabled. Pull and Test Live still work.";

	public static string PushBlockedSteps =>
		"1. Open Library Manager and update 'Network Storage' (sbox.game/sboxcool/network-storage).\n" +
		"2. If Library Manager cannot update it, delete the whole Libraries/sboxcool.network-storage folder and install the library again. " +
		"Replacing only some files causes duplicate-definition errors (CS0121).\n" +
		"3. Restart the editor and reopen the Sync Tool.";

	/// <summary>
	/// The banner to draw, or null when there is nothing to say. Dismissing hides only the notice that was
	/// on screen: a different one (for example outdated turning into unsupported) shows again.
	/// </summary>
	public static Notice Banner => Current is null || Current.Level == "modal" || Current == _dismissedBanner ? null : Current;

	public static void DismissBanner() => _dismissedBanner = Current;

	/// <summary>The once-per-session modal for a deprecated library, then null.</summary>
	public static Notice TakeModal()
	{
		if ( Current is not { Level: "modal" } notice || _deprecatedShown ) return null;
		_deprecatedShown = true;
		return notice;
	}

	/// <summary>Reads server-info and the package feed. Safe to call whenever a window opens or Connected is shown.</summary>
	public static async Task RefreshAsync()
	{
		if ( !SyncToolConfig.IsValid ) return;
		var root = $"{SyncToolConfig.BaseUrl?.TrimEnd( '/' )}";
		if ( root != _lastRoot )
		{
			_lastRoot = root;
			_dismissedBanner = null;
		}

		var generation = ++_generation;
		string status = null;
		string version = null;
		HashSet<string> capabilities = null;
		string sunset = null;
		string minimum = null;
		try
		{
			using var request = new HttpRequestMessage( HttpMethod.Get, $"{root}/v3/server-info" );
			request.Headers.Add( "x-ns-library-version", NetworkStorage.LibraryVersion );
			request.Headers.Add( "x-ns-client-type", "editor" );
			using var response = await _http.SendAsync( request );
			if ( response.IsSuccessStatusCode )
			{
				if ( response.Headers.TryGetValues( "x-ns-library-status", out var statuses ) )
					status = statuses.FirstOrDefault();
				using var document = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
				var info = document.RootElement;
				if ( info.TryGetProperty( "version", out var v ) && v.ValueKind == JsonValueKind.String )
					version = v.GetString();
				if ( info.TryGetProperty( "capabilities", out var caps ) && caps.ValueKind == JsonValueKind.Array )
					capabilities = caps.EnumerateArray().Where( c => c.ValueKind == JsonValueKind.String ).Select( c => c.GetString() ).ToHashSet( StringComparer.Ordinal );
				if ( info.TryGetProperty( "compat", out var compat ) && compat.TryGetProperty( "library", out var library ) )
				{
					if ( library.TryGetProperty( "deprecated", out var deprecated ) && deprecated.ValueKind == JsonValueKind.Array )
						sunset = deprecated.EnumerateArray()
							.Select( d => d.TryGetProperty( "sunset", out var s ) && s.ValueKind == JsonValueKind.String ? s.GetString() : null )
							.Where( s => !string.IsNullOrEmpty( s ) ).OrderBy( s => s, StringComparer.Ordinal ).FirstOrDefault();
					if ( library.TryGetProperty( "minimum", out var min ) && min.ValueKind == JsonValueKind.String )
						minimum = min.GetString();
				}
			}
		}
		catch ( Exception ex )
		{
			// Offline or an older server: no prompt, no dialog.
			Log.Info( $"[NetworkStorage] Compatibility check skipped: {ex.Message}" );
		}

		long? latestRevision = null;
		try
		{
			var package = await Package.FetchAsync( "sboxcool.network-storage", false );
			latestRevision = package?.Revision?.VersionId;
		}
		catch ( Exception ex )
		{
			Log.Info( $"[NetworkStorage] Library package lookup skipped: {ex.Message}" );
		}

		if ( generation != _generation ) return;
		Status = status ?? "unknown";
		ServerVersion = version;
		Current = Evaluate( Status, NetworkStorage.LibraryVersion, ReadLocalRevision( SyncToolConfig.ProjectRoot ), latestRevision, sunset, version, capabilities, minimum );
	}

	/// <summary>Pure decision logic, kept free of editor and network calls so it can be unit tested.</summary>
	public static Notice Evaluate( string status, string libraryVersion, long? localRevision, long? latestRevision, string sunset,
		string serverVersion, IReadOnlyCollection<string> capabilities, string serverMinimum )
	{
		var packageNewer = localRevision.HasValue && latestRevision.HasValue && latestRevision.Value > localRevision.Value;
		var update = packageNewer ? $" A newer revision (1.0.{latestRevision}) is published. Restart the editor after updating." : "";
		var serverWarning = ServerWarning( serverVersion, capabilities );
		switch ( status )
		{
			case "blocked":
				return new Notice( "error", "Library blocked", $"Library {libraryVersion} is blocked by this server. Update 'Network Storage' in Library Manager, or replace the whole library folder.{update}" );
			case "unsupported":
				return new Notice( "error", "Library unsupported", $"Library {libraryVersion} is below this server's minimum{( string.IsNullOrEmpty( serverMinimum ) ? "" : $" ({serverMinimum})" )}. Update it before pushing.{update}" );
			case "deprecated":
				var date = string.IsNullOrEmpty( sunset ) ? "an announced date" : sunset;
				return new Notice( "modal", "Library deprecated",
					$"Library {libraryVersion} still works but is deprecated and stops being supported on {date}. Update 'Network Storage' in Library Manager.{update}" );
			case "outdated":
				return new Notice( "warning", "Library update available", $"Library {libraryVersion} is outdated for this server.{update}{( serverWarning is null ? "" : " " + serverWarning )}" );
		}

		if ( packageNewer )
			return new Notice( "info", "Library update available", $"A newer library revision (1.0.{latestRevision}) is published. Restart the editor after updating.{( serverWarning is null ? "" : " " + serverWarning )}" );
		return serverWarning is null ? null : new Notice( "warning", "Server is older than this library", serverWarning );
	}

	/// <summary>Says which features are off when the server is older than the library baseline or lacks a capability.</summary>
	public static string ServerWarning( string serverVersion, IReadOnlyCollection<string> capabilities )
	{
		var parts = new List<string>();
		if ( !string.IsNullOrEmpty( serverVersion ) && IsOlder( serverVersion, NetworkStorage.MinServerVersion ) )
			parts.Add( $"Server {serverVersion} is older than {NetworkStorage.MinServerVersion}, the oldest this library is built for." );
		if ( capabilities is not null )
		{
			var missing = RequiredCapabilities.Where( r => !capabilities.Contains( r.Capability ) ).Select( r => r.Feature ).ToArray();
			if ( missing.Length > 0 )
				parts.Add( $"The server does not report support for: {string.Join( ", ", missing )}. Those features are turned off." );
		}
		return parts.Count == 0 ? null : string.Join( " ", parts );
	}

	/// <summary>
	/// True when <paramref name="version"/> is below <paramref name="baseline"/>. A server built from source reports
	/// the placeholder <c>0.1.0-dev</c>, which says nothing about its features, so it is never "older".
	/// </summary>
	private static bool IsOlder( string version, string baseline )
		=> !version.EndsWith( "-dev", StringComparison.OrdinalIgnoreCase )
			&& TryParse( version, out var a ) && TryParse( baseline, out var b ) && a < b;

	private static bool TryParse( string text, out Version version )
	{
		version = null;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var core = text.TrimStart( 'v', 'V' ).Split( '-', '+' )[0];
		return Version.TryParse( core, out version );
	}

	/// <summary>
	/// The installed package revision (<c>1.0.{n}</c> in the library's <c>.version</c>), or null when there is nothing to
	/// compare. A git checkout (a <c>.git</c> entry in the library folder) has null: its committed <c>.version</c> is not
	/// updated when packages are published, so comparing it would report an update that is not needed (#52).
	/// </summary>
	public static long? ReadLocalRevision( string projectRoot )
	{
		try
		{
			var library = Path.Combine( projectRoot, "Libraries", "sboxcool.network-storage" );
			var git = Path.Combine( library, ".git" );
			if ( Directory.Exists( git ) || File.Exists( git ) ) return null;
			var text = File.ReadAllText( Path.Combine( library, ".version" ) ).Trim();
			const string prefix = "1.0.";
			if ( text.StartsWith( prefix, StringComparison.Ordinal ) && long.TryParse( text[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var revision ) )
				return revision;
		}
		catch ( Exception ) { }
		return null;
	}
}
