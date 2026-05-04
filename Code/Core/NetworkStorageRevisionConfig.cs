namespace Sandbox;

/// <summary>
/// Opt-in configuration for revision enforcement features.
/// All settings default to false — game code must explicitly opt in.
/// </summary>
public static class NetworkStorageRevisionConfig
{
	/// <summary>Show built-in warning UI when an outdated revision is detected.</summary>
	public static bool EnableRevisionWarningUI { get; set; }

	/// <summary>
	/// Auto-disconnect the local client when the revision grace period has expired.
	/// DANGEROUS: test thoroughly before enabling in production.
	/// </summary>
	public static bool EnableAutoDisconnectHost { get; set; }

	/// <summary>
	/// Set <see cref="NetworkStorageRevisionHandler.BlockWrites"/> when running
	/// an outdated revision. Endpoint code should check this flag before performing writes.
	/// DANGEROUS: may cause data loss if callers ignore the flag.
	/// </summary>
	public static bool EnableBlockSavesOnOutdated { get; set; }
	
	// ── Built-in Outdated Revision Window ──
	
	/// <summary>Enable the entire outdated revision notification UI system.</summary>
	public static bool EnableOutdatedUI { get; set; }
	
	/// <summary>Show the built-in default warning window.</summary>
	public static bool ShowDefaultWindow { get; set; }
	
	/// <summary>Only show the warning once per session.</summary>
	public static bool ShowOnlyOnce { get; set; } = true;
	
	/// <summary>Grace period override (seconds) before showing the warning. 0 = show immediately.</summary>
	public static float GracePeriod { get; set; }
	
	/// <summary>Auto-refresh the lobby list when revision is outdated.</summary>
	public static bool AutoRefreshLobbyList { get; set; }
	
	/// <summary>Interval in seconds between lobby list refreshes.</summary>
	public static float LobbyRefreshInterval { get; set; } = 10f;
}
