using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Analytics batching for servers that advertise the <c>analytics-batch</c> capability.
/// A batch is one POST to <c>/analytics/events</c> with <c>{"events":[...]}</c>; each item is
/// the single-event body. Without the capability every event is still sent on its own.
/// Events that are not delivered are counted and reported through a throttled warning.
/// </summary>
public static partial class NetworkStorage
{
	private const int AnalyticsBatchMaxEvents = 50;
	private const int AnalyticsBatchMaxBytes = 256 * 1024;
	private const int AnalyticsBatchEnvelopeBytes = 16; // {"events":[]} plus separators, rounded up
	private const int AnalyticsEventMaxBytes = 32 * 1024;
	private const int AnalyticsBatchMaxPending = 500;
	private const float AnalyticsBatchWindowSeconds = 2f;
	private const double AnalyticsDropReportIntervalSeconds = 30;

	private static readonly object AnalyticsBatchLock = new();
	private static readonly List<(string Json, int Bytes)> _analyticsBatchQueue = new();
	private static bool _analyticsDrainScheduled;
	private static long _analyticsDroppedTotal;
	private static long _analyticsDroppedUnreported;
	private static double _analyticsNextDropReportAt;

	/// <summary>
	/// Serializes one single-event body now, so later payload changes are not sent, and queues it.
	/// The queue drains <see cref="AnalyticsBatchWindowSeconds"/> after the first queued event.
	/// </summary>
	private static void EnqueueAnalyticsEvent( Dictionary<string, object> body )
	{
		var json = JsonSerializer.Serialize( body );
		var bytes = Encoding.UTF8.GetByteCount( json );
		if ( bytes > AnalyticsEventMaxBytes )
		{
			RecordAnalyticsDrop( 1, $"event is {bytes} bytes (limit {AnalyticsEventMaxBytes})" );
			return;
		}

		var overflow = false;
		var schedule = false;
		lock ( AnalyticsBatchLock )
		{
			if ( _analyticsBatchQueue.Count >= AnalyticsBatchMaxPending )
			{
				overflow = true;
			}
			else
			{
				_analyticsBatchQueue.Add( (json, bytes) );
				schedule = !_analyticsDrainScheduled;
				_analyticsDrainScheduled = true;
			}
		}

		if ( overflow ) RecordAnalyticsDrop( 1, "pending analytics queue is full" );
		if ( schedule ) _ = DrainAnalyticsBatchAsync( AnalyticsBatchWindowSeconds );
	}

	/// <summary>
	/// Sends queued events as batches until the queue is empty. Each request holds at most
	/// <see cref="AnalyticsBatchMaxEvents"/> items and <see cref="AnalyticsBatchMaxBytes"/> bytes.
	/// </summary>
	private static async Task DrainAnalyticsBatchAsync( float delaySeconds )
	{
		if ( delaySeconds > 0f ) await Task.Delay( TimeSpan.FromSeconds( delaySeconds ) );

		while ( true )
		{
			List<string> items;
			lock ( AnalyticsBatchLock )
			{
				if ( _analyticsBatchQueue.Count == 0 )
				{
					_analyticsDrainScheduled = false;
					return;
				}

				items = TakeAnalyticsBatch();
			}

			await SendAnalyticsBatchAsync( items );
		}
	}

	/// <summary>Removes the next batch from the queue. The caller must hold <see cref="AnalyticsBatchLock"/>.</summary>
	private static List<string> TakeAnalyticsBatch()
	{
		var items = new List<string>();
		var bytes = 0;
		while ( items.Count < AnalyticsBatchMaxEvents && _analyticsBatchQueue.Count > 0 )
		{
			var next = _analyticsBatchQueue[0];
			if ( items.Count > 0 && bytes + next.Bytes + items.Count + AnalyticsBatchEnvelopeBytes > AnalyticsBatchMaxBytes ) break;

			items.Add( next.Json );
			bytes += next.Bytes;
			_analyticsBatchQueue.RemoveAt( 0 );
		}

		return items;
	}

	private static async Task SendAnalyticsBatchAsync( List<string> items )
	{
		try
		{
			var headers = await BuildAuthHeaders();
			var url = BuildUrl( $"/storage/{Uri.EscapeDataString( ProjectId )}/analytics/events" );
			var content = new StringContent( "{\"events\":[" + string.Join( ",", items ) + "]}", Encoding.UTF8, "application/json" );
			var raw = await RequestStringAsync( url, "POST", content, headers );
			RecordAnalyticsResponse( raw, items.Count );
			if ( NetworkStorageLogConfig.LogRequests )
				NetLog.Info( "analytics", $"Reported batch events={items.Count}" );
		}
		catch ( Exception ex )
		{
			RecordAnalyticsDrop( items.Count, ex.Message );
		}
	}

	/// <summary>
	/// Counts the events the server did not accept for one <c>/analytics/events</c> POST. A 426 body
	/// (returned without throwing) or <c>ok:false</c> rejects all <paramref name="sent"/> events. A
	/// numeric <c>accepted</c> count is honored. A 2xx body that is not JSON counts as accepted.
	/// </summary>
	private static void RecordAnalyticsResponse( string raw, int sent )
	{
		var delivered = sent;
		var reason = "server did not accept the event";
		if ( !string.IsNullOrWhiteSpace( raw ) )
		{
			try
			{
				using var doc = JsonDocument.Parse( raw );
				var root = doc.RootElement;
				if ( root.ValueKind == JsonValueKind.Object )
				{
					if ( root.TryGetProperty( "error", out var error ) )
					{
						delivered = 0;
						reason = error.ValueKind == JsonValueKind.String ? error.GetString() ?? reason : reason;
					}
					else if ( root.TryGetProperty( "ok", out var ok ) && ok.ValueKind == JsonValueKind.False )
					{
						delivered = 0;
					}
					else if ( root.TryGetProperty( "accepted", out var accepted ) && accepted.TryGetInt32( out var count ) )
					{
						delivered = Math.Clamp( count, 0, sent );
					}
				}
			}
			catch ( JsonException )
			{
				// Non-JSON 2xx body: the request was accepted, so nothing is counted as dropped.
			}
		}

		RecordAnalyticsDrop( sent - delivered, reason );
	}

	/// <summary>
	/// Adds undelivered analytics events to the session total. At most one warning is written per
	/// <see cref="AnalyticsDropReportIntervalSeconds"/>; it carries the count since the previous warning.
	/// </summary>
	private static void RecordAnalyticsDrop( int count, string reason )
	{
		if ( count <= 0 ) return;

		long total;
		long unreported;
		var report = false;
		lock ( AnalyticsBatchLock )
		{
			_analyticsDroppedTotal += count;
			_analyticsDroppedUnreported += count;
			total = _analyticsDroppedTotal;
			unreported = _analyticsDroppedUnreported;
			if ( RealTime.Now >= _analyticsNextDropReportAt )
			{
				report = true;
				_analyticsNextDropReportAt = RealTime.Now + AnalyticsDropReportIntervalSeconds;
				_analyticsDroppedUnreported = 0;
			}
		}

		if ( !report ) return;
		var message = $"Dropped {unreported} analytics event(s) since the last report ({total} this session). Last reason: {reason}";
		NetLog.Warning( "analytics", message );
		Log.Warning( $"[NetworkStorage] {message}" );
	}

	/// <summary>Starts sending queued analytics events now. Called when the analytics runtime is destroyed.</summary>
	internal static void FlushAnalyticsBatch() => _ = DrainAnalyticsBatchAsync( 0f );
}
