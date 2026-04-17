// ============================================================
// BasicSetup.cs -- Configure Network Storage and call an endpoint
// Copy this into your game project's Code/ directory.
// ============================================================

namespace Sandbox;

/// <summary>
/// One-time configuration for the Network Storage library.
/// Create your project and API keys at sboxcool.com.
///
/// Call MyNetStorageConfig.Initialize() once at game startup
/// before any systems that need network storage.
/// </summary>
public static class MyNetStorageConfig
{
	// Get these from your sboxcool.com dashboard
	public const string ProjectId = "your_project_id";
	public const string ApiKey = "sbox_ns_your_public_key";

	public static void Initialize()
	{
		NetworkStorage.Configure( ProjectId, ApiKey );
		Log.Info( "[MyGame] Network Storage configured" );
	}
}

/// <summary>
/// Example component that loads data on start.
/// Attach to your player GameObject.
/// </summary>
public class BasicNetworkExample : Component
{
	protected override void OnStart()
	{
		if ( IsProxy ) return;

		// Configure the library (do this once)
		if ( !NetworkStorage.IsConfigured )
			MyNetStorageConfig.Initialize();

		_ = LoadDataAsync();
	}

	private async Task LoadDataAsync()
	{
		// Call an endpoint (no input = GET, with input = POST)
		var result = await NetworkStorage.CallEndpoint( "load-player" );

		if ( result.HasValue )
		{
			var data = result.Value;

			// Use JsonHelpers for safe extraction with fallbacks
			var name = JsonHelpers.GetString( data, "playerName", "Unknown" );
			var level = JsonHelpers.GetInt( data, "level", 1 );
			var currency = JsonHelpers.GetInt( data, "currency", 0 );

			Log.Info( $"Loaded: {name} | Level {level} | {currency} coins" );
		}
		else
		{
			Log.Warning( "Failed to load player data" );
		}

		// Call an endpoint with input (POST)
		var mineResult = await NetworkStorage.CallEndpoint( "mine-ore", new
		{
			ore_id = "iron",
			kg = 5.0f
		} );

		if ( mineResult.HasValue )
			Log.Info( "Mining reported successfully" );
	}
}
