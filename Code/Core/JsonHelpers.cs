using System;
using System.Globalization;
using System.Text.Json;

namespace Sandbox;

/// <summary>
/// Shared JSON read helpers for Network Storage responses.
/// Handles type coercion (string → number), missing keys, and fallback defaults.
/// Reads never throw: a non-object parent, a missing key, a value of the wrong kind,
/// or a number that does not fit the requested type all return the fallback.
/// </summary>
public static class JsonHelpers
{
	/// <summary>Get an integer from a JSON element, with fallback. Accepts 32-bit integers and integer strings only.</summary>
	public static int GetInt( JsonElement el, string key, int fallback )
	{
		if ( !TryGetValue( el, key, out var v ) ) return fallback;
		if ( v.ValueKind == JsonValueKind.Number ) return v.TryGetInt32( out var n ) ? n : fallback;
		if ( v.ValueKind == JsonValueKind.String && int.TryParse( v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p ) ) return p;
		return fallback;
	}

	/// <summary>Get a float from a JSON element, with fallback. Accepts numbers and finite numeric strings (invariant culture).</summary>
	public static float GetFloat( JsonElement el, string key, float fallback )
	{
		if ( !TryGetValue( el, key, out var v ) ) return fallback;
		if ( v.ValueKind == JsonValueKind.Number ) return v.TryGetDouble( out var d ) ? (float)d : fallback;
		if ( v.ValueKind == JsonValueKind.String && float.TryParse( v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p ) && !float.IsNaN( p ) && !float.IsInfinity( p ) ) return p;
		return fallback;
	}

	/// <summary>
	/// Get a string from a JSON element, with fallback. Strings return their value; numbers and booleans
	/// return their JSON text; null, objects, and arrays return the fallback.
	/// </summary>
	public static string GetString( JsonElement el, string key, string fallback )
	{
		if ( !TryGetValue( el, key, out var v ) ) return fallback;

		switch ( v.ValueKind )
		{
			case JsonValueKind.String:
				return v.GetString() ?? fallback;
			case JsonValueKind.Number:
			case JsonValueKind.True:
			case JsonValueKind.False:
				return v.GetRawText();
			default:
				return fallback;
		}
	}

	/// <summary>Get a bool from a JSON element, with fallback.</summary>
	public static bool GetBool( JsonElement el, string key, bool fallback )
	{
		if ( !TryGetValue( el, key, out var v ) ) return fallback;
		if ( v.ValueKind == JsonValueKind.True ) return true;
		if ( v.ValueKind == JsonValueKind.False ) return false;
		return fallback;
	}

	private static bool TryGetValue( JsonElement el, string key, out JsonElement value )
	{
		value = default;
		return key is not null && el.ValueKind == JsonValueKind.Object && el.TryGetProperty( key, out value );
	}
}
