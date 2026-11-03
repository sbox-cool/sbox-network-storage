using Sandbox;

[TestClass]
public class SyncToolReachabilityTests
{
	// IsGameClientReachable defers to Http.IsAllowed, which only the engine can answer. The unit tests cover
	// the inputs that never reach the engine check; the native run covers the rest.
	[TestMethod]
	public void ReachabilityIsUnknownWhenBaseUrlIsEmpty()
	{
		Assert.IsNull( SyncToolConfig.IsGameClientReachable( "", "v3" ) );
		Assert.IsNull( SyncToolConfig.IsGameClientReachable( "   ", "v3" ) );
		Assert.IsNull( SyncToolConfig.IsGameClientReachable( null, "v3" ) );
	}

	[TestMethod]
	public void ReachabilityIsUnknownWhenBaseUrlCannotBeBuilt()
	{
		Assert.IsNull( SyncToolConfig.IsGameClientReachable( "not a url", "v3" ) );
	}
}
