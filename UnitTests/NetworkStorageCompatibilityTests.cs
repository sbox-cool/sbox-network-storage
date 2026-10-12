using Sandbox;
using System.Collections.Generic;

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
		Assert.AreEqual( "warning", EditorCompatibility.Evaluate( "unknown", "1.1.0", 100, 200, null, "0.5.0", all, null ).Level );
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
}
