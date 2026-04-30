using System;
using System.Collections.Generic;
using System.Text;

namespace Sandbox;

public static partial class NetworkStorage
{
	private static readonly string[] UnsupportedDedicatedSecretLaunchKeys =
	{
		"secret-key",
		"secret_key",
		"secretKey"
	};

	private static void LoadDedicatedServerSecretKey()
	{
		if ( _dedicatedSecretLookupAttempted ) return;
		_dedicatedSecretLookupAttempted = true;

		if ( TryReadLaunchSecretKey( out _dedicatedServerSecretKey, out _dedicatedServerSecretKeySource ) )
		{
			Log.Info( $"[NetworkStorage] Dedicated endpoint secret key loaded from {_dedicatedServerSecretKeySource} ({DescribeSecretKeyForLog( _dedicatedServerSecretKey )})." );
			return;
		}

		_dedicatedServerSecretKey = null;
		_dedicatedServerSecretKeySource = "none";
		_dedicatedServerSecretKeyRejected = false;
	}

	private static bool TryReadLaunchSecretKey( out string secretKey, out string source )
	{
		secretKey = null;
		source = null;

		var settings = LaunchArguments.GameSettings;
		if ( settings is null )
		{
			// Dedicated server launches can leave LaunchArguments.GameSettings empty;
			// the hidden ConVars below still receive +network_storage_secret_key.
		}
		else
		{
			LogLaunchSettingsKeySummary( settings );
			WarnForUnsupportedSecretFlags( settings );
			if ( TryReadSecretFromSettings( settings, out secretKey, out source ) )
				return true;
		}

		return TryReadSecretFromConsoleSystem( out secretKey, out source );
	}

	private static bool TryReadSecretFromSettings( IReadOnlyDictionary<string, string> settings, out string secretKey, out string source )
	{
		secretKey = null;
		source = null;

		foreach ( var supportedKey in DedicatedSecretLaunchKeys )
		{
			if ( TryGetLaunchSettingValue( settings, supportedKey, out var value, out var actualKey ) )
				return TryAcceptDedicatedSecretValue( value, $"launch flag +{actualKey}", actualKey, out secretKey, out source );
		}

		return false;
	}

	private static bool TryReadSecretFromConsoleSystem( out string secretKey, out string source )
	{
		secretKey = null;
		source = null;

		Log.Info( $"[NetworkStorage] Probing ConsoleSystem for dedicated secret keys: {SupportedDedicatedSecretLaunchFlagsText}" );
		foreach ( var unsupported in UnsupportedDedicatedSecretLaunchKeys )
			WarnIfConsoleValuePresentForUnsupportedFlag( unsupported );

		foreach ( var supportedKey in DedicatedSecretLaunchKeys )
		{
			var value = GetDedicatedSecretConVarValue( supportedKey );
			if ( string.IsNullOrWhiteSpace( value ) && !TryGetConsoleValue( supportedKey, out value ) )
				continue;
			if ( string.IsNullOrWhiteSpace( value ) )
				continue;

			return TryAcceptDedicatedSecretValue( value, $"ConsoleSystem +{supportedKey}", supportedKey, out secretKey, out source );
		}

		return false;
	}

	private static string GetDedicatedSecretConVarValue( string key )
	{
		return key switch
		{
			"network-storage-secret-key" => NetworkStorageSecretKeyDashedConVar,
			"network_storage_secret_key" => NetworkStorageSecretKeyConVar,
			"sboxcool_secret_key" => SboxCoolSecretKeyConVar,
			"networkStorageSecretKey" => NetworkStorageSecretKeyCamelConVar,
			"sboxcoolSecretKey" => SboxCoolSecretKeyCamelConVar,
			"nsSecretKey" => NsSecretKeyCamelConVar,
			"ns_secret_key" => NsSecretKeyConVar,
			_ => null
		};
	}

	private static bool TryGetConsoleValue( string key, out string value )
	{
		value = null;
		try
		{
			value = ConsoleSystem.GetValue( key, "" );
			return true;
		}
		catch ( Exception ex )
		{
			if ( NetworkStorageLogConfig.LogConfig )
				Log.Warning( $"[NetworkStorage] Could not read ConsoleSystem value +{key}: {ex.Message}" );
			return false;
		}
	}

	private static void WarnIfConsoleValuePresentForUnsupportedFlag( string key )
	{
		if ( TryGetConsoleValue( key, out var value ) && !string.IsNullOrWhiteSpace( value ) )
			Log.Warning( $"[NetworkStorage] Ignoring unsupported dedicated secret launch flag +{key}. Supported flags: {SupportedDedicatedSecretLaunchFlagsText}" );
	}

	private static bool TryAcceptDedicatedSecretValue( string value, string sourceLabel, string key, out string secretKey, out string source )
	{
		secretKey = null;
		source = null;
		if ( string.IsNullOrWhiteSpace( value ) )
		{
			Log.Warning( $"[NetworkStorage] Dedicated secret launch flag +{key} is present but empty." );
			return false;
		}

		secretKey = NormalizeSecretKey( value );
		source = sourceLabel;
		_dedicatedServerSecretKeyRejected = false;
		return true;
	}

	private static bool TryGetLaunchSettingValue( IReadOnlyDictionary<string, string> settings, string expectedKey, out string value, out string actualKey )
	{
		value = null;
		actualKey = null;

		foreach ( var pair in settings )
		{
			var key = NormalizeLaunchSettingKey( pair.Key );
			if ( string.Equals( key, expectedKey, StringComparison.OrdinalIgnoreCase ) )
			{
				value = pair.Value;
				actualKey = key;
				return true;
			}
		}

		return false;
	}

	private static void WarnForUnsupportedSecretFlags( IReadOnlyDictionary<string, string> settings )
	{
		foreach ( var pair in settings )
			WarnIfUnsupportedSecretFlag( NormalizeLaunchSettingKey( pair.Key ) );
	}

	private static void WarnIfUnsupportedSecretFlag( string key )
	{
		foreach ( var unsupported in UnsupportedDedicatedSecretLaunchKeys )
		{
			if ( string.Equals( key, unsupported, StringComparison.OrdinalIgnoreCase ) )
			{
				Log.Warning( $"[NetworkStorage] Ignoring unsupported dedicated secret launch flag +{key}. Supported flags: {SupportedDedicatedSecretLaunchFlagsText}" );
				return;
			}
		}
	}

	private static void LogLaunchSettingsKeySummary( IReadOnlyDictionary<string, string> settings )
	{
		var sb = new StringBuilder();
		var count = 0;
		foreach ( var pair in settings )
			AppendKeySummary( sb, NormalizeLaunchSettingKey( pair.Key ), ref count );

		var keys = count == 0 ? "(none)" : sb.ToString();
		Log.Info( $"[NetworkStorage] Launch game setting keys visible to Network Storage: {keys}" );
	}

	private static void AppendKeySummary( StringBuilder sb, string key, ref int count )
	{
		if ( count > 0 ) sb.Append( ", " );
		if ( count >= 24 )
		{
			sb.Append( "..." );
			return;
		}

		sb.Append( '+' ).Append( key );
		count++;
	}

	private static string NormalizeLaunchSettingKey( string key )
	{
		key = (key ?? "").Trim();
		while ( key.StartsWith( "+", StringComparison.Ordinal ) || key.StartsWith( "-", StringComparison.Ordinal ) )
			key = key[1..];
		return key;
	}

	private static string DescribeSecretKeyForLog( string secretKey )
	{
		if ( string.IsNullOrWhiteSpace( secretKey ) )
			return "empty";

		var prefix = secretKey.StartsWith( "sbox_sk_", StringComparison.Ordinal ) ? "sbox_sk_" : "non-standard-prefix";
		return $"{prefix}, length={secretKey.Length}";
	}
}
