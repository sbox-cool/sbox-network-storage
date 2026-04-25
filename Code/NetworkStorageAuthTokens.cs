using System;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	private sealed class PreparedAuthTokenEntry
	{
		public string Token { get; init; }
		public DateTimeOffset CreatedAt { get; init; }
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
