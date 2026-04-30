using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	private static DateTimeOffset _lastAuthTokenLookupFailedAt;
	private static readonly TimeSpan FailedAuthLookupCooldown = TimeSpan.FromSeconds( 5 );
	private static readonly TimeSpan PreparedAuthTokenLifetime = TimeSpan.FromSeconds( 10 );
	private static readonly object _preparedAuthTokensLock = new();
	private static readonly Queue<PreparedAuthTokenEntry> _preparedAuthTokens = new();

	private sealed class PreparedAuthTokenEntry
	{
		public string Token { get; init; }
		public DateTimeOffset CreatedAt { get; init; }
	}

	private static void InvalidateCachedAuthToken( string reason )
	{
		if ( NetworkStorageLogConfig.LogTokens )
			Log.Warning( $"[NetworkStorage] Clearing cached auth token: {reason}" );
		lock ( _preparedAuthTokensLock )
			_preparedAuthTokens.Clear();
	}

	public static async Task<bool> EnsureEndpointAuthAsync( string slug )
	{
		var endpoint = ResolveEndpointReference( slug );
		ApplyEndpointReferenceConfiguration( endpoint );
		slug = endpoint.Slug;

		EnsureConfigured();
		ClearLastEndpointError( slug );

		// Dedicated-server secret keys are trusted server/backend auth. Dedicated
		// servers do not have player s&box tokens, so never fall back to token auth.
		if ( ShouldUseDedicatedServerSecret( endpoint ) )
			return true;
		if ( TryRejectDedicatedServerPlayerAuth( slug ) )
			return false;

		var steamId = Game.SteamId.ToString();
		var token = await GetAuthTokenWithRetry( $"steamId={steamId}" );
		if ( !string.IsNullOrWhiteSpace( token ) )
		{
			RememberPreparedAuthToken( token );
			return true;
		}

		RecordEndpointError(
			slug,
			"SBOX_AUTH_FAILED",
			$"Missing token or steamId. token=NO steamId={steamId} This endpoint requires a valid s&box player token." );
		return false;
	}

	private static void RememberPreparedAuthToken( string token )
	{
		if ( string.IsNullOrWhiteSpace( token ) )
			return;

		lock ( _preparedAuthTokensLock )
		{
			_preparedAuthTokens.Enqueue( new PreparedAuthTokenEntry
			{
				Token = token,
				CreatedAt = DateTimeOffset.UtcNow
			} );
		}
	}

	private static string TryTakePreparedAuthToken()
	{
		lock ( _preparedAuthTokensLock )
		{
			while ( _preparedAuthTokens.Count > 0 )
			{
				var next = _preparedAuthTokens.Dequeue();
				if ( next is not null
					&& !string.IsNullOrWhiteSpace( next.Token )
					&& DateTimeOffset.UtcNow - next.CreatedAt < PreparedAuthTokenLifetime )
				{
					return next.Token;
				}
			}
		}

		return null;
	}

	/// <summary>
	/// Auth tokens can lag briefly behind startup, especially in editor flows.
	/// Retry a few times before treating the request as unauthenticated.
	/// </summary>
	private static async Task<string> GetAuthTokenWithRetry( string context, int attempts = 6, int delayMs = 500 )
	{
		if ( IsDedicatedServerProcess )
		{
			LogDedicatedPlayerAuthSuppressedOnce();
			return null;
		}

		if ( _lastAuthTokenLookupFailedAt != default
			&& DateTimeOffset.UtcNow - _lastAuthTokenLookupFailedAt < FailedAuthLookupCooldown )
		{
			return null;
		}

		string lastError = null;

		for ( int attempt = 1; attempt <= attempts; attempt++ )
		{
			try
			{
				var token = await Services.Auth.GetToken( "sbox-network-storage" );
				if ( !string.IsNullOrWhiteSpace( token ) )
				{
					_lastAuthTokenLookupFailedAt = default;
					if ( attempt > 1 && NetworkStorageLogConfig.LogTokens )
						Log.Info( $"[NetworkStorage] Auth token acquired for {context} after retry {attempt}/{attempts}" );
					return token;
				}
			}
			catch ( Exception ex )
			{
				lastError = ex.Message;
			}

			if ( attempt < attempts )
				await Task.Delay( delayMs );
		}

		if ( NetworkStorageLogConfig.LogTokens )
		{
			if ( !string.IsNullOrEmpty( lastError ) )
				Log.Warning( $"[NetworkStorage] Failed to get auth token for {context} after {attempts} attempts: {lastError}" );
			else
				Log.Warning( $"[NetworkStorage] Auth token remained empty for {context} after {attempts} attempts" );
		}

		_lastAuthTokenLookupFailedAt = DateTimeOffset.UtcNow;
		return null;
	}
}
