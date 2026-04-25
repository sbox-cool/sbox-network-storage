using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Sandbox;

/// <summary>
/// Extension helpers for working with Network Storage JSON responses.
/// Reduces boilerplate when parsing endpoint responses into game objects.
///
/// Usage:
///   var ores = response.ReadDictionary( "ores", prop => (float)prop.GetDouble() );
///   var upgrades = response.ReadStringList( "purchasedUpgrades" );
///   var level = response.Int( "level", 1 );
/// </summary>
public static class NetworkStorageExtensions
{
	/// <summary>Get an int from a JsonElement, with fallback.</summary>
	public static int Int( this JsonElement el, string key, int fallback = 0 )
		=> JsonHelpers.GetInt( el, key, fallback );

	/// <summary>Get a float from a JsonElement, with fallback.</summary>
	public static float Float( this JsonElement el, string key, float fallback = 0f )
		=> JsonHelpers.GetFloat( el, key, fallback );

	/// <summary>Get a string from a JsonElement, with fallback.</summary>
	public static string Str( this JsonElement el, string key, string fallback = "" )
		=> JsonHelpers.GetString( el, key, fallback );

	/// <summary>Get a bool from a JsonElement, with fallback.</summary>
	public static bool Bool( this JsonElement el, string key, bool fallback = false )
		=> JsonHelpers.GetBool( el, key, fallback );

	/// <summary>
	/// Read a JSON array property as a List of strings.
	/// Returns empty list if property missing or wrong type.
	/// </summary>
	public static List<string> ReadStringList( this JsonElement el, string key )
	{
		var list = new List<string>();
		if ( !el.TryGetProperty( key, out var arr ) || arr.ValueKind != JsonValueKind.Array )
			return list;

		foreach ( var item in arr.EnumerateArray() )
		{
			if ( item.ValueKind == JsonValueKind.String )
				list.Add( item.GetString() );
		}
		return list;
	}

	/// <summary>
	/// Read a JSON object property as a Dictionary with a value converter.
	/// Useful for ore inventories: response.ReadDictionary("ores", v => (float)v.GetDouble())
	/// </summary>
	public static Dictionary<string, T> ReadDictionary<T>( this JsonElement el, string key, Func<JsonElement, T> converter )
	{
		var dict = new Dictionary<string, T>();
		if ( !el.TryGetProperty( key, out var obj ) || obj.ValueKind != JsonValueKind.Object )
			return dict;

		foreach ( var prop in obj.EnumerateObject() )
		{
			try { dict[prop.Name] = converter( prop.Value ); }
			catch { /* skip malformed entries */ }
		}
		return dict;
	}

	/// <summary>
	/// Read a JSON array property as a List with a row converter.
	/// Useful for tables: response.ReadList("rows", row => new OreInfo(row))
	/// </summary>
	public static List<T> ReadList<T>( this JsonElement el, string key, Func<JsonElement, T> converter )
	{
		var list = new List<T>();
		if ( !el.TryGetProperty( key, out var arr ) || arr.ValueKind != JsonValueKind.Array )
			return list;

		foreach ( var item in arr.EnumerateArray() )
		{
			try { list.Add( converter( item ) ); }
			catch { /* skip malformed entries */ }
		}
		return list;
	}

	/// <summary>
	/// Compare local state against a server response and return a list of mismatches.
	/// Fields is a dictionary of fieldName → (localValue, serverExtractor).
	/// </summary>
	public static List<string> FindMismatches( this JsonElement serverData, Dictionary<string, (object Local, Func<JsonElement, object> Extract)> fields )
	{
		var mismatches = new List<string>();
		foreach ( var (name, (local, extract)) in fields )
		{
			try
			{
				var remote = extract( serverData );
				if ( !Equals( local, remote ) )
					mismatches.Add( $"{name}: local={local} server={remote}" );
			}
			catch { /* field not present */ }
		}
		return mismatches;
	}
}
