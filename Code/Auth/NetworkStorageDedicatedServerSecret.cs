using System;
using System.Collections.Generic;

namespace Sandbox;

public static partial class NetworkStorage
{
	private const string DedicatedSecretLaunchKey = "network_storage_secret_key";

	private static readonly string[] DedicatedSecretLaunchKeys =
	{
		DedicatedSecretLaunchKey,
		"network-storage-secret-key",
		"sboxcool_secret_key",
		"networkStorageSecretKey",
		"sboxcoolSecretKey",
		"nsSecretKey",
		"ns_secret_key"
	};

	[ConVar( "network-storage-secret-key", ConVarFlags.Hidden )]
	public static string NetworkStorageSecretKeyDashedConVar { get; set; } = "";

	[ConVar( "network_storage_secret_key", ConVarFlags.Hidden )]
	public static string NetworkStorageSecretKeyConVar { get; set; } = "";

	[ConVar( "sboxcool_secret_key", ConVarFlags.Hidden )]
	public static string SboxCoolSecretKeyConVar { get; set; } = "";

	[ConVar( "networkStorageSecretKey", ConVarFlags.Hidden )]
	public static string NetworkStorageSecretKeyCamelConVar { get; set; } = "";

	[ConVar( "sboxcoolSecretKey", ConVarFlags.Hidden )]
	public static string SboxCoolSecretKeyCamelConVar { get; set; } = "";

	[ConVar( "nsSecretKey", ConVarFlags.Hidden )]
	public static string NsSecretKeyCamelConVar { get; set; } = "";

	[ConVar( "ns_secret_key", ConVarFlags.Hidden )]
	public static string NsSecretKeyConVar { get; set; } = "";

	private static bool _dedicatedSecretLookupAttempted;
	private static string _dedicatedServerSecretKey;
	private static string _dedicatedServerSecretKeySource;
	private static bool _dedicatedServerSecretKeyRejected;
	private static bool _dedicatedPlayerAuthSuppressedLogged;
	private static bool _dedicatedSecretMissingLogged;

	/// <summary>
	/// True when this process is a dedicated server and a runtime endpoint secret key was supplied.
	/// The key is never read on clients or listen servers.
	/// </summary>
	public static bool HasDedicatedServerSecretKey => TryGetDedicatedServerSecretKey( null, out _ );

	/// <summary>Where the dedicated endpoint secret key was loaded from, without exposing the key.</summary>
	public static string DedicatedServerSecretKeySource
	{
		get
		{
			TryGetDedicatedServerSecretKey( null, out _ );
			return _dedicatedServerSecretKeySource ?? "none";
		}
	}

	/// <summary>
	/// Manually provide a runtime endpoint secret key. This is accepted only on dedicated servers.
	/// Prefer the dedicated launch flag: +network_storage_secret_key sbox_sk_...
	/// </summary>
	public static void ConfigureDedicatedServerSecretKey( string secretKey )
	{
		if ( !CanUseDedicatedServerSecretKey() )
		{
			if ( !string.IsNullOrWhiteSpace( secretKey ) && NetworkStorageLogConfig.LogConfig )
				Log.Warning( "[NetworkStorage] Ignoring dedicated endpoint secret key because this process is not a dedicated server host." );
			return;
		}

		_dedicatedSecretLookupAttempted = true;
		_dedicatedServerSecretKey = NormalizeSecretKey( secretKey );
		_dedicatedServerSecretKeySource = string.IsNullOrEmpty( _dedicatedServerSecretKey ) ? "none" : "manual";
		_dedicatedServerSecretKeyRejected = false;
	}

	private static bool TryAddDedicatedServerSecretHeaders( Dictionary<string, string> headers, EndpointReference endpoint )
	{
		if ( !TryPrepareDedicatedServerSecret( headers, endpoint, out var secretKey ) )
			return false;

		headers["x-secret-key"] = secretKey;
		headers["x-public-key"] = ApiKey ?? "";
		TryAddDedicatedSteamIdHeader( headers );
		LogDedicatedSecretTransportOnce( "x-secret-key" );
		return true;
	}

	private static bool ShouldUseDedicatedServerSecret( EndpointReference endpoint = null )
	{
		if ( !TryGetDedicatedServerSecretKey( endpoint, out _ ) )
			return false;
		if ( CanTransportDedicatedServerSecretSecurely() )
			return true;

		LogInsecureSecretTransportOnce();
		return false;
	}

	private static bool TryBuildDedicatedStorageHeaders( out Dictionary<string, string> headers )
	{
		headers = null;
		if ( !TryGetDedicatedServerSecretKey( null, out var secretKey ) )
			return false;
		if ( !CanTransportDedicatedServerSecretSecurely() )
		{
			LogInsecureSecretTransportOnce();
			return false;
		}

		headers = BuildPublicHeaders();
		headers["x-api-key"] = secretKey;
		headers["x-public-key"] = ApiKey ?? "";
		LogDedicatedSecretTransportOnce( "x-api-key" );
		return true;
	}

	private static void TryAddDedicatedSteamIdHeader( Dictionary<string, string> headers )
	{
		if ( headers is null ) return;

		var steamId = Game.SteamId.ToString();
		if ( !string.IsNullOrWhiteSpace( steamId ) && steamId != "0" )
			headers["x-steam-id"] = steamId;
	}

	private static bool TryPrepareDedicatedServerSecret( Dictionary<string, string> headers, EndpointReference endpoint, out string secretKey )
	{
		secretKey = null;
		if ( headers is null || !TryGetDedicatedServerSecretKey( endpoint, out secretKey ) )
			return false;

		if ( CanTransportDedicatedServerSecretSecurely() )
			return true;

		LogInsecureSecretTransportOnce();
		secretKey = null;
		return false;
	}

	private static bool TryGetDedicatedServerSecretKey( EndpointReference endpoint, out string secretKey )
	{
		secretKey = null;

		if ( !CanUseDedicatedServerSecretKey() )
		{
			LogIgnoredEndpointUrlSecretOnce( endpoint );
			return false;
		}

		LoadDedicatedServerSecretKey();
		if ( _dedicatedServerSecretKeyRejected )
			return false;

		secretKey = _dedicatedServerSecretKey;
		return !string.IsNullOrWhiteSpace( secretKey );
	}

	private static bool CanUseDedicatedServerSecretKey()
		=> IsDedicatedServerHost;

	private static bool IsDedicatedServerProcess => Application.IsDedicatedServer;

	private static bool IsDedicatedServerHost => IsDedicatedServerProcess && IsHost;

	private static string DedicatedServerAuthUnavailableMessage()
	{
		var suffix = HasDedicatedServerSecretKey
			? " Secret keys are only sent to HTTPS or loopback API roots."
			: $" Start the server with one of: {SupportedDedicatedSecretLaunchFlagsText}.";
		return $"Dedicated servers cannot request s&box player auth tokens.{suffix}";
	}

	private static string SupportedDedicatedSecretLaunchFlagsText => "+network_storage_secret_key sbox_sk_..., +network-storage-secret-key sbox_sk_..., +sboxcool_secret_key sbox_sk_..., +networkStorageSecretKey sbox_sk_..., +sboxcoolSecretKey sbox_sk_..., +nsSecretKey sbox_sk_..., +ns_secret_key sbox_sk_...";

	private static void LogDedicatedSecretMissingOnce()
	{
		if ( _dedicatedSecretMissingLogged ) return;

		_dedicatedSecretMissingLogged = true;
		Log.Warning( $"[NetworkStorage] Dedicated server secret key not found. Supported launch flags: {SupportedDedicatedSecretLaunchFlagsText}" );
	}

	private static bool TryRejectDedicatedServerPlayerAuth( string tag )
	{
		if ( !IsDedicatedServerProcess )
			return false;

		LogDedicatedPlayerAuthSuppressedOnce();
		RecordEndpointError( tag, "DEDICATED_SECRET_REQUIRED", DedicatedServerAuthUnavailableMessage() );
		return true;
	}

	private static void LogDedicatedPlayerAuthSuppressedOnce()
	{
		if ( _dedicatedPlayerAuthSuppressedLogged || !NetworkStorageLogConfig.LogTokens ) return;

		_dedicatedPlayerAuthSuppressedLogged = true;
		Log.Warning( $"[NetworkStorage] {DedicatedServerAuthUnavailableMessage()}" );
	}

	private static bool CanTransportDedicatedServerSecretSecurely()
	{
		return Uri.TryCreate( ApiRoot, UriKind.Absolute, out var uri ) &&
			(string.Equals( uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase ) || uri.IsLoopback);
	}

}
