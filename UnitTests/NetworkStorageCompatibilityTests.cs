using Sandbox;
using System.Collections.Generic;
using System.IO;

[TestClass]
public class NetworkStorageCompatibilityTests
{
	[TestMethod]
	public void ApplyCompatibilityMetadata_FiresOnceAndIgnoresMissingOrUnknownValues()
	{
		var seen = new List<string>();
		void Handler( string status ) => seen.Add( status );
		NetworkStorage.OnLibraryStatusChanged += Handler;
		try
		{
			NetworkStorage.ApplyCompatibilityMetadata( null, "supported" );
			seen.Clear();

			NetworkStorage.ApplyCompatibilityMetadata( "0.5.0", "outdated" );
			NetworkStorage.ApplyCompatibilityMetadata( "0.5.0", "outdated" );
			NetworkStorage.ApplyCompatibilityMetadata( null, null );
			NetworkStorage.ApplyCompatibilityMetadata( "", "not-a-status" );

			CollectionAssert.AreEqual( new[] { "outdated" }, seen );
			Assert.AreEqual( "outdated", NetworkStorage.LibraryStatus );
			Assert.AreEqual( "0.5.0", NetworkStorage.ServerVersion );
		}
		finally
		{
			NetworkStorage.OnLibraryStatusChanged -= Handler;
		}
	}

	[TestMethod]
	public void TryReadCompatibilityError_ReturnsTheServerMessageForLibraryErrorsOnly()
	{
		var body = "{\"ok\":false,\"error\":{\"code\":\"LIBRARY_BLOCKED\",\"message\":\"Advisory: X. Update the library and republish the game.\"}}";
		Assert.IsTrue( NetworkStorage.TryReadCompatibilityError( body, out var code, out var message ) );
		Assert.AreEqual( "LIBRARY_BLOCKED", code );
		Assert.AreEqual( "Advisory: X. Update the library and republish the game.", message );

		Assert.IsFalse( NetworkStorage.TryReadCompatibilityError( "{\"error\":{\"code\":\"RATE_LIMITED\",\"message\":\"slow down\"}}", out _, out _ ) );
		Assert.IsFalse( NetworkStorage.TryReadCompatibilityError( "<html>upgrade</html>", out _, out _ ) );
		Assert.IsFalse( NetworkStorage.TryReadCompatibilityError( "", out _, out _ ) );
	}

	[TestMethod]
	public void HasCapability_IsTrueUntilAServerAdvertisesItsCapabilities()
	{
		// A server without capability metadata (0.4.0) keeps every existing feature.
		Assert.IsTrue( NetworkStorage.HasCapability( "records" ) );
		Assert.IsFalse( NetworkStorage.HasCapability( "" ) );
	}

	[TestMethod]
	public void EditorEvaluate_MapsEveryStatusToItsPrompt()
	{
		var all = new[] { "records", "endpoints", "queries", "game-values", "auth-sessions", "package-sync" };

		Assert.AreEqual( "error", EditorCompatibility.Evaluate( "blocked", "1.1.0", 100, 100, null, "0.5.0", all, null ).Level );
		Assert.AreEqual( "error", EditorCompatibility.Evaluate( "unsupported", "1.1.0", 100, 100, null, "0.5.0", all, "1.2.0" ).Level );

		var deprecated = EditorCompatibility.Evaluate( "deprecated", "1.1.0", 100, 100, "2027-04-01", "0.5.0", all, null );
		Assert.AreEqual( "modal", deprecated.Level );
		StringAssert.Contains( deprecated.Message, "2027-04-01" );

		Assert.AreEqual( "warning", EditorCompatibility.Evaluate( "outdated", "1.1.0", 100, 100, null, "0.5.0", all, null ).Level );
		// A server that reports no status (0.4.0) says nothing about this library: only the newer package is news.
		var unknownNewer = EditorCompatibility.Evaluate( "unknown", "1.1.0", 100, 200, null, "0.5.0", all, null );
		Assert.AreEqual( "info", unknownNewer.Level );
		Assert.IsFalse( unknownNewer.Message.Contains( "outdated" ) );
		Assert.IsNull( EditorCompatibility.Evaluate( "supported", "1.1.0", 100, 100, null, "0.5.0", all, null ) );
		// No server metadata at all (older server, offline) stays silent.
		Assert.IsNull( EditorCompatibility.Evaluate( "unknown", "1.1.0", null, null, null, null, null, null ) );
	}

	[TestMethod]
	public void EditorServerWarning_NamesTheFeaturesTheServerLacks()
	{
		var warning = EditorCompatibility.ServerWarning( "0.4.0", new[] { "records", "endpoints" } );
		StringAssert.Contains( warning, "queries" );
		StringAssert.Contains( warning, "game values" );
		StringAssert.Contains( warning, "auth sessions" );
		Assert.IsNull( EditorCompatibility.ServerWarning( null, null ) );
	}

	[TestMethod]
	public void EditorServerWarning_FlagsOldReleasesButNotUnversionedSourceBuilds()
	{
		StringAssert.Contains( EditorCompatibility.ServerWarning( "0.3.9", null ), "older than" );
		// A server built from source reports the placeholder 0.1.0-dev, not its real feature level.
		Assert.IsNull( EditorCompatibility.ServerWarning( "0.1.0-dev", null ) );
		Assert.IsNull( EditorCompatibility.ServerWarning( "0.5.4", null ) );
	}

	[TestMethod]
	public void EditorLocalRevision_ReadsThePackageMarkerButNeverAGitCheckout()
	{
		var root = Directory.CreateTempSubdirectory( "ns-revision-" ).FullName;
		try
		{
			var library = Path.Combine( root, "Libraries", "sboxcool.network-storage" );
			Directory.CreateDirectory( library );
			File.WriteAllText( Path.Combine( library, ".version" ), "1.0.309133\n" );
			Assert.AreEqual( 309133L, EditorCompatibility.ReadLocalRevision( root ) );

			// A clone carries the same committed .version, which does not track the published package.
			Directory.CreateDirectory( Path.Combine( library, ".git" ) );
			Assert.IsNull( EditorCompatibility.ReadLocalRevision( root ) );

			// A submodule or worktree has a .git file instead of a directory.
			Directory.Delete( Path.Combine( library, ".git" ) );
			File.WriteAllText( Path.Combine( library, ".git" ), "gitdir: ../../.git/modules/network-storage" );
			Assert.IsNull( EditorCompatibility.ReadLocalRevision( root ) );
		}
		finally
		{
			Directory.Delete( root, true );
		}
	}

	[TestMethod]
	public void EditorEvaluate_GitCheckoutGetsNoRevisionNotice()
	{
		var all = new[] { "records", "endpoints", "queries", "game-values", "auth-sessions", "package-sync" };
		// Without a local revision (git checkout) a newer published package is not news.
		Assert.IsNull( EditorCompatibility.Evaluate( "supported", "1.2.0", null, 423774, null, "0.5.0", all, null ) );
		// A server that says the library is outdated still says so, just without the revision line.
		var outdated = EditorCompatibility.Evaluate( "outdated", "1.2.0", null, 423774, null, "0.5.0", all, null );
		Assert.AreEqual( "warning", outdated.Level );
		Assert.IsFalse( outdated.Message.Contains( "revision" ) );
	}
}
