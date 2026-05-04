using System;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Network Storage client for sboxcool.com.
///
/// Auto-configures from network-storage.credentials.json on first use.
/// You can also call Configure() manually to override.
/// </summary>
public static partial class NetworkStorage
{
	/// <summary>Base API URL (no trailing slash).</summary>
	public static string BaseUrl { get; private set; } = "https://api.sboxcool.com";

	/// <summary>API version prefix.</summary>
	public static string ApiVersion { get; private set; } = "v3";

	/// <summary>Your project ID from the sboxcool.com dashboard.</summary>
	public static string ProjectId { get; private set; }

	/// <summary>Your public API key (sbox_ns_ prefix).</summary>
	public static string ApiKey { get; private set; }

	/// <summary>True after Configure() or auto-config has loaded valid credentials.</summary>
	public static bool IsConfigured => !string.IsNullOrEmpty( ProjectId ) && !string.IsNullOrEmpty( ApiKey );

	/// <summary>Optional CDN URL for storage reads.</summary>
	public static string CdnUrl { get; private set; }

	/// <summary>The full versioned API root, e.g. https://api.sboxcool.com/v3.</summary>
	public static string ApiRoot => $"{BaseUrl}/{ApiVersion}";

	/// <summary>Whether non-host clients route API calls through the game host.</summary>
	public static bool ProxyEnabled { get; set; } = true;

	/// <summary>Project flag for fast auth sessions.</summary>
	public static bool EnableAuthSessions { get; private set; }

	/// <summary>Project flag for encrypted Network Storage requests.</summary>
	public static bool EnableEncryptedRequests { get; private set; }

	/// <summary>Delegate for proxying endpoint calls through the game host.</summary>
	public static Func<string, string, string, string, Task<string>> RequestProxy { get; set; }

	/// <summary>Delegate for proxying document reads through the game host.</summary>
	public static Func<string, string, string, string, Task<string>> DocumentProxy { get; set; }

	/// <summary>True if this client is the network host or single-player.</summary>
	public static bool IsHost => !Networking.IsActive || Networking.IsHost;

	/// <summary>Configure the client manually. Call once at game startup.</summary>
	public static void Configure( string projectId, string apiKey, string baseUrl = null, string apiVersion = null, string cdnUrl = null )
	{
		ProjectId = projectId;
		ApiKey = apiKey;
		if ( !string.IsNullOrEmpty( baseUrl ) ) BaseUrl = baseUrl.TrimEnd( '/' );
		if ( !string.IsNullOrEmpty( apiVersion ) ) ApiVersion = apiVersion.Trim( '/' );
		CdnUrl = string.IsNullOrEmpty( cdnUrl ) ? null : cdnUrl.TrimEnd( '/' );
		_autoConfigAttempted = true;
		NetworkStorageRevisionHandler.Initialize();
		if ( NetworkStorageLogConfig.LogConfig )
			NetLog.Info( "config", $"NetworkStorage ready — {ApiRoot}" );
	}

	/// <summary>Reset the auto-configure guard so AutoConfigure() can retry.</summary>
	public static void ResetAutoConfigureFlag()
	{
		_autoConfigAttempted = false;
	}
}
