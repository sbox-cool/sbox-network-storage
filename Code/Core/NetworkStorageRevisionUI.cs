using System;
using System.Threading.Tasks;
using Sandbox.UI;

namespace Sandbox;

public static partial class NetworkStorage
{
	/// <summary>
	/// Open the outdated revision message panel.
	/// Requires a parent panel to attach to. If parent is null,
	/// a warning is logged and no UI is created, but the
	/// <see cref="OnRevisionOutdated"/> hook still fires.
	/// </summary>
	public static void OpenRevisionOutdatedMessage( Panel parent = null )
	{
		NetworkStorageOutdatedUI.Open( parent );
	}

	/// <summary>
	/// Close the outdated revision message panel if it is open.
	/// </summary>
	public static void CloseRevisionOutdatedMessage()
	{
		NetworkStorageOutdatedUI.Close();
	}

	/// <summary>
	/// Toggle the outdated revision message panel open/closed.
	/// </summary>
	public static void ToggleRevisionOutdatedMessage( Panel parent = null )
	{
		if ( NetworkStorageOutdatedUI.IsOpen )
		{
			NetworkStorageOutdatedUI.Close();
		}
		else
		{
			NetworkStorageOutdatedUI.Open( parent );
		}
	}

	/// <summary>
	/// Refresh the lobby list to find lobbies running the latest revision.
	/// This is a stub — game code should override with actual lobby query logic
	/// by subscribing to <see cref="OnRefreshLobbyListRequested"/>.
	/// </summary>
	public static async Task RefreshRevisionLobbyListAsync()
	{
		// Fire the request — game code should handle this
		OnRefreshLobbyListRequested?.Invoke();

		// Default implementation: log and return
		await Task.CompletedTask;
	}

	/// <summary>
	/// Fired when <see cref="RefreshRevisionLobbyListAsync"/> is called.
	/// Game code should subscribe and perform the actual lobby refresh.
	/// </summary>
	public static event Action OnRefreshLobbyListRequested;

	// ── Test / Debug APIs ──

	/// <summary>
	/// Simulate an outdated revision detection for testing.
	/// Creates a fake <see cref="RevisionOutdatedData"/> with
	/// <c>Reason = ManualTest</c> and fires the full UI pipeline:
	/// - Updates <see cref="NetworkStoragePackageInfo"/> state
	/// - Fires <see cref="OnRevisionOutdated"/> for custom hooks
	/// - Opens the default message panel (if configured)
	///
	/// Does NOT require the backend to report a mismatch.
	/// Bypasses <see cref="RevisionSettings.ShowOnlyOnce"/>.
	/// Logs clearly that this is a test.
	/// </summary>
	public static void TestShowRevisionOutdatedMessage( Panel parent = null )
	{
		Log.Warning( "[NetworkStorage] TEST: Simulating outdated revision detection (no backend required)" );

		var data = new RevisionOutdatedData
		{
			CurrentRevisionId = NetworkStoragePackageInfo.CurrentRevisionId ?? 1,
			LatestRevisionId = (NetworkStoragePackageInfo.CurrentRevisionId ?? 1) + 1,
			RevisionOutdated = true,
			GraceSeconds = 120,
			GraceEndsAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120,
			ServerUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
			Source = "manual-test",
			Reason = RevisionOutdatedReason.ManualTest,
		};

		// Update package info state
		NetworkStoragePackageInfo.UpdateFromRevisionBlock( data );

		// Fire the event for custom hooks
		OnRevisionOutdated?.Invoke( data );

		// Open the default UI
		NetworkStorageOutdatedUI.ForceOpen( parent );
	}

	/// <summary>
	/// Fire the <see cref="OnRevisionOutdated"/> event with test data.
	/// Does NOT show the default UI — only fires the event so game code
	/// can test custom hooks without interference from the default panel.
	/// Logs clearly that this is a test.
	/// </summary>
	public static void TestFireRevisionOutdated()
	{
		Log.Warning( "[NetworkStorage] TEST: Firing OnRevisionOutdated event only (no UI)" );

		var data = new RevisionOutdatedData
		{
			CurrentRevisionId = NetworkStoragePackageInfo.CurrentRevisionId ?? 1,
			LatestRevisionId = (NetworkStoragePackageInfo.CurrentRevisionId ?? 1) + 1,
			RevisionOutdated = true,
			GraceSeconds = 120,
			GraceEndsAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120,
			ServerUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
			Source = "manual-test",
			Reason = RevisionOutdatedReason.ManualTest,
		};

		// Fire the event only — no UI, no package info update
		OnRevisionOutdated?.Invoke( data );
	}
}
