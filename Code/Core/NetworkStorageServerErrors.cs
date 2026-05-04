using System;
using System.Text.Json;

namespace Sandbox;

public static partial class NetworkStorage
{
	private static void LogServerError( string slug, JsonElement json )
	{
		var code = "UNKNOWN";
		var message = "";
		var authFailureKind = "";

		if ( json.TryGetProperty( "error", out var err ) )
		{
			if ( err.ValueKind == JsonValueKind.Object )
			{
				code = err.TryGetProperty( "code", out var c ) ? c.GetString() ?? "UNKNOWN" : "UNKNOWN";
				message = err.TryGetProperty( "message", out var m ) ? m.GetString() ?? "" : "";
				authFailureKind = err.TryGetProperty( "authFailureKind", out var authKind )
					? authKind.GetString() ?? ""
					: "";
			}
			else if ( err.ValueKind == JsonValueKind.String )
			{
				code = err.GetString() ?? "UNKNOWN";
			}
		}

		// Top-level message (server copies error.message here for convenience)
		if ( string.IsNullOrEmpty( message ) && json.TryGetProperty( "message", out var topMsg ) )
			message = topMsg.GetString() ?? "";

		if ( string.Equals( code, "SBOX_AUTH_FAILED", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( authFailureKind, "token_rejected", StringComparison.OrdinalIgnoreCase ) )
		{
			InvalidateCachedAuthToken( $"{slug} {code} {authFailureKind}".Trim() );
		}

		if ( IsSecurityConfigMismatchCode( code ) )
		{
			JsonElement expected = default;
			string expectedConfigVersion = null;
			if ( err.ValueKind == JsonValueKind.Object )
			{
				if ( err.TryGetProperty( "security", out var secObj ) )
				{
					if ( secObj.TryGetProperty( "configVersion", out var cv ) )
						expectedConfigVersion = cv.GetString();
					secObj.TryGetProperty( "expected", out expected );
				}
				if ( expected.ValueKind != JsonValueKind.Object )
					err.TryGetProperty( "expected", out expected );
			}
			if ( expected.ValueKind != JsonValueKind.Object && json.TryGetProperty( "security", out var topSec ) )
			{
				if ( expectedConfigVersion == null && topSec.TryGetProperty( "configVersion", out var cv ) )
					expectedConfigVersion = cv.GetString();
				topSec.TryGetProperty( "expected", out expected );
			}
			ApplyServerExpectedSecurityConfig( code, expected, expectedConfigVersion );
		}

		if ( NetworkStorageLogConfig.LogErrors )
		{
			Log.Warning( $"[NetworkStorage] {slug}: {code} — {message}" );
			NetLog.Error( slug, $"{code}: {message}" );
		}
		RecordEndpointError( slug, code, message );
	}
}
