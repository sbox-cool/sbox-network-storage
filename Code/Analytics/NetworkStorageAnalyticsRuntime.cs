using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Diagnostics;

namespace Sandbox;

/// <summary>
/// Lightweight managed analytics runtime. Created automatically after NetworkStorage.Configure().
/// Sends join/leave and a 30 second heartbeat with privacy-safe s&amp;box/runtime telemetry.
/// </summary>
internal sealed class NetworkStorageAnalyticsRuntime : Component
{
	private const float HeartbeatInterval = 30f;
	private const float VoiceActivityThreshold = 0.02f;
	private static NetworkStorageAnalyticsRuntime _instance;
	private static bool _ensureQueued;
	private static readonly object EndpointLock = new();
	private static int _endpointCallCount;
	private static int _endpointFailureCount;
	private static string _lastEndpointSlug;
	private static string _lastEndpointErrorCode;
	private static string _lastEndpointErrorMessage;
	private static double _lastEndpointLatencyMs;

	private string _sessionId;
	private double _sessionStartedAt;
	private double _nextHeartbeatAt;
	private double _fpsSum;
	private int _fpsSamples;
	private float _fpsPeak;
	private float _fpsMin = float.MaxValue;
	private bool _leaveSent;
	private double _voiceSpeakingSeconds;
	private int _voiceListeningCount;
	private float _voicePeakAmplitude;

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

	internal static void RecordEndpointDiagnostic( string slug, bool ok, double elapsedMs = 0, string code = null, string message = null )
	{
		if ( string.IsNullOrWhiteSpace( slug ) ) return;
		if ( string.Equals( slug, "analytics", StringComparison.OrdinalIgnoreCase ) ) return;

		lock ( EndpointLock )
		{
			_endpointCallCount++;
			_lastEndpointSlug = slug;
			_lastEndpointLatencyMs = Math.Max( 0, elapsedMs );
			if ( !ok )
			{
				_endpointFailureCount++;
				_lastEndpointErrorCode = string.IsNullOrWhiteSpace( code ) ? "UNKNOWN" : code;
				_lastEndpointErrorMessage = Redact( message ?? "" );
			}
		}

		if ( ok || !NetworkStorage.EnablePlayerAnalytics || !NetworkStorage.AnalyticsCaptureEndpointDiagnostics ) return;
		_ = NetworkStorage.TrackManagedDiagnosticEvent( "endpoint.client_error", new
		{
			managedTelemetry = "endpoint-diagnostics",
			endpointSlug = slug,
			code = string.IsNullOrWhiteSpace( code ) ? "UNKNOWN" : code,
			message = Redact( message ?? "" ),
			elapsedMs = Math.Max( 0, elapsedMs ),
			clientType = NetworkStorage.GetClientType(),
			isHost = NetworkStorage.IsHost,
			proxyEnabled = NetworkStorage.ProxyEnabled,
			revisionId = NetworkStoragePackageInfo.RuntimeRevisionId,
			packageIdent = NetworkStoragePackageInfo.PackageIdent,
			libraryVersion = NetworkStorage.PackageVersion
		}, "Endpoint client error" );
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
		SampleVoice();

		if ( RealTime.Now < _nextHeartbeatAt ) return;
		_nextHeartbeatAt = RealTime.Now + HeartbeatInterval;
		var fps = NetworkStorage.AnalyticsManagedPerformance ? BuildFpsPayload( true ) : null;
		if ( !NetworkStorage.AnalyticsManagedPerformance ) ResetFpsWindow();
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

	private object BuildContext( string eventName )
	{
		var context = new Dictionary<string, object>
		{
			["schemaVersion"] = 1,
			["sessionId"] = _sessionId,
			["eventName"] = eventName
		};

		if ( NetworkStorage.AnalyticsManagedRuntime ) context["runtime"] = BuildRuntimeSnapshot();
		if ( NetworkStorage.AnalyticsManagedRevision ) context["revision"] = BuildRevisionSnapshot();
		if ( NetworkStorage.AnalyticsManagedPerformance ) context["performance"] = BuildPerformanceSnapshot();
		if ( NetworkStorage.AnalyticsManagedNetwork ) context["network"] = BuildNetworkSnapshot();
		if ( NetworkStorage.AnalyticsManagedVoice ) context["voice"] = BuildVoiceSnapshot( reset: eventName == "heartbeat" );
		if ( NetworkStorage.AnalyticsManagedInputFocus ) context["inputFocus"] = BuildInputFocusSnapshot();
		if ( NetworkStorage.AnalyticsManagedScene ) context["scene"] = BuildSceneSnapshot();
		if ( NetworkStorage.AnalyticsManagedSystemInfo ) context["system"] = BuildSystemSnapshot();
		if ( NetworkStorage.AnalyticsCaptureEndpointDiagnostics ) context["endpointHealth"] = TakeEndpointHealthSnapshot();

		return context;
	}

	private object BuildRuntimeSnapshot() => new
	{
		clientType = NetworkStorage.GetClientType(),
		isHost = NetworkStorage.IsHost,
		isNetworkActive = Networking.IsActive,
		isNetworkHost = Networking.IsHost,
		isNetworkClient = Networking.IsClient,
		isProxyEnabled = NetworkStorage.ProxyEnabled,
		authSessionsEnabled = NetworkStorage.EnableAuthSessions,
		encryptedRequestsEnabled = NetworkStorage.EnableEncryptedRequests,
		securityConfigVersion = NetworkStorage.RuntimeSecurityConfigVersion,
		packageIdent = NetworkStoragePackageInfo.PackageIdent,
		packageTitle = NetworkStoragePackageInfo.PackageTitle,
		libraryVersion = NetworkStorage.PackageVersion,
		apiVersion = NetworkStorage.ApiVersion
	};

	private object BuildRevisionSnapshot() => new
	{
		currentRevisionId = NetworkStoragePackageInfo.CurrentRevisionId,
		runtimeRevisionId = NetworkStoragePackageInfo.RuntimeRevisionId,
		latestRevisionId = NetworkStoragePackageInfo.ServerCurrentRevision ?? NetworkStoragePackageInfo.LatestRevisionId,
		isOutdated = NetworkStoragePackageInfo.IsOutdatedRevision,
		graceRemainingMinutes = NetworkStoragePackageInfo.GraceRemainingMinutes,
		graceExpired = NetworkStoragePackageInfo.GraceExpired,
		action = NetworkStoragePackageInfo.RevisionAction,
		message = Redact( NetworkStoragePackageInfo.RevisionMessage ?? "" ),
		publishStatus = NetworkStoragePackageInfo.PublishStatus,
		packageIdent = NetworkStoragePackageInfo.PackageIdent,
		clientType = NetworkStoragePackageInfo.RuntimeClientType,
		isPublishedGameBundle = NetworkStoragePackageInfo.IsPublishedGameBundle
	};

	private object BuildPerformanceSnapshot()
	{
		try
		{
			var last = PerformanceStats.LastSecond;
			var frame = FrameStats.Current;
			return new
			{
				frameTimeMs = PerformanceStats.FrameTime * 1000.0,
				gpuFrameTimeMs = PerformanceStats.GpuFrametime,
				lastSecondFrameAvgMs = last.FrameAvg,
				lastSecondFrameMinMs = last.FrameMin,
				lastSecondFrameMaxMs = last.FrameMax,
				bytesAllocated = PerformanceStats.BytesAllocated,
				memoryMb = Math.Round( PerformanceStats.ApproximateProcessMemoryUsage / 1024.0 / 1024.0, 1 ),
				gc0 = PerformanceStats.Gen0Collections,
				gc1 = PerformanceStats.Gen1Collections,
				gc2 = PerformanceStats.Gen2Collections,
				gcPauseTicks = PerformanceStats.GcPause,
				exceptions = PerformanceStats.Exceptions,
				drawCalls = frame.DrawCalls,
				trianglesRendered = frame.TrianglesRendered,
				objectsRendered = frame.ObjectsRendered,
				sceneViewsRendered = frame.SceneViewsRendered
			};
		}
		catch
		{
			return new
			{
				frameTimeMs = Time.Delta > 0 ? Time.Delta * 1000.0 : 0
			};
		}
	}

	private object BuildNetworkSnapshot()
	{
		var local = Connection.Local;
		var stats = local?.Stats;
		return new
		{
			isActive = Networking.IsActive,
			isHost = Networking.IsHost,
			isClient = Networking.IsClient,
			isConnecting = Networking.IsConnecting,
			connectionCount = Connection.All?.Count ?? 0,
			hostSteamId = Connection.Host?.SteamId.ToString() ?? "",
			roster = Connection.All?.Take( 64 ).Select( c => new { steamId = c.SteamId.ToString(), isHost = c.IsHost, isActive = c.IsActive, isConnecting = c.IsConnecting } ).ToArray(),
			localSteamId = Game.SteamId.ToString(),
			localDisplayName = Redact( Connection.Local?.DisplayName ?? "" ),
			pingMs = local?.Ping ?? 0,
			latencyMs = (local?.Latency ?? 0) * 1000f,
			connectionQuality = stats?.ConnectionQuality ?? 0,
			inBytesPerSecond = stats?.InBytesPerSecond ?? 0,
			outBytesPerSecond = stats?.OutBytesPerSecond ?? 0,
			sendRateBytesPerSecond = stats?.SendRateBytesPerSecond ?? 0,
			hostPingMs = Connection.Host?.Ping ?? 0,
			serverName = Redact( Networking.ServerName ?? "" ),
			mapName = Redact( Networking.MapName ?? "" )
		};
	}

	private object BuildVoiceSnapshot( bool reset )
	{
		var payload = new
		{
			speakingSeconds = Math.Round( _voiceSpeakingSeconds, 2 ),
			listeningCount = _voiceListeningCount,
			peakAmplitude = Math.Round( _voicePeakAmplitude, 3 )
		};
		if ( reset )
		{
			_voiceSpeakingSeconds = 0;
			_voiceListeningCount = 0;
			_voicePeakAmplitude = 0;
		}
		return payload;
	}

	private object BuildInputFocusSnapshot() => new
	{
		isPaused = Game.IsPaused,
		isPlaying = Game.IsPlaying,
		isMainMenuVisible = Game.IsMainMenuVisible,
		isRecordingVideo = Game.IsRecordingVideo
	};

	private object BuildSceneSnapshot()
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return new { name = "", isLoading = false, componentCount = 0 };
		var componentCount = 0;
		try { componentCount = scene.GetAllComponents<Component>().Take( 10000 ).Count(); } catch { }
		return new
		{
			name = scene.Name ?? "",
			isEditor = scene.IsEditor,
			isLoading = scene.IsLoading,
			timeScale = scene.TimeScale,
			componentCount
		};
	}

	private object BuildSystemSnapshot() => new
	{
		clientType = NetworkStorage.GetClientType(),
		isEditor = Game.IsEditor,
		isDedicated = NetworkStorage.GetClientType() == "dedicated",
		isRunningInVr = Game.IsRunningInVR,
		isRunningOnHandheld = Game.IsRunningOnHandheld,
		memoryMb = Math.Round( PerformanceStats.ApproximateProcessMemoryUsage / 1024.0 / 1024.0, 1 )
	};

	private static object TakeEndpointHealthSnapshot()
	{
		lock ( EndpointLock )
		{
			var payload = new
			{
				callCount = _endpointCallCount,
				failureCount = _endpointFailureCount,
				lastEndpointSlug = _lastEndpointSlug,
				lastErrorSlug = _lastEndpointSlug,
				lastErrorCode = _lastEndpointErrorCode,
				lastErrorMessage = _lastEndpointErrorMessage,
				lastLatencyMs = Math.Round( _lastEndpointLatencyMs, 1 )
			};
			_endpointCallCount = 0;
			_endpointFailureCount = 0;
			_lastEndpointLatencyMs = 0;
			return payload;
		}
	}

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

	private void SampleVoice()
	{
		if ( !NetworkStorage.AnalyticsManagedVoice || Game.ActiveScene is null ) return;
		try
		{
			var listening = 0;
			foreach ( var voice in Game.ActiveScene.GetAllComponents<Voice>() )
			{
				if ( voice is null || !voice.Enabled ) continue;
				if ( voice.IsListening ) listening++;
				var amplitude = Math.Max( 0, voice.Amplitude );
				if ( voice.IsRecording || amplitude > VoiceActivityThreshold )
				{
					_voiceSpeakingSeconds += Math.Max( 0, Time.Delta );
					_voicePeakAmplitude = Math.Max( _voicePeakAmplitude, amplitude );
				}
			}
			_voiceListeningCount = Math.Max( _voiceListeningCount, listening );
		}
		catch
		{
			// Voice telemetry is best-effort and must never affect gameplay.
		}
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


	private static string Redact( string value )
	{
		if ( string.IsNullOrEmpty( value ) ) return "";
		var text = value.Length > 500 ? value[..500] : value;
		text = Regex.Replace( text, "sbox_(sk|ns)_[A-Za-z0-9_-]+", "[redacted-key]" );
		text = Regex.Replace( text, "(?i)(token|secret|password|authorization|api[-_]?key)=\\S+", "$1=[redacted]" );
		return text;
	}
}
