using System;
using Sandbox.UI;

namespace Sandbox;

/// <summary>
/// Built-in simple in-game UI for outdated revision warnings.
/// Supports two enforcement modes:
/// - Force Upgrade: Warning with grace countdown, Dismiss button
/// - Allow Continue: Informational with Continue Playing primary
///
/// Uses <see cref="NetworkStorage.RevisionSettings"/> for configuration.
/// Subscribes to <see cref="NetworkStorage.OnRevisionOutdated"/>
/// when <see cref="RevisionSettings.AutoOpenOnOutdated"/> is true.
///
/// The panel is hidden via CSS class toggling (SetClass("hidden", ...)).
/// A parent panel MUST be provided — the UI does not auto-attach.
/// </summary>
public class NetworkStorageOutdatedUI : Panel
{
	private static NetworkStorageOutdatedUI _instance;
	private static bool _hasShownOnce;
	private static long? _shownForRevision; // Track which revision we've shown for
	private static GameObject _autoCreatedScreenPanelObject;
	private static ScreenPanel _autoCreatedScreenPanel;

	/// <summary>
	/// Reset the popup state. Call this when starting a new game session.
	/// This allows the popup to show again even if showPopupOnce is enabled.
	/// </summary>
	public static void ResetState()
	{
		_hasShownOnce = false;
		_shownForRevision = null;
	}

	/// <summary>
	/// The parent panel to use when auto-opening the UI.
	/// If not set, the library will auto-create a ScreenPanel.
	/// </summary>
	public static Panel RootPanel { get; set; }

	private Panel _container;
	private Label _titleLabel;
	private Label _infoLabel;
	private Label _countdownLabel;
	private Panel _buttonsPanel;

	// Mode-specific buttons
	private Button _continueButton;
	private Panel _dividerPanel;
	private Button _createNewButton;
	private Button _joinLobbyButton;
	private Button _dismissButton;

	// Mouse state tracking
	private MouseVisibility _savedMouseVisibility;
	private bool _isShowingCursor;

	/// <summary>True when the panel instance exists and is not hidden.</summary>
	public static bool IsOpen => _instance != null && !_instance.HasClass( "hidden" );

	/// <summary>Fired when the user clicks "Create New Game".</summary>
	public static event Action OnCreateNewGame;

	/// <summary>Fired when the user clicks "Join New Lobby".</summary>
	public static event Action OnJoinNewLobby;

	/// <summary>Fired when the user clicks "Continue Playing" (Allow Continue mode).</summary>
	public static event Action OnContinuePlaying;

	/// <summary>Fired when the user dismisses the popup.</summary>
	public static event Action OnDismiss;

	/// <summary>
	/// Open the outdated revision window.
	/// Uses <paramref name="parent"/> if provided, otherwise falls back to <see cref="RootPanel"/>.
	/// If no parent is available, logs a warning and returns without creating UI.
	/// </summary>
	public static void Open( Panel parent = null )
	{
		if ( !NetworkStorage.RevisionSettings.Enabled )
			return;

		if ( _instance != null )
		{
			// Validate the instance is still valid (might be stale from hot reload)
			if ( !_instance.IsValid || _instance.Parent == null || !_instance.Parent.IsValid )
			{
				_instance = null;
			}
			else
			{
				_instance.ShowPanel();
				return;
			}
		}

		// Clear stale RootPanel
		if ( RootPanel != null && !RootPanel.IsValid )
			RootPanel = null;

		// Use provided parent, registered RootPanel, or auto-create a ScreenPanel
		var targetParent = parent ?? RootPanel ?? GetOrCreateAutoScreenPanel();

		if ( targetParent == null )
		{
			Log.Warning( "[NetworkStorage] Cannot show outdated revision UI: failed to create screen panel." );
			return;
		}

		try
		{
			_instance = new NetworkStorageOutdatedUI();
			_instance.Parent = targetParent;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] Failed to create outdated revision UI: {ex.Message}" );
			_instance = null;
		}
	}

	/// <summary>
	/// Get or create an auto-managed ScreenPanel for the revision UI.
	/// </summary>
	private static Panel GetOrCreateAutoScreenPanel()
	{
		// Clear stale references
		if ( _autoCreatedScreenPanel != null && !_autoCreatedScreenPanel.IsValid )
		{
			_autoCreatedScreenPanel = null;
			_autoCreatedScreenPanelObject = null;
		}

		// Return existing if valid
		if ( _autoCreatedScreenPanel != null && _autoCreatedScreenPanel.IsValid )
			return _autoCreatedScreenPanel.GetPanel();

		try
		{
			// Create a new GameObject with a ScreenPanel
			_autoCreatedScreenPanelObject = new GameObject( true, "NetworkStorageRevisionUI" );
			_autoCreatedScreenPanelObject.Flags = GameObjectFlags.DontDestroyOnLoad | GameObjectFlags.Hidden;
			_autoCreatedScreenPanel = _autoCreatedScreenPanelObject.Components.Create<ScreenPanel>();
			_autoCreatedScreenPanel.ZIndex = 9999; // On top of everything
			return _autoCreatedScreenPanel.GetPanel();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[NetworkStorage] Failed to create auto screen panel: {ex.Message}" );
			return null;
		}
	}

	/// <summary>
	/// Force-open the panel regardless of ShowOnlyOnce.
	/// Used by <see cref="NetworkStorage.TestShowRevisionOutdatedMessage"/>.
	/// </summary>
	internal static void ForceOpen( Panel parent = null )
	{
		_hasShownOnce = false;
		Open( parent );
	}

	/// <summary>
	/// Dismiss the panel if it is visible.
	/// </summary>
	public static void Close()
	{
		if ( _instance != null )
		{
			_instance.HidePanel();
		}
	}

	public override void OnDeleted()
	{
		NetworkStorage.OnRevisionOutdated -= OnRevisionOutdatedEvent;

		// Restore cursor if we were showing it
		if ( _isShowingCursor )
		{
			Mouse.Visibility = _savedMouseVisibility;
			_isShowingCursor = false;
		}

		if ( _instance == this )
			_instance = null;
		base.OnDeleted();
	}

	public override void Tick()
	{
		base.Tick();

		// Aggressively ensure cursor stays visible while panel is shown
		if ( _isShowingCursor && Style.Display == DisplayMode.Flex )
		{
			// Force cursor visible every frame
			if ( Mouse.Visibility != MouseVisibility.Visible )
			{
				Mouse.Visibility = MouseVisibility.Visible;
			}

			// Also ensure we accept input
			AcceptsFocus = true;
			Focus();
		}
	}

	// ── Panel construction ──

	private NetworkStorageOutdatedUI()
	{
		// Apply overlay styles (full screen dark backdrop)
		Style.Position = PositionMode.Absolute;
		Style.Top = 0;
		Style.Left = 0;
		Style.Right = 0;
		Style.Bottom = 0;
		Style.PointerEvents = PointerEvents.All;
		Style.BackgroundColor = new Color( 0, 0, 0, 0.7f );
		Style.ZIndex = 9999;
		Style.Display = DisplayMode.None; // Start hidden
		Style.AlignItems = Align.Center;
		Style.JustifyContent = Justify.Center;
		Style.BackdropFilterBlur = 4;

		AddClass( "ns-outdated-overlay" );

		_container = new Panel();
		_container.Parent = this;
		_container.AddClass( "ns-outdated-container" );
		ApplyContainerStyles( _container );

		_titleLabel = new Label();
		_titleLabel.Parent = _container;
		_titleLabel.AddClass( "ns-outdated-title" );
		_titleLabel.Text = "OUTDATED REVISION";
		ApplyTitleStyles( _titleLabel );

		_infoLabel = new Label();
		_infoLabel.Parent = _container;
		_infoLabel.AddClass( "ns-outdated-info" );
		ApplyInfoStyles( _infoLabel );

		_countdownLabel = new Label();
		_countdownLabel.Parent = _container;
		_countdownLabel.AddClass( "ns-outdated-countdown" );
		ApplyCountdownStyles( _countdownLabel );

		_buttonsPanel = new Panel();
		_buttonsPanel.Parent = _container;
		_buttonsPanel.AddClass( "ns-outdated-buttons" );
		ApplyButtonsPanelStyles( _buttonsPanel );

		// Continue Playing button (Allow Continue mode)
		_continueButton = new Button();
		_continueButton.Parent = _buttonsPanel;
		_continueButton.AddClass( "ns-outdated-continue-btn" );
		_continueButton.Text = "▶ Continue Playing";
		_continueButton.AddEventListener( "onclick", OnContinueClick );
		ApplyContinueButtonStyles( _continueButton );

		// Divider (Allow Continue mode)
		_dividerPanel = new Panel();
		_dividerPanel.Parent = _buttonsPanel;
		_dividerPanel.AddClass( "ns-outdated-divider" );
		ApplyDividerStyles( _dividerPanel );
		var dividerLabel = new Label();
		dividerLabel.Parent = _dividerPanel;
		dividerLabel.Text = "Or update to latest";
		ApplyDividerLabelStyles( dividerLabel );

		// Create New Game button
		_createNewButton = new Button();
		_createNewButton.Parent = _buttonsPanel;
		_createNewButton.AddClass( "ns-outdated-create-btn" );
		_createNewButton.Text = "Create New Session";
		_createNewButton.AddEventListener( "onclick", OnCreateNewClick );
		ApplySecondaryButtonStyles( _createNewButton );

		// Join New Lobby button
		_joinLobbyButton = new Button();
		_joinLobbyButton.Parent = _buttonsPanel;
		_joinLobbyButton.AddClass( "ns-outdated-join-btn" );
		_joinLobbyButton.Text = "Join New Lobby";
		_joinLobbyButton.AddEventListener( "onclick", OnJoinLobbyClick );
		ApplySecondaryButtonStyles( _joinLobbyButton );

		// Dismiss button (Force Upgrade mode only)
		_dismissButton = new Button();
		_dismissButton.Parent = _buttonsPanel;
		_dismissButton.AddClass( "ns-outdated-dismiss-btn" );
		_dismissButton.Text = "Dismiss for now";
		_dismissButton.AddEventListener( "onclick", OnDismissClick );
		ApplyDismissButtonStyles( _dismissButton );

		// Wire events
		NetworkStorage.OnRevisionOutdated += OnRevisionOutdatedEvent;

		// Show immediately if already outdated
		if ( NetworkStoragePackageInfo.IsOutdatedRevision )
		{
			UpdateForMode( NetworkStoragePackageInfo.EnforcementMode );
			UpdateDisplay(
				NetworkStoragePackageInfo.RevisionMessage,
				NetworkStoragePackageInfo.GraceRemainingMinutes,
				NetworkStoragePackageInfo.GraceExpired
			);
			ShowPanel();
		}
	}

	// ── Click handlers ──

	private void OnContinueClick()
	{
		OnContinuePlaying?.Invoke();
		Close();
	}

	private void OnCreateNewClick()
	{
		OnCreateNewGame?.Invoke();
		Close();
	}

	private void OnJoinLobbyClick()
	{
		OnJoinNewLobby?.Invoke();
		Close();
	}

	private void OnDismissClick()
	{
		OnDismiss?.Invoke();
		Close();
	}

	// ── Internal ──

	private void OnRevisionOutdatedEvent( RevisionOutdatedData data )
	{
		if ( !NetworkStorage.RevisionSettings.ShowDefaultMessage || !NetworkStoragePackageInfo.PolicyShowDefaultMessage )
		{
			Close();
			return;
		}

		if ( !NetworkStorage.RevisionSettings.AutoOpenOnOutdated )
			return;

		// Reset _hasShownOnce if the server revision changed (new update published)
		var currentServerRevision = NetworkStoragePackageInfo.ServerCurrentRevision;
		if ( currentServerRevision != null && _shownForRevision != currentServerRevision )
		{
			_hasShownOnce = false;
			_shownForRevision = currentServerRevision;
		}

		// ShowPopupOnce from server policy takes precedence
		var showOnce = NetworkStoragePackageInfo.PolicyShowPopupOnce;
		if ( showOnce && _hasShownOnce )
			return;

		_hasShownOnce = true;

		UpdateForMode( NetworkStoragePackageInfo.EnforcementMode );
		UpdateDisplay(
			data.Message ?? NetworkStoragePackageInfo.RevisionMessage,
			NetworkStoragePackageInfo.GraceRemainingMinutes,
			NetworkStoragePackageInfo.GraceExpired
		);

		ShowPanel();
	}

	/// <summary>
	/// Update the UI layout for the given enforcement mode.
	/// </summary>
	private void UpdateForMode( RevisionEnforcementMode mode )
	{
		var isForceUpgrade = mode == RevisionEnforcementMode.ForceUpgrade;
		var showUpdateOptions = NetworkStoragePackageInfo.PolicyShowUpdateOptions;

		// Apply mode-specific container styling
		if ( isForceUpgrade )
		{
			// Force Upgrade: amber/warning style
			_container.Style.Set( "border", "2px solid #f59e0b" );
			_container.Style.Set( "box-shadow", "0 8px 32px rgba(245, 158, 11, 0.25)" );
			_titleLabel.Style.FontColor = new Color( 0.98f, 0.75f, 0.14f, 1f ); // #fbbf24
		}
		else
		{
			// Allow Continue: blue/info style
			_container.Style.Set( "border", "2px solid #3b82f6" );
			_container.Style.Set( "box-shadow", "0 8px 32px rgba(59, 130, 246, 0.25)" );
			_titleLabel.Style.FontColor = new Color( 0.376f, 0.647f, 0.976f, 1f ); // #60a5fa
		}

		// Update title
		_titleLabel.Text = isForceUpgrade ? "UPDATE REQUIRED" : "NEW VERSION AVAILABLE";

		// Continue Playing: visible only in AllowContinue mode
		_continueButton.Style.Display = isForceUpgrade ? DisplayMode.None : DisplayMode.Flex;

		// Divider: visible only in AllowContinue mode when update options are shown
		_dividerPanel.Style.Display = ( isForceUpgrade || !showUpdateOptions ) ? DisplayMode.None : DisplayMode.Flex;

		// Create/Join buttons: controlled by showUpdateOptions
		_createNewButton.Style.Display = showUpdateOptions ? DisplayMode.Flex : DisplayMode.None;
		_joinLobbyButton.Style.Display = showUpdateOptions ? DisplayMode.Flex : DisplayMode.None;

		// Dismiss: visible only in ForceUpgrade mode
		_dismissButton.Style.Display = isForceUpgrade ? DisplayMode.Flex : DisplayMode.None;
	}

	private void UpdateDisplay( string message, int? graceRemainingMinutes, bool graceExpired )
	{
		if ( _infoLabel == null ) return;

		_infoLabel.Text = message ?? "A new version is available.";

		if ( _countdownLabel != null )
		{
			var isForceUpgrade = NetworkStoragePackageInfo.EnforcementMode == RevisionEnforcementMode.ForceUpgrade;

			if ( isForceUpgrade && graceExpired )
			{
				_countdownLabel.Text = "Grace period expired";
				_countdownLabel.Style.Display = DisplayMode.Flex;
				// Expired red styling
				_countdownLabel.Style.BackgroundColor = new Color( 0.94f, 0.27f, 0.27f, 0.15f );
				_countdownLabel.Style.Set( "border", "1px solid rgba(239, 68, 68, 0.3)" );
				_countdownLabel.Style.FontColor = new Color( 0.99f, 0.65f, 0.65f, 1f ); // #fca5a5
			}
			else if ( isForceUpgrade && graceRemainingMinutes.HasValue && graceRemainingMinutes.Value > 0 )
			{
				var consequence = NetworkStoragePackageInfo.PolicyPostGraceAction == "block_all"
					? "this session will expire"
					: "saving will be disabled";
				_countdownLabel.Text = $"Grace period: {graceRemainingMinutes.Value} minute(s) remaining\n(After that, {consequence})";
				_countdownLabel.Style.Display = DisplayMode.Flex;
				// Normal amber styling
				_countdownLabel.Style.BackgroundColor = new Color( 0.96f, 0.62f, 0.04f, 0.15f );
				_countdownLabel.Style.Set( "border", "1px solid rgba(245, 158, 11, 0.3)" );
				_countdownLabel.Style.FontColor = new Color( 0.98f, 0.75f, 0.14f, 1f ); // #fbbf24
			}
			else
			{
				_countdownLabel.Style.Display = DisplayMode.None;
			}
		}
	}

	private void ShowPanel()
	{
		Style.Display = DisplayMode.Flex;

		// Save current mouse state and show cursor
		if ( !_isShowingCursor )
		{
			_savedMouseVisibility = Mouse.Visibility;
			_isShowingCursor = true;
		}
		Mouse.Visibility = MouseVisibility.Visible;
	}

	private void HidePanel()
	{
		Style.Display = DisplayMode.None;

		// Restore original mouse state
		if ( _isShowingCursor )
		{
			Mouse.Visibility = _savedMouseVisibility;
			_isShowingCursor = false;
		}
	}

	// ── Style Helpers (inline styles for library portability) ──

	private static void ApplyContainerStyles( Panel p )
	{
		p.Style.BackgroundColor = new Color( 0.08f, 0.09f, 0.14f, 0.98f ); // Darker, slightly transparent
		p.Style.Set( "border-radius", "16px" );
		p.Style.Set( "padding", "32px 40px" );
		p.Style.MaxWidth = 480;
		p.Style.MinWidth = 380;
		p.Style.Width = Length.Percent( 90 );
		p.Style.Display = DisplayMode.Flex;
		p.Style.FlexDirection = FlexDirection.Column;
		p.Style.Set( "gap", "20px" );
		p.Style.Set( "box-shadow", "0 25px 50px -12px rgba(0, 0, 0, 0.5)" );
	}

	private static void ApplyTitleStyles( Label l )
	{
		l.Style.FontSize = 22;
		l.Style.FontWeight = 800;
		l.Style.TextAlign = TextAlign.Center;
		l.Style.TextTransform = TextTransform.Uppercase;
		l.Style.Set( "letter-spacing", "2px" );
		l.Style.FontColor = new Color( 0.98f, 0.75f, 0.14f, 1f ); // #fbbf24 (amber default)
		l.Style.Set( "margin-bottom", "8px" );
	}

	private static void ApplyInfoStyles( Label l )
	{
		l.Style.FontSize = 15;
		l.Style.FontColor = new Color( 0.82f, 0.86f, 0.92f, 1f ); // Softer white
		l.Style.TextAlign = TextAlign.Center;
		l.Style.Set( "line-height", "2" );
		l.Style.Set( "padding", "4px 12px" );
	}

	private static void ApplyCountdownStyles( Label l )
	{
		l.Style.FontSize = 14;
		l.Style.TextAlign = TextAlign.Center;
		l.Style.Set( "padding", "16px 20px" );
		l.Style.Set( "border-radius", "10px" );
		l.Style.Set( "line-height", "1.9" );
		l.Style.Set( "white-space", "pre-line" );
		// Default amber style (force upgrade mode)
		l.Style.BackgroundColor = new Color( 0.96f, 0.62f, 0.04f, 0.12f );
		l.Style.Set( "border", "1px solid rgba(245, 158, 11, 0.25)" );
		l.Style.FontColor = new Color( 0.98f, 0.78f, 0.2f, 1f );
	}

	private static void ApplyButtonsPanelStyles( Panel p )
	{
		p.Style.Display = DisplayMode.Flex;
		p.Style.FlexDirection = FlexDirection.Column;
		p.Style.Set( "gap", "12px" );
		p.Style.Set( "margin-top", "8px" );
	}

	private static void ApplyContinueButtonStyles( Button b )
	{
		b.Style.Set( "background", "linear-gradient(135deg, #3b82f6, #1d4ed8)" );
		b.Style.FontColor = Color.White;
		b.Style.Set( "border", "none" );
		b.Style.Set( "border-radius", "10px" );
		b.Style.Set( "padding", "16px 28px" );
		b.Style.FontSize = 16;
		b.Style.FontWeight = 700;
		b.Style.Set( "cursor", "pointer" );
		b.Style.TextAlign = TextAlign.Center;
		b.Style.Set( "text-shadow", "0 1px 2px rgba(0,0,0,0.2)" );
	}

	private static void ApplyDividerStyles( Panel p )
	{
		p.Style.Display = DisplayMode.Flex;
		p.Style.AlignItems = Align.Center;
		p.Style.Set( "gap", "16px" );
		p.Style.Set( "margin", "8px 0" );
	}

	private static void ApplyDividerLabelStyles( Label l )
	{
		l.Style.FontColor = new Color( 0.45f, 0.52f, 0.62f, 1f ); // Slightly brighter
		l.Style.FontSize = 13;
		l.Style.Set( "white-space", "nowrap" );
		l.Style.Set( "line-height", "1.8" );
	}

	private static void ApplySecondaryButtonStyles( Button b )
	{
		b.Style.BackgroundColor = new Color( 0.45f, 0.52f, 0.62f, 0.12f );
		b.Style.FontColor = new Color( 0.85f, 0.88f, 0.92f, 1f );
		b.Style.Set( "border", "1px solid rgba(148, 163, 184, 0.2)" );
		b.Style.Set( "border-radius", "10px" );
		b.Style.Set( "padding", "14px 24px" );
		b.Style.FontSize = 14;
		b.Style.FontWeight = 600;
		b.Style.Set( "cursor", "pointer" );
		b.Style.TextAlign = TextAlign.Center;
	}

	private static void ApplyDismissButtonStyles( Button b )
	{
		b.Style.BackgroundColor = Color.Transparent;
		b.Style.FontColor = new Color( 0.52f, 0.58f, 0.68f, 1f );
		b.Style.Set( "border", "none" );
		b.Style.Set( "padding", "12px" );
		b.Style.FontSize = 13;
		b.Style.Set( "cursor", "pointer" );
		b.Style.TextAlign = TextAlign.Center;
		b.Style.Set( "line-height", "1.8" );
	}
}
