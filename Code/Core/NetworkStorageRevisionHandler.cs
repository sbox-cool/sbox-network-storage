using System;

namespace Sandbox;

/// <summary>
/// Wires <see cref="NetworkStoragePackageInfo.OnRevisionStatusChanged"/> to concrete actions
/// based on <see cref="NetworkStorage.RevisionSettings"/> configuration.
/// Call <see cref="Initialize"/> once during game startup.
/// </summary>
public static class NetworkStorageRevisionHandler
{
	/// <summary>
	/// Set when <see cref="NetworkStorage.RevisionSettings"/> has block-saves
	/// enabled and the client is running an outdated revision.
	/// Endpoint code MUST check this flag before performing write operations.
	/// </summary>
	public static bool BlockWrites { get; private set; }

	/// <summary>
	/// Game code can assign a handler to receive revision warnings.
	/// Useful for showing custom in-game UI when the built-in console logging is not enough.
	/// </summary>
	public static Action<RevisionStatusInfo> OnShowRevisionWarning;

	private static bool _initialized;

	/// <summary>
	/// Subscribe to revision status change events. Safe to call multiple times —
	/// subsequent calls are no-ops.
	/// </summary>
	public static void Initialize()
	{
		if ( _initialized )
			return;

		_initialized = true;
		NetworkStoragePackageInfo.OnRevisionStatusChanged += HandleRevisionStatusChanged;
	}

	private static void HandleRevisionStatusChanged( RevisionStatusInfo info )
	{
		if ( !info.IsOutdated )
		{
			// Revision is current — clear any stale test/outdated state.
			BlockWrites = false;
			NetworkStorageOutdatedUI.Close();
			return;
		}

		var settings = NetworkStorage.RevisionSettings;
		var showDefaultMessage = settings.ShowDefaultMessage && NetworkStoragePackageInfo.PolicyShowDefaultMessage;

		if ( !showDefaultMessage )
		{
			NetworkStorageOutdatedUI.Close();
		}

		if ( settings.Enabled && showDefaultMessage )
		{
			ShowWarningUI( info );
			OnShowRevisionWarning?.Invoke( info );
		}

		// Show the built-in default UI panel when configured
		if ( settings.Enabled && settings.AutoOpenOnOutdated && showDefaultMessage )
		{
			NetworkStorageOutdatedUI.Open();
		}

		if ( settings.Enabled && settings.AutoRefreshLobbies )
		{
			_ = NetworkStorage.RefreshRevisionLobbyListAsync();
		}

		if ( NetworkStorageRevisionConfig.EnableAutoDisconnectHost && info.GraceExpired )
		{
			Log.Warning( $"[NetworkStorage] Auto-disconnecting: revision grace period expired. {info.Message}" );
			Game.Disconnect();
			return;
		}

		if ( NetworkStorageRevisionConfig.EnableBlockSavesOnOutdated )
		{
			BlockWrites = true;
			Log.Warning( $"[NetworkStorage] Revision is outdated — writes are now blocked. {info.Message}" );
		}
	}

	private static void ShowWarningUI( RevisionStatusInfo info )
	{
		Log.Warning( "╔══════════════════════════════════════════════════════╗" );
		Log.Warning( "║ OUTDATED REVISION                                   ║" );
		Log.Warning( $"║ {(info.Message ?? "").PadRight( 50 )}║" );
		Log.Warning( "╚══════════════════════════════════════════════════════╝" );
	}
}
