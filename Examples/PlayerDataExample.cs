// ============================================================
// PlayerDataExample.cs -- Manage player data with Network Storage
// Copy this into your game project's Code/ directory.
// ============================================================

using System.Text.Json;

namespace Sandbox;

/// <summary>
/// Example: Player data manager using Network Storage endpoints.
///
/// This shows the typical pattern:
/// 1. Load player data on join (or init for new players)
/// 2. Call endpoints for game actions (mine, sell, buy)
/// 3. Apply server responses to local state
/// 4. Optimistic updates with revert on failure
///
/// Your endpoints are defined on sboxcool.com and synced
/// via the Editor Sync Tool.
/// </summary>
public class MyPlayerData : Component
{
	// ── Synced to all clients (for nameplates, leaderboards) ──
	[Sync] public string PlayerName { get; set; } = "";
	[Sync] public int Level { get; set; } = 1;
	[Sync] public int Currency { get; set; }

	// ── Local-only state ──
	public Dictionary<string, float> Inventory { get; private set; } = new();
	public bool IsLoaded { get; private set; }

	protected override void OnStart()
	{
		if ( IsProxy ) return;
		_ = LoadAsync();
	}

	private async Task LoadAsync()
	{
		// Ensure library is configured
		if ( !NetworkStorage.IsConfigured )
			MyNetStorageConfig.Initialize();

		// Load existing player data
		var data = await NetworkStorage.CallEndpoint( "load-player" );
		if ( data.HasValue )
		{
			ApplyServerData( data.Value );
			IsLoaded = true;
			Log.Info( $"Loaded: {PlayerName} Lv.{Level}" );
			return;
		}

		// New player -- call init endpoint
		var init = await NetworkStorage.CallEndpoint( "init-player", new
		{
			playerName = Connection.Local?.DisplayName ?? "Player"
		} );

		if ( init.HasValue )
			ApplyServerData( init.Value );

		IsLoaded = true;
	}

	/// <summary>
	/// Mine ore -- optimistic local update, confirmed by server.
	/// </summary>
	public async Task MineOre( string oreId, float kg )
	{
		if ( kg <= 0f ) return;

		// Optimistic update
		Inventory[oreId] = Inventory.GetValueOrDefault( oreId, 0f ) + kg;

		var result = await NetworkStorage.CallEndpoint( "mine-ore", new
		{
			ore_id = oreId,
			kg
		} );

		if ( result.HasValue )
		{
			ApplyServerData( result.Value );
		}
		else
		{
			// Revert on failure
			Inventory[oreId] = Inventory.GetValueOrDefault( oreId, 0f ) - kg;
			if ( Inventory[oreId] <= 0f ) Inventory.Remove( oreId );
			Log.Warning( $"Mine failed -- reverted {kg}kg {oreId}" );
		}
	}

	/// <summary>
	/// Sell ore for currency.
	/// </summary>
	public async Task SellOre( string oreId, float kg )
	{
		var held = Inventory.GetValueOrDefault( oreId, 0f );
		var actual = MathF.Min( kg, held );
		if ( actual <= 0f ) return;

		var result = await NetworkStorage.CallEndpoint( "sell-ore", new
		{
			ore_id = oreId,
			kg = actual
		} );

		if ( result.HasValue )
			ApplyServerData( result.Value );
	}

	/// <summary>
	/// Read a document directly from a collection (e.g. leaderboard entry).
	/// </summary>
	public async Task<JsonElement?> GetLeaderboardEntry( string steamId )
	{
		return await NetworkStorage.GetDocument( "leaderboard", steamId );
	}

	private void ApplyServerData( JsonElement data )
	{
		// Use JsonHelpers for safe extraction with fallbacks
		PlayerName = JsonHelpers.GetString( data, "playerName", PlayerName );
		Level = JsonHelpers.GetInt( data, "level", Level );
		Currency = JsonHelpers.GetInt( data, "currency", Currency );

		// Use extension methods for complex types
		if ( data.TryGetProperty( "inventory", out var inv ) && inv.ValueKind == JsonValueKind.Object )
		{
			foreach ( var prop in inv.EnumerateObject() )
			{
				if ( prop.Value.ValueKind == JsonValueKind.Number )
					Inventory[prop.Name] = (float)prop.Value.GetDouble();
			}
		}
	}
}
