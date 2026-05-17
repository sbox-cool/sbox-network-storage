using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

public static partial class NetworkStorage
{
	public static Task TrackAnalyticsEvent( string eventType, object payload = null )
		=> SendAnalyticsEvent( eventType, payload, "custom", null, null );

	public static Task TrackAnalyticsWarning( string code, string message = null, object context = null )
		=> AnalyticsCaptureWarnings
			? SendAnalyticsEvent( code, context, "warning", message, null )
			: Task.CompletedTask;

	public static Task TrackAnalyticsError( Exception exception, string code = null, object context = null )
		=> TrackAnalyticsError( code ?? exception?.GetType().Name ?? "error", exception?.Message, exception?.StackTrace, context );

	public static Task TrackAnalyticsError( string code, string message = null, string stack = null, object context = null )
		=> AnalyticsCaptureErrors
			? SendAnalyticsEvent( code, context, "error", message, AnalyticsRetainErrorStacks ? stack : null )
			: Task.CompletedTask;

	public static Task TrackSessionStart( object context = null )
		=> AnalyticsCaptureSessions ? SendSessionSignal( "join", 0, context, null, null ) : Task.CompletedTask;

	public static Task TrackSessionHeartbeat( double sessionSeconds = 0, object context = null, object fps = null )
		=> AnalyticsCaptureSessions ? SendSessionSignal( "heartbeat", sessionSeconds, context, fps, null ) : Task.CompletedTask;

	public static Task TrackSessionEnd( double durationSeconds = 0, object context = null )
		=> AnalyticsCaptureSessions ? SendSessionSignal( "leave", durationSeconds, context, null, null ) : Task.CompletedTask;

	internal static Task TrackManagedSessionSignal( string eventType, string sessionId, double sessionSeconds, object context = null, object fps = null )
		=> AnalyticsCaptureSessions ? SendSessionSignal( eventType, sessionSeconds, context, fps, sessionId ) : Task.CompletedTask;

	internal static Task TrackManagedDiagnosticEvent( string eventType, object payload = null, string label = null )
		=> EnablePlayerAnalytics ? SendAnalyticsEvent( eventType, payload, "info", label, null ) : Task.CompletedTask;

	private static async Task SendSessionSignal( string eventType, double sessionSeconds, object context, object fps, string sessionId )
	{
		try
		{
			EnsureConfigured();
			await EnsureRuntimeSecurityConfigAsync( "analytics-session" );
			if ( !EnablePlayerAnalytics || !AnalyticsCaptureSessions ) return;

			var body = new Dictionary<string, object>
			{
				["steamId"] = Game.SteamId.ToString(),
				["sessionId"] = sessionId,
				["sessionSeconds"] = Math.Max( 0, sessionSeconds ),
				["event"] = eventType,
				["playerName"] = Connection.Local?.DisplayName,
				["source"] = "network-storage-library",
				["libraryVersion"] = NetworkStorage.PackageVersion,
				["packageIdent"] = NetworkStoragePackageInfo.PackageIdent,
				["context"] = context,
				["fps"] = fps
			};

			var headers = await BuildAuthHeaders();
			var url = BuildUrl( $"/storage/{Uri.EscapeDataString( ProjectId )}/stats/heartbeat" );
			var content = Http.CreateJsonContent( body );
			await Http.RequestStringAsync( url, "POST", content, headers );
			if ( NetworkStorageLogConfig.LogRequests )
			{
				var logLine = $"Reported session:{eventType} session={sessionId ?? "manual"} seconds={sessionSeconds:0}s";
				NetLog.Info( "analytics", logLine );
				Log.Info( $"[NetworkStorage] Analytics {logLine}" );
			}
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
				NetLog.Error( "analytics", $"Session analytics failed: {ex.Message}" );
		}
	}

	private static async Task SendAnalyticsEvent( string eventType, object payload, string severity, string message, string stack )
	{
		try
		{
			EnsureConfigured();
			await EnsureRuntimeSecurityConfigAsync( "analytics" );
			if ( !EnablePlayerAnalytics ) return;

			var normalized = NormalizeAnalyticsEventType( eventType );
			if ( string.IsNullOrWhiteSpace( normalized ) ) return;
			if ( severity == "custom" && !IsAnalyticsEventAllowed( normalized ) )
			{
				if ( NetworkStorageLogConfig.LogRequests )
					NetLog.Info( "analytics", $"Suppressed disallowed event {normalized}" );
				return;
			}

			var steamId = Game.SteamId.ToString();
			var reportType = severity == "session" ? $"session.{normalized}" : normalized;
			var body = new Dictionary<string, object>
			{
				["steamId"] = steamId,
				["type"] = reportType,
				["severity"] = severity == "custom" ? null : severity,
				["label"] = severity == "info" && !string.IsNullOrWhiteSpace( message ) ? message : normalized,
				["message"] = message,
				["stack"] = stack,
				["context"] = payload,
				["source"] = "network-storage-library",
				["libraryVersion"] = NetworkStorage.PackageVersion,
				["packageIdent"] = NetworkStoragePackageInfo.PackageIdent
			};

			var headers = await BuildAuthHeaders();
			var url = BuildUrl( $"/storage/{Uri.EscapeDataString( ProjectId )}/analytics/events" );
			var content = Http.CreateJsonContent( body );
			await Http.RequestStringAsync( url, "POST", content, headers );
			if ( NetworkStorageLogConfig.LogRequests )
				NetLog.Info( "analytics", $"Reported {severity}:{normalized}" );
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogErrors )
				NetLog.Error( "analytics", $"Analytics report failed: {ex.Message}" );
		}
	}
}
