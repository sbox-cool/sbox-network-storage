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
		if ( IsDedicatedServerProcess )
			return "dedicated";
		if ( IsEditorRuntime() )
			return "editor";
		return "game";
	}

	/// <summary>
	/// True when running in the editor, an editor-spawned local instance, or against an active local project.
	/// A real loaded <c>Application.GamePackage</c> with a revision wins over a merely-open
	/// <c>Project.Current</c>, because players can run a published game while the editor has a
	/// local project open in the background.
	/// </summary>
	internal static bool IsEditorRuntime()
	{
		if ( ReadApplicationBool( "IsJoinLocal" ) )
			return true;

		if ( IsPublishedGamePackage( GetApplicationGamePackage() ) )
			return false;

		if ( ReadSandboxFlag( () => Application.IsEditor ) )
			return true;
		if ( ReadSandboxFlag( () => Game.IsEditor ) )
			return true;
		if ( ReadSandboxFlag( () => Project.Current is not null ) )
			return true;
		return false;
	}

	/// <summary>
	/// Returns the package that the engine is actually running, if one is loaded.
	/// Falls back to the active project package only when the application has no runtime package.
	/// </summary>
	internal static Package GetRuntimeGamePackage()
	{
		return GetApplicationGamePackage() ?? GetActiveProjectPackage();
	}

	private static Package GetApplicationGamePackage()
	{
		return ReadApplicationValue( "GamePackage" ) as Package;
	}

	private static Package GetActiveProjectPackage()
	{
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

	internal static string BuildRuntimeContextSummary()
	{
		return $"appEditor={ReadSandboxFlag( () => Application.IsEditor )}, gameEditor={ReadSandboxFlag( () => Game.IsEditor )}, joinLocal={ReadApplicationBool( "IsJoinLocal" )}, standalone={ReadSandboxFlag( () => Application.IsStandalone )}, appPackage={DescribePackage( GetApplicationGamePackage() )}, projectPackage={DescribePackage( GetActiveProjectPackage() )}";
	}

	/// <summary>
	/// True only for a real published game bundle, not an editor/local project that happens
	/// to share an ident with a published package on asset.party.
	/// </summary>
	internal static bool IsPublishedGameBundleRuntime( Package detectedPackage = null )
	{
		// Highest-confidence signal: the engine says the currently loaded game package
		// is a versioned game package. This should not be overridden by Project.Current,
		// which can exist simply because the editor is open.
		var applicationPackage = GetApplicationGamePackage();
		if ( IsPublishedGamePackage( applicationPackage ) )
			return true;

		if ( IsEditorRuntime() )
			return false;

		return IsPublishedGamePackage( GetRuntimeGamePackage() ) || IsPublishedGamePackage( detectedPackage );
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

	private static bool IsPublishedGamePackage( Package package )
	{
		return package?.Revision is not null && IsGamePackage( package );
	}

	private static string DescribePackage( Package package )
	{
		if ( package is null )
			return "null";

		return $"{package.FullIdent ?? package.Ident ?? "(no-ident)"}/type={package.TypeName ?? "?"}/rev={package.Revision?.VersionId.ToString() ?? "null"}/remote={package.IsRemote}";
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
