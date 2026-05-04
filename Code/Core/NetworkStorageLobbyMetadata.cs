using System;
using System.Collections.Generic;

namespace Sandbox;

/// <summary>
/// Helpers for writing revision metadata to lobbies.
/// Call <see cref="NetworkStorage.BuildLobbyMetadata"/> when creating
/// a lobby to stamp revision tracking keys that other clients can
/// use to discover lobbies running the latest game version.
///
/// Key schema (short names for efficient network transfer):
///   ns_rev      — CurrentRevisionId
///   ns_stale    — 1 if outdated, 0 if current
///   ns_grace_end — Grace period end unix timestamp, or 0
///   ns_mig      — 1 if this lobby supports migration, 0 otherwise
///   ns_mig_rev  — Migration target revision, or 0
///   ns_src      — Source lobby ID (set when created via "Create New Game")
/// </summary>
public static partial class NetworkStorage
{
	/// <summary>
	/// Build a lobby metadata dictionary with revision tracking keys.
	/// Pass the result to your lobby creation API (e.g. lobby.SetMetadata).
	/// </summary>
	/// <param name="migrationRevision">Target revision for migration, or null.</param>
	/// <param name="sourceLobbyId">Previous lobby ID (for "Create New Game" flow), or null.</param>
	public static Dictionary<string, string> BuildLobbyMetadata(
		long? migrationRevision = null,
		string sourceLobbyId = null )
	{
		var meta = new Dictionary<string, string>
		{
			["ns_rev"] = (NetworkStoragePackageInfo.CurrentRevisionId ?? 0).ToString(),
			["ns_stale"] = "0",
			["ns_grace_end"] = "0",
			["ns_mig"] = migrationRevision.HasValue ? "1" : "0",
			["ns_mig_rev"] = (migrationRevision ?? 0).ToString(),
			["ns_src"] = sourceLobbyId ?? "",
		};

		// If revision is outdated, set stale flag and grace end
		if ( NetworkStoragePackageInfo.IsOutdatedRevision )
		{
			meta["ns_stale"] = "1";
		}

		if ( NetworkStoragePackageInfo.PolicyGracePeriodMinutes.HasValue )
		{
			var graceEnd = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
				+ NetworkStoragePackageInfo.PolicyGracePeriodMinutes.Value * 60;
			meta["ns_grace_end"] = graceEnd.ToString();
		}

		return meta;
	}

	/// <summary>
	/// Check whether a lobby's metadata indicates it's running the
	/// latest revision. Useful for filtering stale lobbies.
	/// </summary>
	public static bool IsLobbyOnCurrentRevision( Dictionary<string, string> metadata )
	{
		if ( metadata == null )
			return true;

		if ( !metadata.TryGetValue( "ns_rev", out var revStr ) || !long.TryParse( revStr, out var rev ) )
			return true;

		var current = NetworkStoragePackageInfo.CurrentRevisionId;
		if ( !current.HasValue )
			return true;

		return rev >= current.Value;
	}

	/// <summary>
	/// Check whether a lobby's metadata says it's stale (outdated revision).
	/// </summary>
	public static bool IsLobbyStale( Dictionary<string, string> metadata )
	{
		if ( metadata == null )
			return false;

		return metadata.TryGetValue( "ns_stale", out var stale ) && stale == "1";
	}
}
