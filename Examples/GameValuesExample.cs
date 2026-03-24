// ============================================================
// GameValuesExample.cs — Fetch and parse game configuration
// Copy this into your game project's Code/ directory.
// ============================================================

using System.Text.Json;

namespace Sandbox;

/// <summary>
/// Example: Load game values (constants + tables) from Network Storage.
///
/// Game values are defined in your sboxcool.com dashboard as constants
/// and tables on a "global" collection. The API returns them merged
/// into a single JSON response.
///
/// Call GameConfig.Load() once at startup, then access
/// GameConfig.OreTypes, GameConfig.Constants, etc.
/// </summary>
public static class GameConfig
{
	// ── Parsed data ──
	public static bool IsLoaded { get; private set; }
	public static Dictionary<string, OreInfo> OreTypes { get; private set; } = new();
	public static int StartingCurrency { get; private set; } = 500;
	public static int XpPerLevel { get; private set; } = 100;

	// ── Data records ──
	public record OreInfo( string Id, string Name, int Tier, float ValuePerKg );

	/// <summary>
	/// Fetch game values from the server and parse them.
	/// </summary>
	public static async Task Load()
	{
		var response = await NetworkStorage.GetGameValues();
		if ( !response.HasValue )
		{
			Log.Warning( "Failed to load game values — using defaults" );
			return;
		}

		var data = response.Value;

		// ── Parse constants (flat key-value groups) ──
		// Constants are organized in groups on the dashboard.
		// They arrive as top-level properties in the response.
		if ( data.TryGetProperty( "constants", out var constants ) )
		{
			// Each group is a nested object
			if ( constants.TryGetProperty( "progression", out var prog ) )
			{
				XpPerLevel = JsonHelpers.GetInt( prog, "xp_per_level", 100 );
				StartingCurrency = JsonHelpers.GetInt( prog, "starting_currency", 500 );
			}
		}

		// ── Parse tables (arrays of rows) ──
		// Tables are arrays of objects defined on the dashboard.
		if ( data.TryGetProperty( "tables", out var tables ) )
		{
			if ( tables.TryGetProperty( "ore_types", out var oreTable ) )
			{
				// Use the ReadList extension for clean parsing
				var rows = oreTable.ReadList( "rows", row => new OreInfo(
					JsonHelpers.GetString( row, "id", "" ),
					JsonHelpers.GetString( row, "name", "" ),
					JsonHelpers.GetInt( row, "tier", 1 ),
					JsonHelpers.GetFloat( row, "value_per_kg", 1f )
				) );

				foreach ( var ore in rows )
					OreTypes[ore.Id] = ore;
			}
		}

		IsLoaded = true;
		Log.Info( $"[GameConfig] Loaded: {OreTypes.Count} ore types, xp/level={XpPerLevel}" );
	}
}
