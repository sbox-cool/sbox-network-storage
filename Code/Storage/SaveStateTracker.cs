using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Tracks the save state of endpoint calls for HUD feedback.
/// Wraps NetworkStorage.CallEndpoint with automatic state management and logging.
///
/// Usage:
///   var tracker = new SaveStateTracker();
///   var result = await tracker.Call( "mine-ore", new { ore_id = "moon_ore", kg = 5 } );
///   if ( result.HasValue ) Apply( result.Value );
///
/// Properties:
///   tracker.State     → Idle, Saving, Saved, Error
///   tracker.IsBusy    → true while any call is in progress
///   tracker.LastError → error message from last failure
/// </summary>
public class SaveStateTracker
{
	public enum SaveState { Idle, Saving, Saved, Error }

	public SaveState State { get; private set; } = SaveState.Idle;
	public TimeSince TimeSinceStateChange { get; private set; }
	public int PendingCalls { get; private set; }
	public string LastError { get; private set; }
	public bool IsBusy => PendingCalls > 0;

	private int _generation;

	/// <summary>
	/// Call an endpoint with automatic save state tracking and logging.
	/// Sets State to Saving → Saved/Error. Logs to NetLog. An exception sets Error and is rethrown to the caller.
	/// </summary>
	public async Task<JsonElement?> Call( string slug, object input = null )
	{
		var generation = _generation;
		PendingCalls++;
		SetState( SaveState.Saving );

		JsonElement? result = null;
		try
		{
			if ( NetworkStorageLogConfig.LogRequests )
				NetLog.Info( slug, $"Calling endpoint... input={( input != null ? JsonSerializer.Serialize( input ) : "null" )}" );

			result = await NetworkStorage.CallEndpoint( slug, input );
		}
		catch ( Exception ex )
		{
			Fail( generation, slug, $"{slug} failed — {ex.Message}" );
			throw;
		}
		finally
		{
			// A Reset() while this call was in flight already cleared the count; do not drive it negative.
			if ( generation == _generation )
				PendingCalls--;
		}

		// A Reset() while this call was in flight owns the state now; a stale completion must not overwrite it.
		if ( generation != _generation )
			return result;

		if ( !result.HasValue )
		{
			Fail( generation, slug, DescribeEndpointFailure( slug ) );
			return null;
		}

		if ( PendingCalls <= 0 )
			SetState( SaveState.Saved );
		if ( NetworkStorageLogConfig.LogResponses )
			NetLog.Info( slug, "Success" );
		return result;
	}

	private void Fail( int generation, string slug, string error )
	{
		// A Reset() while this call was in flight owns the state now; a stale completion must not overwrite it.
		if ( generation != _generation ) return;
		SetState( SaveState.Error, error );
		if ( NetworkStorageLogConfig.LogErrors )
			NetLog.Error( slug, error );
	}

	private static string DescribeEndpointFailure( string slug )
	{
		if ( !NetworkStorage.TryGetLastEndpointError( slug, out var code, out var message ) )
			return $"{slug} failed — server returned null";

		return string.IsNullOrEmpty( message ) ? $"{slug} failed — {code}" : $"{slug} failed — {code}: {message}";
	}

	/// <summary>
	/// Call an endpoint, apply the result to a callback on success, revert on failure.
	/// Handles the optimistic update → server confirm → revert pattern.
	/// </summary>
	public async Task<bool> CallAndApply( string slug, object input, Action applyOptimistic, Action<JsonElement> applyServer, Action revert )
	{
		applyOptimistic?.Invoke();

		JsonElement? result;
		try
		{
			result = await Call( slug, input );
		}
		catch
		{
			// Call has recorded the failure and rethrows it; the optimistic change must be reverted on that path too.
			revert?.Invoke();
			throw;
		}

		if ( result.HasValue )
		{
			applyServer?.Invoke( result.Value );
			return true;
		}

		revert?.Invoke();
		return false;
	}

	public void Reset()
	{
		_generation++;
		SetState( SaveState.Idle );
		PendingCalls = 0;
		LastError = null;
	}

	private void SetState( SaveState state, string error = null )
	{
		State = state;
		TimeSinceStateChange = 0;
		if ( error != null ) LastError = error;
	}
}
