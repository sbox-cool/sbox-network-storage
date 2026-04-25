// ============================================================
// SaveStateTrackerExample.cs — Automatic save state management
// Copy this into your game project's Code/ directory.
// ============================================================

using System.Text.Json;

namespace Sandbox;

/// <summary>
/// Example: Using SaveStateTracker for automatic state management.
///
/// SaveStateTracker wraps NetworkStorage.CallEndpoint() and tracks
/// whether you're Idle, Saving, Saved, or in Error state. Use this
/// to drive HUD indicators (spinner, checkmark, error icon).
///
/// It also provides CallAndApply() for the optimistic update pattern:
/// apply locally → call server → revert if failed.
/// </summary>
public class SaveStateExample : Component
{
	private readonly SaveStateTracker _tracker = new();

	// ── Read these from your UI to show save status ──
	public SaveStateTracker.SaveState State => _tracker.State;
	public bool IsSaving => _tracker.IsBusy;
	public string LastError => _tracker.LastError;
	public TimeSince TimeSinceStateChange => _tracker.TimeSinceStateChange;

	// ── Game state ──
	[Sync] public int Currency { get; set; }
	public Dictionary<string, float> Inventory { get; set; } = new();

	/// <summary>
	/// Simple tracked call — just tracks Saving/Saved/Error state.
	/// </summary>
	public async Task MineOre( string oreId, float kg )
	{
		var result = await _tracker.Call( "mine-ore", new { ore_id = oreId, kg } );

		if ( result.HasValue )
		{
			// Apply server response
			Currency = JsonHelpers.GetInt( result.Value, "currency", Currency );
			Log.Info( $"Mined {kg}kg {oreId} — Currency: {Currency}" );
		}
		// If null, _tracker.State is already SaveState.Error
		// and _tracker.LastError has the message
	}

	/// <summary>
	/// Full optimistic update pattern with auto-revert.
	/// CallAndApply does: applyOptimistic → call server → applyServer or revert.
	/// </summary>
	public async Task SellOre( string oreId, float kg )
	{
		var held = Inventory.GetValueOrDefault( oreId, 0f );
		var actual = MathF.Min( kg, held );
		if ( actual <= 0f ) return;

		var prevCurrency = Currency;
		var prevHeld = held;

		var success = await _tracker.CallAndApply(
			slug: "sell-ore",
			input: new { ore_id = oreId, kg = actual },

			// 1. Optimistic local update (runs immediately)
			applyOptimistic: () =>
			{
				Currency += (int)( actual * 10f ); // estimated value
				Inventory[oreId] = held - actual;
			},

			// 2. Apply authoritative server response (runs on success)
			applyServer: ( JsonElement data ) =>
			{
				Currency = JsonHelpers.GetInt( data, "currency", Currency );
				// Server response is authoritative — override estimate
			},

			// 3. Revert (runs on failure)
			revert: () =>
			{
				Currency = prevCurrency;
				Inventory[oreId] = prevHeld;
				Log.Warning( $"Sell failed — reverted" );
			}
		);

		if ( success )
			Log.Info( $"Sold {actual}kg {oreId} — Currency: {Currency}" );
	}

	/// <summary>
	/// Example: Show save state in a Razor UI component.
	/// </summary>
	// In your .razor file:
	//
	// @if ( SaveStateExample.Instance?.IsSaving == true )
	// {
	//     <div class="save-indicator saving">Saving...</div>
	// }
	// else if ( SaveStateExample.Instance?.State == SaveStateTracker.SaveState.Saved )
	// {
	//     <div class="save-indicator saved">Saved</div>
	// }
	// else if ( SaveStateExample.Instance?.State == SaveStateTracker.SaveState.Error )
	// {
	//     <div class="save-indicator error">@SaveStateExample.Instance.LastError</div>
	// }
}
