using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Sandbox;

public static partial class NetworkStorage
{
	private static readonly Dictionary<string, EndpointErrorInfo> _lastEndpointErrors = new();

	public static bool TryGetLastEndpointError( string slug, out string code, out string message )
	{
		if ( _lastEndpointErrors.TryGetValue( slug, out var error ) )
		{
			code = error.Code;
			message = error.Message;
			return true;
		}

		code = null;
		message = null;
		return false;
	}

	/// <summary>
	/// Parse a server response. Returns the response data on success, null on any error.
	/// Detects errors via: ok=false, error object, or non-JSON responses.
	/// </summary>
	private static JsonElement? ParseResponse( string slug, string raw )
	{
		if ( string.IsNullOrEmpty( raw ) )
		{
			if ( NetworkStorageLogConfig.LogErrors )
				NetLog.Error( slug, "Server returned empty response" );
			RecordEndpointError( slug, "EMPTY_RESPONSE", "Server returned empty response" );
			return null;
		}

		// Catch HTML error pages or non-JSON responses early
		var trimmed = raw.TrimStart();
		if ( trimmed.Length > 0 && trimmed[0] != '{' && trimmed[0] != '[' )
		{
			if ( NetworkStorageLogConfig.LogErrors )
				NetLog.Error( slug, $"Non-JSON response: {raw[..Math.Min( raw.Length, 120 )]}" );
			RecordEndpointError( slug, "INVALID_RESPONSE", "Server returned non-JSON response" );
			return null;
		}

		JsonElement json;
		try
		{
			json = JsonSerializer.Deserialize<JsonElement>( raw );
		}
		catch
		{
			if ( NetworkStorageLogConfig.LogErrors )
				NetLog.Error( slug, $"Invalid JSON: {raw[..Math.Min( raw.Length, 200 )]}" );
			RecordEndpointError( slug, "INVALID_JSON", "Server returned invalid JSON" );
			return null;
		}

		// ── Error detection (multiple patterns for robustness) ──

		// 1) Explicit ok: false
		if ( json.TryGetProperty( "ok", out var ok ) && ok.ValueKind == JsonValueKind.False )
		{
			LogServerError( slug, json );
			return null;
		}

		// 2) Error object without ok field (legacy / edge cases)
		if ( json.TryGetProperty( "error", out var errProp ) )
		{
			// { error: { code, message } } — structured error
			if ( errProp.ValueKind == JsonValueKind.Object )
			{
				// Only treat as error if there's no ok:true (some responses include error metadata alongside success)
				if ( !json.TryGetProperty( "ok", out var okCheck ) || okCheck.ValueKind != JsonValueKind.True )
				{
					LogServerError( slug, json );
					return null;
				}
			}
			// { error: "string message" } — simple error
			else if ( errProp.ValueKind == JsonValueKind.String )
			{
				if ( NetworkStorageLogConfig.LogErrors )
				{
					Log.Warning( $"[NetworkStorage] {slug}: {errProp.GetString()}" );
					NetLog.Error( slug, errProp.GetString() );
				}
				RecordEndpointError( slug, errProp.GetString() ?? "UNKNOWN", "" );
				return null;
			}
		}

		// 3) HTTP error status forwarded as { status: 4xx/5xx }
		if ( json.TryGetProperty( "status", out var status ) && status.ValueKind == JsonValueKind.Number )
		{
			var statusCode = status.GetInt32();
			if ( statusCode >= 400 )
			{
				LogServerError( slug, json );
				return null;
			}
		}

		// ── Success — extract response body ──

		// Server wraps endpoint responses in { ok, body, timing } — unwrap body if present
		if ( json.TryGetProperty( "body", out var body ) && body.ValueKind == JsonValueKind.Object )
			return body;

		return json;
	}

	/// <summary>
	/// Extract and log error details from a server error response.
	/// Handles: { error: { code, message } }, { error: "msg" }, { message: "msg" }
	/// </summary>
	private static void ClearLastEndpointError( string slug )
	{
		if ( !string.IsNullOrEmpty( slug ) )
			_lastEndpointErrors.Remove( slug );
	}

	private static void RecordEndpointError( string slug, string code, string message )
	{
		if ( string.IsNullOrEmpty( slug ) )
			return;

		_lastEndpointErrors[slug] = new EndpointErrorInfo
		{
			Code = string.IsNullOrWhiteSpace( code ) ? "UNKNOWN" : code,
			Message = message ?? ""
		};
	}

	private sealed class EndpointErrorInfo
	{
		public string Code { get; init; }
		public string Message { get; init; }
	}

	private static string TruncateJson( JsonElement el ) => TruncateJson( el.ToString() ?? "", 120 );

	private static string TruncateJson( string s, int max = 120 )
		=> s.Length > max ? s[..max] + "..." : s;
}