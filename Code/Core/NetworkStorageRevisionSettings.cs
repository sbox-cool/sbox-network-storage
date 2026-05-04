using System;

namespace Sandbox;

/// <summary>
/// How the server enforces revision updates.
/// </summary>
public enum RevisionEnforcementMode
{
	/// <summary>Grace period countdown, then block saves/all requests.</summary>
	ForceUpgrade,

	/// <summary>Players can keep playing indefinitely with a notification.</summary>
	AllowContinue
}


/// <summary>
/// Public settings for the NetworkStorage revision/outdated-session system.
///
/// Usage:
///   NetworkStorage.RevisionSettings.ShowDefaultMessage = false;
///
/// All settings are static and should be configured once during game startup
/// (e.g. in your game's initialization code).
/// </summary>
public class RevisionSettings
{
	/// <summary>
	/// Enable revision-outdated detection and hook firing.
	/// When false, no detection occurs and no events fire.
	/// Default: true
	/// </summary>
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Show the built-in default "OUTDATED REVISION" message panel
	/// when the revision becomes outdated. Set to true to enable
	/// the default UI, or leave false and use your own via <see cref="NetworkStorage.OnRevisionOutdated"/>.
	/// Default: false
	/// </summary>
	public bool ShowDefaultMessage { get; set; } = false;

	/// <summary>
	/// Only show the default message once per session.
	/// If false, the panel re-appears every time <c>load-profile</c>
	/// reports the revision is outdated.
	/// Default: true
	/// </summary>
	public bool ShowOnlyOnce { get; set; } = true;

	/// <summary>
	/// Grace period before the outdated warning appears.
	/// Use <see cref="TimeSpan.Zero"/> to show immediately.
	/// Default: TimeSpan.Zero
	/// </summary>
	public TimeSpan GracePeriod { get; set; } = TimeSpan.Zero;

	/// <summary>
	/// Auto-refresh the lobby list when the revision is outdated.
	/// Default: false
	/// </summary>
	public bool AutoRefreshLobbies { get; set; } = false;

	/// <summary>
	/// Interval in seconds between lobby list refreshes.
	/// Only used when <see cref="AutoRefreshLobbies"/> is true.
	/// Default: 10
	/// </summary>
	public float LobbyRefreshInterval { get; set; } = 10f;

	/// <summary>
	/// Allow the SPACE key to close the default message panel.
	/// Default: true
	/// </summary>
	public bool AllowSpaceToClose { get; set; } = true;

	/// <summary>
	/// Automatically open the default message panel when the
	/// revision becomes outdated. Set to false if you want to
	/// control when the UI appears via <see cref="NetworkStorage.OpenRevisionOutdatedMessage"/>.
	/// Default: true
	/// </summary>
	public bool AutoOpenOnOutdated { get; set; } = true;
}

public static partial class NetworkStorage
{
	/// <summary>
	/// Public settings for the revision/outdated-session system.
	/// Configure these once during game startup.
	/// </summary>
	public static RevisionSettings RevisionSettings { get; } = new RevisionSettings();
}
