using System;
using System.IO;
using System.Text.Json;

public static partial class SyncToolConfig
{
	public static string AuthSessionsLabel => EnableAuthSessions ? "Enabled" : "Disabled";
	public static string EncryptedRequestsLabel => EnableEncryptedRequests ? "Enabled" : "Disabled";

	public static bool ApplyProjectSecuritySettings( bool enableAuthSessions, bool enableEncryptedRequests )
	{
		var changed = EnableAuthSessions != enableAuthSessions
			|| EnableEncryptedRequests != enableEncryptedRequests;

		EnableAuthSessions = enableAuthSessions;
		EnableEncryptedRequests = enableEncryptedRequests;

		if ( changed && File.Exists( Abs( ProjectConfigFile ) ) )
			Save( SecretKey, PublicApiKey, ProjectId, BaseUrl, DataSource, DataFolder, CdnUrl );

		return changed;
	}

	public static bool TryApplyProjectSecuritySettings( JsonElement response )
	{
		if ( TryFindProjectSecuritySettings( response, out var authSessions, out var encryptedRequests ) )
			return ApplyProjectSecuritySettings( authSessions, encryptedRequests );

		return false;
	}

	private static bool TryFindProjectSecuritySettings( JsonElement value, out bool authSessions, out bool encryptedRequests )
	{
		authSessions = false;
		encryptedRequests = false;

		if ( value.ValueKind != JsonValueKind.Object )
			return false;

		if ( TryReadBool( value, "enableAuthSessions", out authSessions )
			& TryReadBool( value, "enableEncryptedRequests", out encryptedRequests ) )
			return true;

		foreach ( var key in new[] { "project", "settings", "projectSettings", "data" } )
		{
			if ( value.TryGetProperty( key, out var child )
				&& TryFindProjectSecuritySettings( child, out authSessions, out encryptedRequests ) )
				return true;
		}

		return false;
	}

	private static bool TryReadBool( JsonElement value, string key, out bool result )
	{
		result = false;
		if ( !value.TryGetProperty( key, out var property ) )
			return false;

		if ( property.ValueKind is JsonValueKind.True or JsonValueKind.False )
		{
			result = property.GetBoolean();
			return true;
		}

		if ( property.ValueKind == JsonValueKind.String && bool.TryParse( property.GetString(), out result ) )
			return true;

		return false;
	}
}
