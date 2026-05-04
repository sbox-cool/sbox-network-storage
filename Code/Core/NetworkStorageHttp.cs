using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	public static void EnsureConfigured()
	{
		if ( !IsConfigured )
			AutoConfigure();

		if ( !IsConfigured )
			throw new InvalidOperationException( "NetworkStorage not configured. Add credentials via Editor → Network Storage → Setup, or call NetworkStorage.Configure() manually." );
	}

	private static bool IsCdnRoot( string root )
		=> root.Contains( "storage.sbox.cool" ) || root.Contains( "storage.sboxcool.com" );

	/// <summary>
	/// Build the request URL with API key query param (no auth tokens in URL).
	/// </summary>
	private static string BuildUrl( string path, bool includeDedicatedSecretFlag = false )
	{
		var baseUrl = $"{ApiRoot}{path}?apiKey={Uri.EscapeDataString( ApiKey )}";
		if ( includeDedicatedSecretFlag )
			baseUrl += "&secret-key=1";
		if ( IsCdnRoot( ApiRoot ) )
		{
			var v = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			return $"{baseUrl}&v={v}";
		}
		return baseUrl;
	}

	/// <summary>
	/// Build auth headers containing the Steam ID and auth token.
	/// Sending auth via headers avoids URL-encoding issues that corrupt
	/// base64 tokens in query strings (+ decoded as space, etc.).
	/// </summary>
	private static Dictionary<string, string> BuildPublicHeaders( string clientMode = null )
	{
		var headers = new Dictionary<string, string>
		{
			{ "x-public-key", ApiKey ?? "" }
		};
		if ( !string.IsNullOrWhiteSpace( clientMode ) )
			headers["x-ns-security-mode"] = clientMode;
		if ( !string.IsNullOrWhiteSpace( RuntimeSecurityConfigVersion ) )
			headers["x-ns-security-config-version"] = RuntimeSecurityConfigVersion;

		var revisionId = NetworkStoragePackageInfo.CurrentRevisionId;
		if ( revisionId.HasValue )
			headers["x-ns-revision-id"] = revisionId.Value.ToString();
		headers["x-ns-client-type"] = GetClientType();
		return headers;
	}

	private static async Task<Dictionary<string, string>> BuildAuthHeaders( string authSessionToken = null, string clientMode = null )
	{
		if ( IsDedicatedServerProcess )
		{
			LogDedicatedPlayerAuthSuppressedOnce();
			return BuildPublicHeaders( clientMode );
		}

		var steamId = Game.SteamId.ToString();
		var token = TryTakePreparedAuthToken() ?? await GetAuthTokenWithRetry( $"steamId={steamId}" );

		if ( string.IsNullOrEmpty( token ) )
		{
			if ( NetworkStorageLogConfig.LogTokens )
				Log.Warning( $"[NetworkStorage] Auth token is empty for steamId={steamId} — requests may fail" );
		}
		else if ( NetworkStorageLogConfig.LogTokens )
		{
			var preview = token.Length > 8 ? $"{token[..4]}...{token[^4..]}" : "****";
			Log.Info( $"[NetworkStorage] Auth token acquired for steamId={steamId} ({token.Length} chars, preview={preview})" );
		}

		var headers = new Dictionary<string, string>
		{
			{ "x-public-key", ApiKey ?? "" },
			{ "x-steam-id", steamId },
			{ "x-sbox-token", token ?? "" }
		};
		if ( !string.IsNullOrWhiteSpace( authSessionToken ) )
			headers["x-auth-session"] = authSessionToken;
		if ( !string.IsNullOrWhiteSpace( clientMode ) )
			headers["x-ns-security-mode"] = clientMode;
		if ( !string.IsNullOrWhiteSpace( RuntimeSecurityConfigVersion ) )
			headers["x-ns-security-config-version"] = RuntimeSecurityConfigVersion;

		var revisionId = NetworkStoragePackageInfo.CurrentRevisionId;
		if ( revisionId.HasValue )
			headers["x-ns-revision-id"] = revisionId.Value.ToString();

		var clientType = GetClientType();
		if ( !string.IsNullOrEmpty( clientType ) )
			headers["x-ns-client-type"] = clientType;

		return headers;
	}

	/// <summary>
	/// Determines the client type based on the current runtime context.
	/// Returns "editor" in the s&amp;box editor, "dedicated" on dedicated servers, or "game" otherwise.
	/// </summary>
	private static string GetClientType()
	{
		if ( IsDedicatedServerProcess )
			return "dedicated";
		if ( Game.IsEditor )
			return "editor";
		return "game";
	}

}
