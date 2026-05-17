using System;
using System.Collections.Generic;

namespace Sandbox;

public static partial class NetworkStorage
{
	public static bool EnablePlayerAnalytics { get; private set; } = true;
	public static bool AnalyticsCaptureSessions { get; private set; } = true;
	public static bool AnalyticsCaptureWarnings { get; private set; } = true;
	public static bool AnalyticsCaptureErrors { get; private set; } = true;
	public static bool AnalyticsCaptureLocalWarnings { get; private set; } = true;
	public static bool AnalyticsCaptureLocalErrors { get; private set; } = true;
	public static bool AnalyticsCaptureEndpointDiagnostics { get; private set; } = true;
	public static bool AnalyticsManagedLibraryEvents { get; private set; } = true;
	public static bool AnalyticsManagedPerformance { get; private set; } = true;
	public static bool AnalyticsManagedNetwork { get; private set; } = true;
	public static bool AnalyticsManagedVoice { get; private set; } = true;
	public static bool AnalyticsManagedInputFocus { get; private set; } = true;
	public static bool AnalyticsManagedScene { get; private set; } = true;
	public static bool AnalyticsManagedRuntime { get; private set; } = true;
	public static bool AnalyticsManagedRevision { get; private set; } = true;
	public static bool AnalyticsManagedSystemInfo { get; private set; } = true;
	public static bool AnalyticsRetainErrorStacks { get; private set; }

	private static HashSet<string> AnalyticsAllowedEventTypes { get; set; } = new( StringComparer.OrdinalIgnoreCase )
	{
		"death", "respawn", "cast_line", "caught_fish", "sold_fish"
	};

	private static string NormalizeAnalyticsEventType( string eventType )
	{
		if ( string.IsNullOrWhiteSpace( eventType ) ) return "";
		var chars = new List<char>();
		foreach ( var c in eventType.Trim().ToLowerInvariant() )
		{
			if ( char.IsLetterOrDigit( c ) || c is '_' or '-' or '.' or ':' ) chars.Add( c );
			else if ( char.IsWhiteSpace( c ) ) chars.Add( '_' );
		}
		return new string( chars.ToArray() ).Trim( '_' );
	}

	private static bool IsAnalyticsEventAllowed( string eventType )
	{
		if ( !EnablePlayerAnalytics || !AnalyticsManagedLibraryEvents ) return false;
		var normalized = NormalizeAnalyticsEventType( eventType );
		return !string.IsNullOrWhiteSpace( normalized ) && AnalyticsAllowedEventTypes.Contains( normalized );
	}

	private static void ApplyAnalyticsRuntimeConfig( System.Text.Json.JsonElement settings )
	{
		if ( settings.ValueKind != System.Text.Json.JsonValueKind.Object ) return;
		var analytics = settings.TryGetProperty( "analytics", out var prop ) ? prop : default;
		if ( analytics.ValueKind != System.Text.Json.JsonValueKind.Object ) return;

		EnablePlayerAnalytics = ReadBool( analytics, "enabled", EnablePlayerAnalytics );
		AnalyticsCaptureSessions = ReadBool( analytics, "captureSessions", AnalyticsCaptureSessions );
		AnalyticsCaptureWarnings = ReadBool( analytics, "captureWarnings", AnalyticsCaptureWarnings );
		AnalyticsCaptureErrors = ReadBool( analytics, "captureErrors", AnalyticsCaptureErrors );
		AnalyticsCaptureLocalWarnings = ReadBool( analytics, "captureLocalWarnings", AnalyticsCaptureLocalWarnings );
		AnalyticsCaptureLocalErrors = ReadBool( analytics, "captureLocalErrors", AnalyticsCaptureLocalErrors );
		AnalyticsCaptureEndpointDiagnostics = ReadBool( analytics, "captureEndpointDiagnostics", AnalyticsCaptureEndpointDiagnostics );
		AnalyticsManagedLibraryEvents = ReadBool( analytics, "managedLibraryEvents", AnalyticsManagedLibraryEvents );
		AnalyticsManagedPerformance = ReadBool( analytics, "managedPerformance", AnalyticsManagedPerformance );
		AnalyticsManagedNetwork = ReadBool( analytics, "managedNetwork", AnalyticsManagedNetwork );
		AnalyticsManagedVoice = ReadBool( analytics, "managedVoice", AnalyticsManagedVoice );
		AnalyticsManagedInputFocus = ReadBool( analytics, "managedInputFocus", AnalyticsManagedInputFocus );
		AnalyticsManagedScene = ReadBool( analytics, "managedScene", AnalyticsManagedScene );
		AnalyticsManagedRuntime = ReadBool( analytics, "managedRuntime", AnalyticsManagedRuntime );
		AnalyticsManagedRevision = ReadBool( analytics, "managedRevision", AnalyticsManagedRevision );
		AnalyticsManagedSystemInfo = ReadBool( analytics, "managedSystemInfo", AnalyticsManagedSystemInfo );
		AnalyticsRetainErrorStacks = ReadBool( analytics, "retainErrorStacks", AnalyticsRetainErrorStacks );

		if ( analytics.TryGetProperty( "allowedEventTypes", out var allowed ) && allowed.ValueKind == System.Text.Json.JsonValueKind.Array )
		{
			var next = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			foreach ( var entry in allowed.EnumerateArray() )
			{
				if ( entry.ValueKind != System.Text.Json.JsonValueKind.String ) continue;
				var normalized = NormalizeAnalyticsEventType( entry.GetString() );
				if ( !string.IsNullOrWhiteSpace( normalized ) ) next.Add( normalized );
			}
			AnalyticsAllowedEventTypes = next;
		}
	}
}
