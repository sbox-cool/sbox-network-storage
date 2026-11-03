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

	/// <summary>The server clock (unix seconds) carried by the last refusal for slug, or 0 when it reported none.</summary>
	internal static long LastEndpointServerTime( string slug )
		=> _lastEndpointErrors.TryGetValue( slug, out var error ) ? error.ServerTime : 0;

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

		// Only an object carries envelope fields. Arrays and scalars are game data and are returned unchanged.
		if ( json.ValueKind != JsonValueKind.Object )
			return json;

		// ── Error detection ──

		// Update revision status before error handling too. Revision-expired
		// responses intentionally return ok:false but still carry _revisionStatus.
		NetworkStoragePackageInfo.UpdateFromServerResponse( json );
		var firedRevisionStatusOutdated = false;
		if ( json.TryGetProperty( "_revisionStatus", out var statusProp ) && statusProp.ValueKind == JsonValueKind.Object )
		{
			var statusData = RevisionOutdatedData.FromRevisionStatusJson( statusProp );
			if ( statusData.HasValue && statusData.Value.RevisionOutdated )
			{
				firedRevisionStatusOutdated = true;
				InvokeSafely( OnRevisionOutdated, statusData.Value, nameof( OnRevisionOutdated ) );
			}
		}

		// ok:true marks the body as data, so error-shaped fields inside it are not failures.
		var okKind = json.TryGetProperty( "ok", out var ok ) ? ok.ValueKind : JsonValueKind.Undefined;

		// 1) Explicit ok: false, or an error object / error string without ok:true
		if ( okKind == JsonValueKind.False ||
			( okKind != JsonValueKind.True && json.TryGetProperty( "error", out var errProp ) &&
				( errProp.ValueKind == JsonValueKind.Object || errProp.ValueKind == JsonValueKind.String ) ) )
		{
			LogServerError( slug, json );
			return null;
		}

		// 2) HTTP error status forwarded as { status: 4xx/5xx }
		if ( okKind != JsonValueKind.True && json.TryGetProperty( "status", out var status ) &&
			status.ValueKind == JsonValueKind.Number && status.TryGetInt32( out var statusCode ) && statusCode >= 400 )
		{
			LogServerError( slug, json );
			return null;
		}

		// Check for load-profile revision block and fire outdated event
		if ( !firedRevisionStatusOutdated && json.TryGetProperty( "revision", out var revisionProp ) && revisionProp.ValueKind == JsonValueKind.Object )
		{
			var data = RevisionOutdatedData.FromJson( revisionProp );
			if ( data.HasValue && data.Value.RevisionOutdated )
				NetworkStorage.FireRevisionOutdated( data.Value );
		}

		// Server wraps endpoint responses in { ok, body, timing }. Unwrap only that envelope; any other body is data.
		if ( okKind == JsonValueKind.True && json.TryGetProperty( "timing", out _ ) && json.TryGetProperty( "body", out var body ) )
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

	private static void RecordEndpointError( string slug, string code, string message, long serverTime = 0 )
	{
		if ( string.IsNullOrEmpty( slug ) )
			return;

		var safeCode = string.IsNullOrWhiteSpace( code ) ? "UNKNOWN" : code;
		if ( safeCode == "LIBRARY_UNSUPPORTED" ) ObserveLibraryStatus( "unsupported" );
		else if ( safeCode == "LIBRARY_BLOCKED" ) ObserveLibraryStatus( "blocked" );
		var safeMessage = message ?? "";
		_lastEndpointErrors[slug] = new EndpointErrorInfo
		{
			Code = safeCode,
			Message = safeMessage,
			ServerTime = serverTime
		};
		NetworkStorageAnalyticsRuntime.RecordEndpointDiagnostic( slug, ok: false, code: safeCode, message: safeMessage );
	}

	/// <summary>
	/// Records a validation error under the call's key and under its code (<c>INVALID_ENDPOINT</c>, <c>INVALID_QUERY</c>,
	/// <c>CAPABILITY_UNAVAILABLE</c>), so a call without a usable key, such as a blank endpoint reference, stays readable.
	/// </summary>
	private static void RecordEndpointErrorUnderCode( string slug, string code, string message )
	{
		if ( string.IsNullOrWhiteSpace( slug ) )
		{
			RecordEndpointError( code, code, message );
			return;
		}

		RecordEndpointError( slug, code, message );
		_lastEndpointErrors[code] = new EndpointErrorInfo { Code = code, Message = message ?? "" };
	}

	private sealed class EndpointErrorInfo
	{
		public string Code { get; init; }
		public string Message { get; init; }
		public long ServerTime { get; init; }
	}

	private static string TruncateJson( JsonElement el ) => TruncateJson( el.ToString() ?? "", 120 );

	private static string TruncateJson( string s, int max = 120 )
		=> s.Length > max ? s[..max] + "..." : s;
}
