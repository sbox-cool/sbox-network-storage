using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	/// <summary>Last server version reported by server-info or a response header; null on older servers.</summary>
	public static string ServerVersion { get; private set; }

	/// <summary>Last reported library status. Unknown metadata never disables an existing feature.</summary>
	public static string LibraryStatus { get; private set; } = "unknown";

	/// <summary>API root path the server advertises in server-info (<c>api.root</c>, for example "/api"); null when none.</summary>
	public static string ServerApiRoot { get; private set; }

	/// <summary>Raised only when the reported status changes. No player-facing UI is created.</summary>
	public static event Action<string> OnLibraryStatusChanged;

	private static HashSet<string> _serverCapabilities;
	private static readonly HashSet<string> _loggedLibraryStatuses = new( StringComparer.Ordinal );
	private static int _compatibilityGeneration;
	private static string _compatibilityRoot;
	private static string _compatibilityProject;
	private static string _compatibilityKey;
	private static Task _serverInfoTask = Task.CompletedTask;

	/// <summary>Whether a feature is available. Servers without capability metadata retain legacy behavior.</summary>
	public static bool HasCapability( string name ) => !string.IsNullOrWhiteSpace( name )
		&& (_serverCapabilities is null || _serverCapabilities.Contains( name ));

	private static void InitializeServerCompatibility()
	{
		if ( _compatibilityRoot == ApiRoot && _compatibilityProject == ProjectId && _compatibilityKey == ApiKey ) return;
		_compatibilityRoot = ApiRoot;
		_compatibilityProject = ProjectId;
		_compatibilityKey = ApiKey;
		ServerVersion = null;
		ServerApiRoot = null;
		LibraryStatus = "unknown";
		_serverCapabilities = null;
		var generation = ++_compatibilityGeneration;
		_serverInfoTask = ReadServerInfoAsync( BaseUrl, ApiVersion, generation );
	}

	private static async Task ReadServerInfoAsync( string baseUrl, string apiVersion, int generation )
	{
		try
		{
			using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( 5 ) );
			var headers = new Dictionary<string, string> { ["x-ns-library-version"] = LibraryVersion };
			var sentAt = DateTimeOffset.UtcNow;
			using var response = await Http.RequestAsync( $"{baseUrl}/{apiVersion}/server-info", "GET", null, headers, timeout.Token );
			if ( generation == _compatibilityGeneration ) ObserveServerDateHeader( response, sentAt, DateTimeOffset.UtcNow );
			if ( generation != _compatibilityGeneration || !response.IsSuccessStatusCode ) return;
			var raw = await response.Content.ReadAsStringAsync( timeout.Token );
			if ( generation != _compatibilityGeneration ) return;
			using var document = JsonDocument.Parse( raw );
			var info = document.RootElement;
			if ( info.ValueKind != JsonValueKind.Object ) return;
			if ( info.TryGetProperty( "version", out var version ) && version.ValueKind == JsonValueKind.String )
				ServerVersion = version.GetString();
			if ( info.TryGetProperty( "capabilities", out var capabilities ) && capabilities.ValueKind == JsonValueKind.Array )
				_serverCapabilities = capabilities.EnumerateArray().Where( c => c.ValueKind == JsonValueKind.String )
					.Select( c => c.GetString() ).ToHashSet( StringComparer.Ordinal );
			if ( info.TryGetProperty( "api", out var api ) && api.ValueKind == JsonValueKind.Object
				&& api.TryGetProperty( "root", out var root ) && root.ValueKind == JsonValueKind.String )
				ServerApiRoot = ValidServerApiRoot( root.GetString() );
			ObserveCompatibilityHeaders( response );
		}
		catch ( Exception )
		{
			// The lookup is optional: offline and older servers keep the existing request path.
		}
	}

	/// <summary>
	/// Accepts only a plain absolute path such as "/api": no scheme or host, query, fragment, backslash, control
	/// character or dot segment. Anything else is ignored and the library keeps its versioned root.
	/// </summary>
	private static string ValidServerApiRoot( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || path[0] != '/' || path.Length > 128 ) return null;
		if ( path.Contains( "..", StringComparison.Ordinal ) || path.Contains( "//", StringComparison.Ordinal ) ) return null;
		if ( path.Any( c => c == '\\' || c == '?' || c == '#' || char.IsControl( c ) ) ) return null;
		var trimmed = path.TrimEnd( '/' );
		return trimmed.Length == 0 ? null : trimmed;
	}

	private static void ObserveCompatibilityHeaders( HttpResponseMessage response )
	{
		string version = null;
		string status = null;
		if ( response.Headers.TryGetValues( "x-ns-server-version", out var versions ) ) version = versions.FirstOrDefault();
		if ( response.Headers.TryGetValues( "x-ns-library-status", out var statuses ) ) status = statuses.FirstOrDefault();
		ApplyCompatibilityMetadata( version, status );
	}

	/// <summary>
	/// Applies the values of the <c>x-ns-server-version</c> and <c>x-ns-library-status</c> response headers.
	/// Null or unrecognized values change nothing, so servers without these headers behave as before.
	/// </summary>
	public static void ApplyCompatibilityMetadata( string serverVersion, string libraryStatus )
	{
		if ( !string.IsNullOrWhiteSpace( serverVersion ) ) ServerVersion = serverVersion.Trim();
		if ( libraryStatus is not null ) ObserveLibraryStatus( libraryStatus.Trim().ToLowerInvariant() );
	}

	/// <summary>
	/// Reads the <c>{ ok:false, error:{ code, message } }</c> body of a 426 response. Only
	/// LIBRARY_UNSUPPORTED and LIBRARY_BLOCKED are compatibility errors; anything else returns false.
	/// </summary>
	public static bool TryReadCompatibilityError( string raw, out string code, out string message )
	{
		code = null;
		message = null;
		try
		{
			using var document = JsonDocument.Parse( raw ?? "" );
			if ( !document.RootElement.TryGetProperty( "error", out var error ) || error.ValueKind != JsonValueKind.Object ) return false;
			var found = error.TryGetProperty( "code", out var c ) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
			if ( found is not ("LIBRARY_UNSUPPORTED" or "LIBRARY_BLOCKED") ) return false;
			code = found;
			message = error.TryGetProperty( "message", out var m ) && m.ValueKind == JsonValueKind.String ? m.GetString() : "";
			return true;
		}
		catch ( JsonException )
		{
			return false;
		}
	}

	private static void ObserveLibraryStatus( string status )
	{
		if ( status is not ("supported" or "outdated" or "deprecated" or "unsupported" or "blocked" or "unknown") ) return;
		if ( status == LibraryStatus ) return;
		LibraryStatus = status;
		if ( _loggedLibraryStatuses.Add( status ) )
			NetLog.Info( "compat", $"Library {LibraryVersion}: {status}; server {ServerVersion ?? "unknown"}." );
		InvokeSafely( OnLibraryStatusChanged, status, nameof( OnLibraryStatusChanged ) );
	}

	/// <summary>
	/// Compatibility wrapper for callers that need a body or an exception. A reply that is not 2xx throws an
	/// <see cref="HttpRequestException"/> with the status. New code uses <see cref="SendRawAsync"/> instead.
	/// </summary>
	internal static async Task<string> RequestStringAsync( string url, string method = "GET", Func<HttpContent> createContent = null,
		Dictionary<string, string> headers = null )
	{
		var reply = await SendRawContentAsync( url, method, createContent, headers );
		if ( reply.TransportError is not null ) throw new HttpRequestException( reply.TransportError );
		if ( reply.Ok || IsCompatibilityReply( reply ) ) return reply.Body;
		throw new HttpRequestException( $"Response status code does not indicate success: {reply.Status} ({(HttpStatusCode)reply.Status}).", null, (HttpStatusCode)reply.Status );
	}

	/// <summary>True for a 426 whose body is a library compatibility error (LIBRARY_UNSUPPORTED or LIBRARY_BLOCKED).</summary>
	private static bool IsCompatibilityReply( RawReply reply ) => reply.Status == 426 && TryReadCompatibilityError( reply.Body, out _, out _ );

	/// <summary>
	/// Sends a request and retries transient failures: rate limits (429), and 503 with Retry-After for reads. Content is
	/// created for each attempt because the engine disposes request content after it is sent. Every HTTP status comes back
	/// as a <see cref="RawReply"/>; only a failure to get any response is reported as a transport error.
	/// </summary>
	internal static async Task<RawReply> SendWithRetriesAsync( string method, Func<HttpContent> createContent,
		Func<HttpContent, Task<HttpResponseMessage>> send )
	{
		var waited = 0.0;
		for ( var retries = 0; ; retries++ )
		{
			var generation = _compatibilityGeneration;
			RawReply reply;
			try
			{
				using var content = createContent?.Invoke();
				var sentAt = DateTimeOffset.UtcNow;
				using var response = await send( content );
				if ( generation == _compatibilityGeneration ) ObserveServerDateHeader( response, sentAt, DateTimeOffset.UtcNow );
				if ( generation == _compatibilityGeneration ) ObserveCompatibilityHeaders( response );
				var body = await response.Content.ReadAsStringAsync();
				reply = new RawReply( (int)response.StatusCode, body, null, RetryAfterHeader( response ) );
			}
			catch ( Exception ex )
			{
				return new RawReply( 0, null, ex.Message, null );
			}

			// A 426 carries the server's remediation message in a structured body. The caller records it as an error.
			if ( reply.Status == 426 && TryReadCompatibilityError( reply.Body, out var code, out var message ) )
			{
				ObserveLibraryStatus( code == "LIBRARY_BLOCKED" ? "blocked" : "unsupported" );
				Log.Warning( $"[NetworkStorage] {code}: {message}" );
				return reply;
			}

			// Transient failures retry a bounded number of times; the total wait never exceeds MaxRateLimitWaitSeconds.
			var transient = IsRetryableRateLimit( method, reply.Status, reply.Body ) || IsRetryableUnavailable( method, reply.Status, reply.RetryAfter );
			if ( retries < MaxRateLimitRetries && transient )
			{
				var delay = RateLimitDelaySeconds( reply.RetryAfter, reply.Body );
				if ( waited + delay <= MaxRateLimitWaitSeconds )
				{
					waited += delay;
					Log.Info( $"[NetworkStorage] {method} HTTP {reply.Status} transient; retry {retries + 1}/{MaxRateLimitRetries} in {delay:0.##}s." );
					await Task.Delay( (int)Math.Ceiling( delay * 1000 ) );
					continue;
				}
			}

			return reply;
		}
	}

	private static async Task<bool> RequireCapabilityAsync( string capability, string operation )
	{
		await _serverInfoTask;
		if ( HasCapability( capability ) ) return true;
		RecordEndpointError( operation, "CAPABILITY_UNAVAILABLE",
			$"Server {ServerVersion ?? "unknown"} does not advertise '{capability}'. Update the server to use this feature." );
		return false;
	}
}
