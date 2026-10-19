// ============================================================
// GameValuesExample.cs — Fetch and parse game configuration
// Copy this into your game project's Code/ directory.
// ============================================================

using System.Text.Json;

namespace Sandbox;

/// <summary>
/// Example: load game values (groups + tables) from Network Storage.
///
/// Matching authoring (owner console "Game values" or the Sync Tool once
/// game_values collections are served; see server docs/game-values.md):
///
///   items:
///     - id: progression
///       type: group
///       entries:
///         xp_per_level: 100
///         starting_currency: 500
///     - id: ore_types
///       type: table
///       columns:
///         - { key: id, type: string }
///         - { key: name, type: string }
///         - { key: tier, type: number }
///         - { key: value_per_kg, type: number }
///       rows:
///         - { id: iron, name: Iron, tier: 1, value_per_kg: 2.5 }
///
/// The response has "groups.<id>.values" for groups and "tables.<id>.rows"
/// (objects keyed by column) for tables. Call GameConfig.Load() once at
/// startup. Only public keys belong in game code.
/// </summary>
public static class GameConfig
{
	// ── Parsed data ──
	/// <summary>True only after a successful load that found every expected group and table.</summary>
	public static bool IsLoaded { get; private set; }
	public static Dictionary<string, OreInfo> OreTypes { get; private set; } = new();
	public static int StartingCurrency { get; private set; } = 500;
	public static int XpPerLevel { get; private set; } = 100;

	// ── Data records ──
	public record OreInfo( string Id, string Name, int Tier, float ValuePerKg );

	/// <summary>
	/// Fetch game values from the server and parse them. On failure, IsLoaded
	/// stays false and a warning is logged; callers decide whether to retry or
	/// block on it. Compiled defaults are not server-confirmed values.
	/// </summary>
	public static async Task Load()
	{
		var response = await NetworkStorage.GetGameValues();
		if ( !response.HasValue )
		{
			IsLoaded = false;
			Log.Warning( "[GameConfig] Game values request failed; values are not loaded from the server." );
			return;
		}

		var data = response.Value;

		// ── Group: groups.progression.values ──
		if ( !data.TryGetProperty( "groups", out var groups )
			|| !groups.TryGetProperty( "progression", out var progression )
			|| !progression.TryGetProperty( "values", out var prog ) )
		{
			IsLoaded = false;
			Log.Warning( "[GameConfig] Response has no groups.progression.values; check the published game values." );
			return;
		}

		XpPerLevel = JsonHelpers.GetInt( prog, "xp_per_level", XpPerLevel );
		StartingCurrency = JsonHelpers.GetInt( prog, "starting_currency", StartingCurrency );

		// ── Table: tables.ore_types.rows (object rows) ──
		if ( !data.TryGetProperty( "tables", out var tables )
			|| !tables.TryGetProperty( "ore_types", out var oreTable ) )
		{
			IsLoaded = false;
			Log.Warning( "[GameConfig] Response has no tables.ore_types; check the published game values." );
			return;
		}

		OreTypes.Clear();
		var rows = oreTable.ReadList( "rows", row => new OreInfo(
			JsonHelpers.GetString( row, "id", "" ),
			JsonHelpers.GetString( row, "name", "" ),
			JsonHelpers.GetInt( row, "tier", 1 ),
			JsonHelpers.GetFloat( row, "value_per_kg", 1f )
		) );

		foreach ( var ore in rows )
			OreTypes[ore.Id] = ore;

		IsLoaded = true;
		Log.Info( $"[GameConfig] Loaded: {OreTypes.Count} ore types, xp/level={XpPerLevel}" );
	}
}
