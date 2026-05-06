using System;

namespace Sandbox;

public static partial class NetworkStorage
{
	/// <summary>
	/// Determines the Network Storage client type from the current s&amp;box runtime.
	/// Editor-launched play sessions can report <see cref="Game.IsEditor"/> as false, so this
	/// also checks the lower-level application flag and active project state.
	/// </summary>
	internal static string GetClientType()
	{
		if ( IsEditorRuntime() )
			return "editor";
		if ( IsDedicatedServerProcess )
			return "dedicated";
		return "game";
	}

	/// <summary>
	/// True when running in the editor, an editor-spawned local instance, or against an active local project.
	/// </summary>
	internal static bool IsEditorRuntime()
	{
		if ( ReadSandboxFlag( () => Application.IsEditor ) )
			return true;
		if ( ReadApplicationBool( "IsJoinLocal" ) )
			return true;
		if ( ReadSandboxFlag( () => Game.IsEditor ) )
			return true;
		if ( ReadSandboxFlag( () => Project.Current is not null ) )
			return true;
		return false;
	}

	/// <summary>
	/// Returns the package that the engine is actually running, if one is loaded.
	/// </summary>
	internal static Package GetRuntimeGamePackage()
	{
		if ( ReadApplicationValue( "GamePackage" ) is Package package )
			return package;

		try
		{
			return Project.Current?.Package;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Returns Application.GameIdent when the currently loaded s&amp;box API exposes it.
	/// </summary>
	internal static string GetApplicationGameIdent()
	{
		return ReadApplicationValue( "GameIdent" ) as string;
	}

	/// <summary>
	/// True only for a real published game bundle, not an editor/local project that happens
	/// to share an ident with a published package on asset.party.
	/// </summary>
	internal static bool IsPublishedGameBundleRuntime( Package detectedPackage = null )
	{
		if ( IsEditorRuntime() )
			return false;
		if ( ReadSandboxFlag( () => Application.IsStandalone ) )
			return false;

		var runtimePackage = GetRuntimeGamePackage();
		if ( runtimePackage?.Revision is null )
			return false;

		return IsGamePackage( runtimePackage ) || IsGamePackage( detectedPackage );
	}

	private static bool ReadApplicationBool( string propertyName )
	{
		return ReadApplicationValue( propertyName ) is bool value && value;
	}

	private static object ReadApplicationValue( string propertyName )
	{
		try
		{
			var appType = TypeLibrary.GetType( "Sandbox.Application" ) ?? TypeLibrary.GetType( "Application" );
			return appType?.GetStaticValue( propertyName );
		}
		catch
		{
			return null;
		}
	}

	private static bool IsGamePackage( Package package )
	{
		return string.Equals( package?.TypeName, "game", StringComparison.OrdinalIgnoreCase );
	}

	private static bool ReadSandboxFlag( Func<bool> read )
	{
		try
		{
			return read();
		}
		catch
		{
			return false;
		}
	}

}
