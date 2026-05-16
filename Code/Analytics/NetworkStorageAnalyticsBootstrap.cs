namespace Sandbox;

/// <summary>
/// Extra lifecycle hooks for managed Player Analytics. This makes the runtime
/// start even when project code configures Network Storage before the scene is ready.
/// </summary>
internal static class NetworkStorageAnalyticsBootstrap
{
	[Event( "network.client.active" )]
	private static void OnNetworkClientActive( Connection connection )
	{
		NetworkStorageAnalyticsRuntime.EnsureCreated( "network.client.active" );
	}

	[Event( "scene.loaded" )]
	private static void OnSceneLoaded()
	{
		NetworkStorageAnalyticsRuntime.EnsureCreated( "scene.loaded" );
	}
}
