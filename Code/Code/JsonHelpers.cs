using System;
using System.Text.Json;

namespace Sandbox;

/// <summary>
/// Shared JSON deserialization helpers for Network Storage responses.
/// Handles type coercion (string → number), missing keys, and fallback defaults.
/// </summary>
public static class JsonHelpers
{
	/// <summary>Get an integer from a JSON element, with fallback.</summary>
	public static int GetInt( JsonElement el, string key, int fallback )
	{
		if ( !el.TryGetProperty( key, out var v ) ) return fallback;
		if ( v.ValueKind == JsonValueKind.Number ) return v.GetInt32();
		if ( v.ValueKind == JsonValueKind.String && int.TryParse( v.GetString(), out var p ) ) return p;
		return fallback;
	}

	/// <summary>Get a float from a JSON element, with fallback.</summary>
	public static float GetFloat( JsonElement el, string key, float fallback )
	{
		if ( !el.TryGetProperty( key, out var v ) ) return fallback;
		if ( v.ValueKind == JsonValueKind.Number ) return (float)v.GetDouble();
		if ( v.ValueKind == JsonValueKind.String && float.TryParse( v.GetString(), out var p ) ) return p;
		return fallback;
	}

	/// <summary>Get a string from a JSON element, with fallback.</summary>
	public static string GetString( JsonElement el, string key, string fallback )
	{
		if ( !el.TryGetProperty( key, out var v ) ) return fallback;
		return v.GetString() ?? fallback;
	}

	/// <summary>Get a bool from a JSON element, with fallback.</summary>
	public static bool GetBool( JsonElement el, string key, bool fallback )
	{
		if ( !el.TryGetProperty( key, out var v ) ) return fallback;
		if ( v.ValueKind == JsonValueKind.True ) return true;
		if ( v.ValueKind == JsonValueKind.False ) return false;
		return fallback;
	}
}
