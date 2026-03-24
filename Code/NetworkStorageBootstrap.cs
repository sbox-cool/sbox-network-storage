using System.Threading.Tasks;

namespace Sandbox;

/// <summary>
/// Checks whether the editor assembly loaded after the Code assembly.
/// Called from NetworkStorage.AutoConfigure() on first use.
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

		var editorLoaded = TypeLibrary.GetType( "SetupWindow" ) != null;

		if ( !editorLoaded )
		{
			Log.Warning( "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" );
			Log.Warning( "[Network Storage] Restart the editor to enable the Network Storage menu." );
			Log.Warning( "[Network Storage] Go to: Editor → Network Storage → Setup" );
			Log.Warning( "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" );
		}
	}
}
