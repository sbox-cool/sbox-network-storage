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
		var statusCode = 0;

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

		if ( json.TryGetProperty( "status", out var statusProp ) && statusProp.ValueKind == JsonValueKind.Number )
			statusProp.TryGetInt32( out statusCode );

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
			var logMessage = $"{code}: {message}";
			if ( IsExpectedStorageNotFound( slug, code, statusCode, message ) )
			{
				Log.Info( $"[NetworkStorage] {slug}: {logMessage}" );
				NetLog.Info( slug, logMessage );
			}
			else
			{
				Log.Warning( $"[NetworkStorage] {slug}: {code} — {message}" );
				NetLog.Error( slug, logMessage );
			}
		}
		RecordEndpointError( slug, code, message );
	}

	private static bool IsExpectedStorageNotFound( string slug, string code, int statusCode, string message )
	{
		if ( !string.Equals( slug, "storage", StringComparison.OrdinalIgnoreCase ) )
			return false;

		if ( statusCode == 404 )
			return true;

		if ( string.Equals( code, "NOT_FOUND", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( code, "PROFILE_MISSING", StringComparison.OrdinalIgnoreCase ) )
			return true;

		return !string.IsNullOrWhiteSpace( message )
			&& message.Contains( "404", StringComparison.OrdinalIgnoreCase )
			&& message.Contains( "Not Found", StringComparison.OrdinalIgnoreCase );
	}

	private static bool IsHttpNotFoundException( Exception ex )
	{
		if ( ex is System.Net.Http.HttpRequestException httpEx && httpEx.StatusCode.HasValue && (int)httpEx.StatusCode.Value == 404 )
			return true;

		var message = ex?.Message ?? "";
		return message.Contains( "404", StringComparison.OrdinalIgnoreCase )
			&& message.Contains( "Not Found", StringComparison.OrdinalIgnoreCase );
	}

	private static void RecordStorageNotFound( string details )
	{
		var message = string.IsNullOrWhiteSpace( details ) ? "404 Not Found" : $"404 Not Found: {details}";
		if ( NetworkStorageLogConfig.LogErrors )
		{
			Log.Info( $"[NetworkStorage] storage: {message}" );
			NetLog.Info( "storage", message );
		}
		RecordEndpointError( "storage", "NOT_FOUND", message );
	}
}
