using System;

namespace Sandbox;

/// <summary>
/// Lightweight managed analytics runtime. Created automatically after NetworkStorage.Configure().
/// Sends join/leave and a 30 second heartbeat with FPS summary data.
/// </summary>
internal sealed class NetworkStorageAnalyticsRuntime : Component
{
	private const float HeartbeatInterval = 30f;
	private static NetworkStorageAnalyticsRuntime _instance;
	private static bool _ensureQueued;

	private string _sessionId;
	private double _sessionStartedAt;
	private double _nextHeartbeatAt;
	private double _fpsSum;
	private int _fpsSamples;
	private float _fpsPeak;
	private float _fpsMin = float.MaxValue;
	private bool _leaveSent;

	internal static void EnsureCreated( string reason = "unspecified" )
	{
		if ( _instance is not null ) return;
		if ( Game.ActiveScene is null )
		{
			QueueEnsureRetry( reason );
			return;
		}

		foreach ( var existing in Game.ActiveScene.GetAllComponents<NetworkStorageAnalyticsRuntime>() )
		{
			_instance = existing;
			if ( NetworkStorageLogConfig.LogConfig )
				Log.Info( $"[NetworkStorage] Analytics runtime found in active scene (reason={reason})." );
			return;
		}

		var go = new GameObject( true, "Network Storage Analytics" );
		_instance = go.Components.Create<NetworkStorageAnalyticsRuntime>();
		if ( NetworkStorageLogConfig.LogConfig )
			Log.Info( $"[NetworkStorage] Analytics runtime created (reason={reason}); join + 30s heartbeat reporting enabled." );
	}

	private static void QueueEnsureRetry( string reason )
	{
		if ( _ensureQueued ) return;
		_ensureQueued = true;
		_ = EnsureCreatedWhenSceneReady();
		if ( NetworkStorageLogConfig.LogConfig )
			Log.Info( $"[NetworkStorage] Analytics runtime waiting for active scene (reason={reason})..." );
	}

	private static async System.Threading.Tasks.Task EnsureCreatedWhenSceneReady()
	{
		for ( var attempt = 1; attempt <= 30; attempt++ )
		{
			await System.Threading.Tasks.Task.Delay( 500 );
			if ( _instance is not null ) return;
			if ( Game.ActiveScene is null ) continue;
			_ensureQueued = false;
			EnsureCreated( "scene-ready-retry" );
			return;
		}

		_ensureQueued = false;
		if ( NetworkStorageLogConfig.LogErrors )
			Log.Warning( "[NetworkStorage] Analytics runtime could not start because no active scene became available." );
	}

	protected override void OnEnabled()
	{
		_instance = this;
		_sessionId = Guid.NewGuid().ToString( "N" );
		_sessionStartedAt = RealTime.Now;
		_nextHeartbeatAt = RealTime.Now + 2f;
		ResetFpsWindow();
		if ( NetworkStorageLogConfig.LogRequests )
			Log.Info( $"[NetworkStorage] Analytics session join queued session={_sessionId}" );
		_ = NetworkStorage.TrackManagedSessionSignal( "join", _sessionId, 0, BuildContext( "join" ) );
	}

	protected override void OnUpdate()
	{
		SampleFps();

		if ( RealTime.Now < _nextHeartbeatAt ) return;
		_nextHeartbeatAt = RealTime.Now + HeartbeatInterval;
		var fps = BuildFpsPayload( true );
		if ( NetworkStorageLogConfig.LogRequests )
			Log.Info( $"[NetworkStorage] Analytics heartbeat queued session={_sessionId} seconds={SessionSeconds:0}s" );
		_ = NetworkStorage.TrackManagedSessionSignal( "heartbeat", _sessionId, SessionSeconds, BuildContext( "heartbeat" ), fps );
	}

	protected override void OnDestroy()
	{
		SendLeaveOnce();
		if ( _instance == this ) _instance = null;
	}

	private double SessionSeconds => Math.Max( 0, RealTime.Now - _sessionStartedAt );

	private object BuildContext( string eventName ) => new
	{
		sessionId = _sessionId,
		eventName,
		clientType = NetworkStorage.GetClientType(),
		isHost = NetworkStorage.IsHost,
		isProxyEnabled = NetworkStorage.ProxyEnabled
	};

	private void SampleFps()
	{
		if ( Time.Delta <= 0 ) return;
		var fps = 1f / Time.Delta;
		if ( float.IsNaN( fps ) || float.IsInfinity( fps ) || fps <= 0 ) return;
		fps = Math.Clamp( fps, 1f, 1000f );
		_fpsSum += fps;
		_fpsSamples++;
		_fpsPeak = Math.Max( _fpsPeak, fps );
		_fpsMin = Math.Min( _fpsMin, fps );
	}

	private object BuildFpsPayload( bool reset )
	{
		var samples = Math.Max( 0, _fpsSamples );
		var average = samples > 0 ? _fpsSum / samples : 0;
		var peak = samples > 0 ? _fpsPeak : 0;
		var min = samples > 0 && _fpsMin < float.MaxValue ? _fpsMin : 0;
		var payload = new
		{
			average,
			peak,
			max = peak,
			min,
			samples,
			windowSeconds = HeartbeatInterval
		};
		if ( reset ) ResetFpsWindow();
		return payload;
	}

	private void ResetFpsWindow()
	{
		_fpsSum = 0;
		_fpsSamples = 0;
		_fpsPeak = 0;
		_fpsMin = float.MaxValue;
	}

	private void SendLeaveOnce()
	{
		if ( _leaveSent ) return;
		_leaveSent = true;
		if ( NetworkStorageLogConfig.LogRequests )
			Log.Info( $"[NetworkStorage] Analytics session leave queued session={_sessionId} seconds={SessionSeconds:0}s" );
		_ = NetworkStorage.TrackManagedSessionSignal( "leave", _sessionId, SessionSeconds, BuildContext( "leave" ) );
	}
}
