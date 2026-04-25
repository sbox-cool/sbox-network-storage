namespace Sandbox;

/// <summary>
/// Controls which Network Storage log categories are printed to the console.
/// All categories default to enabled. Call DisableAll() then enable specific ones,
/// or EnableAll() then disable specific ones.
/// </summary>
public static class NetworkStorageLogConfig
{
	/// <summary>Log outgoing request details (method, URL, body).</summary>
	public static bool LogRequests { get; set; } = true;

	/// <summary>Log response bodies and status.</summary>
	public static bool LogResponses { get; set; } = true;

	/// <summary>Log auth token acquisition and status.</summary>
	public static bool LogTokens { get; set; } = true;

	/// <summary>Log proxy RPC traffic (host/client handoff).</summary>
	public static bool LogProxy { get; set; } = true;

	/// <summary>Log errors and warnings (failures, HTTP errors).</summary>
	public static bool LogErrors { get; set; } = true;

	/// <summary>Log configuration and startup messages.</summary>
	public static bool LogConfig { get; set; } = true;

	/// <summary>Enable all log categories.</summary>
	public static void EnableAll()
	{
		LogRequests = true;
		LogResponses = true;
		LogTokens = true;
		LogProxy = true;
		LogErrors = true;
		LogConfig = true;
	}

	/// <summary>Disable all log categories.</summary>
	public static void DisableAll()
	{
		LogRequests = false;
		LogResponses = false;
		LogTokens = false;
		LogProxy = false;
		LogErrors = false;
		LogConfig = false;
	}

	internal static void Info( string message )
	{
		Log.Info( message );
	}

	internal static void Warning( string message )
	{
		Log.Warning( message );
	}
}
