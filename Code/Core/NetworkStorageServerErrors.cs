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
				code = ReadStringProperty( err, "code" ) ?? "UNKNOWN";
				message = ReadStringProperty( err, "message" ) ?? "";
				authFailureKind = ReadStringProperty( err, "authFailureKind" ) ?? "";
			}
			else if ( err.ValueKind == JsonValueKind.String )
			{
				// A bare string is the error code when it has code shape. Otherwise it is the message, under REQUEST_FAILED.
				var text = err.GetString() ?? "";
				if ( IsErrorCodeToken( text ) )
				{
					code = text;
				}
				else
				{
					code = "REQUEST_FAILED";
					message = text;
				}
			}
		}

		// Top-level message, then detail (the server copies error.message here for convenience)
		if ( string.IsNullOrEmpty( message ) )
			message = ReadStringProperty( json, "message" ) ?? "";
		if ( string.IsNullOrEmpty( message ) )
			message = ReadStringProperty( json, "detail" ) ?? "";

		if ( json.TryGetProperty( "status", out var statusProp ) && statusProp.ValueKind == JsonValueKind.Number )
			statusProp.TryGetInt32( out statusCode );

		if ( string.Equals( code, "SBOX_AUTH_FAILED", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( authFailureKind, "token_rejected", StringComparison.OrdinalIgnoreCase ) )
		{
			InvalidateCachedAuthToken( $"{slug} {code} {authFailureKind}".Trim() );
		}

		if ( IsSecurityConfigMismatchCode( code ) )
		{
			var expected = default(JsonElement);
			string expectedConfigVersion = null;
			if ( err.ValueKind == JsonValueKind.Object && err.TryGetProperty( "security", out var secObj ) && secObj.ValueKind == JsonValueKind.Object )
			{
				expectedConfigVersion = ReadStringProperty( secObj, "configVersion" );
				if ( secObj.TryGetProperty( "expected", out var secExpected ) )
					expected = secExpected;
			}
			if ( expected.ValueKind != JsonValueKind.Object && err.ValueKind == JsonValueKind.Object && err.TryGetProperty( "expected", out var errExpected ) )
				expected = errExpected;
			if ( expected.ValueKind != JsonValueKind.Object && json.TryGetProperty( "security", out var topSec ) && topSec.ValueKind == JsonValueKind.Object )
			{
				expectedConfigVersion ??= ReadStringProperty( topSec, "configVersion" );
				if ( topSec.TryGetProperty( "expected", out var topExpected ) )
					expected = topExpected;
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
		// Only an out-of-window encrypted request carries the server clock; learn it so the next id is in the window.
		var serverTime = code == "ENCRYPTED_REQUEST_STALE" ? ReadServerTimeSeconds( json ) : 0;
		ObserveServerUnixSeconds( serverTime, DateTimeOffset.UtcNow );
		RecordEndpointError( slug, code, message, serverTime );
	}

	// Reads a string property. A missing property, a non-string value, or a non-object reads as null.
	private static string ReadStringProperty( JsonElement obj, string property )
		=> obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

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
