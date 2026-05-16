using System;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Convenience facade for Player Analytics. Methods forward to NetworkStorage so
/// projects can use either NetworkStorageAnalytics.TrackEvent(...) or
/// NetworkStorage.TrackAnalyticsEvent(...).
/// </summary>
public static class NetworkStorageAnalytics
{
	public static Task TrackEvent( string eventType, object payload = null )
		=> NetworkStorage.TrackAnalyticsEvent( eventType, payload );

	public static Task Warning( string code, string message = null, object context = null )
		=> NetworkStorage.TrackAnalyticsWarning( code, message, context );

	public static Task Error( Exception exception, string code = null, object context = null )
		=> NetworkStorage.TrackAnalyticsError( exception, code, context );

	public static Task Error( string code, string message = null, string stack = null, object context = null )
		=> NetworkStorage.TrackAnalyticsError( code, message, stack, context );

	public static Task SessionStart( object context = null )
		=> NetworkStorage.TrackSessionStart( context );

	public static Task SessionHeartbeat( double sessionSeconds = 0, object context = null, object fps = null )
		=> NetworkStorage.TrackSessionHeartbeat( sessionSeconds, context, fps );

	public static Task SessionEnd( double durationSeconds = 0, object context = null )
		=> NetworkStorage.TrackSessionEnd( durationSeconds, context );
}
