using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	private static Task<bool> _dedicatedSecretValidationTask;

	/// <summary>
	/// Validates the configured dedicated-server secret key against the management API.
	/// No-op outside dedicated server hosts. Logs success/failure without exposing the key.
	/// </summary>
	public static Task<bool> EnsureDedicatedServerSecretValidatedAsync( string reason = "startup", bool forceRefresh = false )
	{
		if ( !IsDedicatedServerHost )
			return Task.FromResult( true );

		if ( !forceRefresh && _dedicatedSecretValidationTask is not null )
			return _dedicatedSecretValidationTask;

		_dedicatedSecretValidationTask = ValidateDedicatedServerSecretAsync( reason );
		return _dedicatedSecretValidationTask;
	}

	private static async Task<bool> ValidateDedicatedServerSecretAsync( string reason )
	{
		EnsureConfigured();
		ClearLastEndpointError( "dedicated-secret" );

		if ( !TryGetDedicatedServerSecretKey( null, out var secretKey ) )
		{
			LogDedicatedSecretMissingOnce();
			RecordEndpointError( "dedicated-secret", "DEDICATED_SECRET_REQUIRED", DedicatedServerAuthUnavailableMessage() );
			return false;
		}

		if ( !CanTransportDedicatedServerSecretSecurely() )
		{
			LogInsecureSecretTransportOnce();
			RecordEndpointError( "dedicated-secret", "DEDICATED_SECRET_INSECURE_TRANSPORT", "Dedicated server secret keys are only sent to HTTPS or loopback API roots." );
			return false;
		}

		if ( IsPlaceholderSecretKey( secretKey ) )
		{
			var message = "Dedicated server secret key looks like the example placeholder; use a real sbox_sk_ key from the dashboard.";
			_dedicatedServerSecretKeyRejected = true;
			Log.Warning( $"[NetworkStorage] {message}" );
			RecordEndpointError( "dedicated-secret", "DEDICATED_SECRET_PLACEHOLDER", message );
			return false;
		}

		var path = $"/manage/{EscapeRouteSegment( ProjectId )}/validate";
		var url = $"{ApiRoot}{path}";
		var headers = new Dictionary<string, string>
		{
			["x-api-key"] = secretKey,
			["x-public-key"] = ApiKey ?? ""
		};

		try
		{
			Log.Info( $"[NetworkStorage] Validating dedicated server secret key source={DedicatedServerSecretKeySource} reason={reason}..." );

			var raw = await Http.RequestStringAsync( url, "GET", null, headers );
			using var doc = JsonDocument.Parse( raw );
			var root = doc.RootElement;
			if ( !DedicatedSecretValidationSucceeded( root, raw, out var detail ) )
			{
				var message = string.IsNullOrWhiteSpace( detail ) ? "Dedicated server secret key validation failed." : detail;
				_dedicatedServerSecretKeyRejected = true;
				Log.Warning( $"[NetworkStorage] Dedicated server secret key validation failed: {message}" );
				RecordEndpointError( "dedicated-secret", "DEDICATED_SECRET_INVALID", message );
				return false;
			}

			_dedicatedServerSecretKeyRejected = false;
			var project = ReadValidationProjectName( root );
			var suffix = string.IsNullOrWhiteSpace( project ) ? "" : $" project={project}";
			Log.Info( $"[NetworkStorage] Dedicated server secret key validated source={DedicatedServerSecretKeySource}{suffix}." );
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] Dedicated server secret key validation failed: {ex.Message}" );
			RecordEndpointError( "dedicated-secret", "DEDICATED_SECRET_VALIDATE_FAILED", ex.Message );
			return false;
		}
	}

	private static bool DedicatedSecretValidationSucceeded( JsonElement root, string raw, out string detail )
	{
		detail = ReadValidationFailureDetail( root, raw );
		if ( root.TryGetProperty( "error", out var error ) && error.ValueKind != JsonValueKind.Undefined )
			return false;
		if ( root.TryGetProperty( "ok", out var ok ) && ok.ValueKind == JsonValueKind.False )
			return false;

		if ( root.TryGetProperty( "checks", out var checks ) && checks.ValueKind == JsonValueKind.Object &&
			checks.TryGetProperty( "secretKey", out var secretCheck ) && secretCheck.ValueKind == JsonValueKind.Object )
		{
			if ( secretCheck.TryGetProperty( "message", out var message ) && message.ValueKind == JsonValueKind.String )
				detail = message.GetString();
			if ( secretCheck.TryGetProperty( "ok", out var checkOk ) && checkOk.ValueKind == JsonValueKind.False )
				return false;
			if ( secretCheck.TryGetProperty( "valid", out var valid ) && valid.ValueKind == JsonValueKind.False )
				return false;
			if ( secretCheck.TryGetProperty( "status", out var status ) && status.ValueKind == JsonValueKind.String )
			{
				var value = status.GetString();
				if ( string.Equals( value, "invalid", StringComparison.OrdinalIgnoreCase ) ||
					string.Equals( value, "failed", StringComparison.OrdinalIgnoreCase ) ||
					string.Equals( value, "error", StringComparison.OrdinalIgnoreCase ) )
				{
					return false;
				}
			}
		}

		return true;
	}

	private static string ReadValidationFailureDetail( JsonElement root, string raw )
	{
		if ( root.TryGetProperty( "error", out var error ) )
		{
			if ( error.ValueKind == JsonValueKind.Object )
			{
				var code = error.TryGetProperty( "code", out var codeElement ) && codeElement.ValueKind == JsonValueKind.String
					? codeElement.GetString()
					: null;
				var message = error.TryGetProperty( "message", out var messageElement ) && messageElement.ValueKind == JsonValueKind.String
					? messageElement.GetString()
					: null;
				if ( !string.IsNullOrWhiteSpace( code ) || !string.IsNullOrWhiteSpace( message ) )
					return string.IsNullOrWhiteSpace( code ) ? message : string.IsNullOrWhiteSpace( message ) ? code : $"{code}: {message}";
			}
			else if ( error.ValueKind == JsonValueKind.String )
			{
				return error.GetString();
			}
		}

		var serverMessage = ReadServerMessage( root, null );
		if ( !string.IsNullOrWhiteSpace( serverMessage ) )
			return serverMessage;

		return string.IsNullOrWhiteSpace( raw ) ? null : $"Unexpected validate response: {TruncateJson( raw, 300 )}";
	}

	private static bool IsPlaceholderSecretKey( string secretKey )
	{
		if ( string.IsNullOrWhiteSpace( secretKey ) )
			return false;

		var normalized = secretKey.Trim();
		return string.Equals( normalized, "sbox_sk_your_secret_key", StringComparison.OrdinalIgnoreCase )
			|| normalized.Contains( "your_secret", StringComparison.OrdinalIgnoreCase )
			|| normalized.Contains( "your-secret", StringComparison.OrdinalIgnoreCase );
	}

	private static string ReadValidationProjectName( JsonElement root )
	{
		if ( root.TryGetProperty( "project", out var project ) && project.ValueKind == JsonValueKind.Object )
		{
			if ( project.TryGetProperty( "name", out var name ) && name.ValueKind == JsonValueKind.String )
				return name.GetString();
			if ( project.TryGetProperty( "title", out var title ) && title.ValueKind == JsonValueKind.String )
				return title.GetString();
		}

		return null;
	}
}
