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

	/// <summary>
	/// Call an endpoint with automatic save state tracking and logging.
	/// Sets State to Saving → Saved/Error. Logs to NetLog.
	/// </summary>
	public async Task<JsonElement?> Call( string slug, object input = null )
	{
		PendingCalls++;
		SetState( SaveState.Saving );

		if ( NetworkStorageLogConfig.LogRequests )
			NetLog.Info( slug, $"Calling endpoint... input={( input != null ? JsonSerializer.Serialize( input ) : "null" )}" );

		var result = await NetworkStorage.CallEndpoint( slug, input );

		PendingCalls--;
		if ( result.HasValue )
		{
			if ( PendingCalls <= 0 )
				SetState( SaveState.Saved );
			if ( NetworkStorageLogConfig.LogResponses )
				NetLog.Info( slug, "Success" );
		}
		else
		{
			var error = $"{slug} failed — server returned null";
			SetState( SaveState.Error, error );
			if ( NetworkStorageLogConfig.LogErrors )
				NetLog.Error( slug, error );
		}

		return result;
	}

	/// <summary>
	/// Call an endpoint, apply the result to a callback on success, revert on failure.
	/// Handles the optimistic update → server confirm → revert pattern.
	/// </summary>
	public async Task<bool> CallAndApply( string slug, object input, Action applyOptimistic, Action<JsonElement> applyServer, Action revert )
	{
		applyOptimistic?.Invoke();

		var result = await Call( slug, input );

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
