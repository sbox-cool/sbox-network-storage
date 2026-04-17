using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Checks whether the editor assembly loaded after the Code assembly.
/// Called from NetworkStorage.AutoConfigure() on first use.
///
/// Only warns in editor context when auto-configure fails -- at runtime
/// (published games) the client auto-configures from the credentials JSON
/// and the editor is not available, so the warning is suppressed.
/// </summary>
internal static class NetworkStorageBootstrap
{
	private static bool _checked;

	internal static void CheckEditorOnce()
	{
		if ( _checked ) return;
		_checked = true;

		_ = CheckEditorAsync();
	}

	private static async Task CheckEditorAsync()
	{
		await Task.Delay( 2000 );

		// If the client configured successfully (from credentials JSON or manual call),
		// no editor is needed -- skip the warning. This is the normal path for published games.
		if ( NetworkStorage.IsConfigured ) return;

		// Only show editor hint when the editor assembly is expected but didn't load
		var editorLoaded = TypeLibrary.GetType( "SetupWindow" ) != null;

		if ( !editorLoaded )
		{
			Log.Warning( "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" );
			Log.Warning( "[Network Storage] Not configured." );
			Log.Warning( "[Network Storage] If running in editor: restart to enable the Network Storage menu, then go to Editor → Network Storage → Setup" );
			Log.Warning( "[Network Storage] If running published: ensure Assets/network-storage.credentials.json exists with projectId and publicKey" );
			Log.Warning( "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" );
		}
	}
}
