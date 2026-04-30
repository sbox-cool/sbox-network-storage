using System.Text.Json.Serialization;

namespace Sandbox;

/// <summary>
/// A server-side document mutation for Network Storage collection records.
/// Operations are applied by the backend against the current document, so callers
/// can update counters and arrays without sending a full replacement document.
/// </summary>
public sealed class NetworkStorageOperation
{
	[JsonPropertyName( "op" )]
	public string Op { get; init; }

	[JsonPropertyName( "path" )]
	public string Path { get; init; }

	[JsonPropertyName( "value" )]
	[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
	public object Value { get; init; }

	[JsonPropertyName( "match" )]
	[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
	public object Match { get; init; }

	[JsonPropertyName( "source" )]
	[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
	public string Source { get; init; }

	[JsonPropertyName( "reason" )]
	[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
	public string Reason { get; init; }

	public static NetworkStorageOperation Set( string path, object value, string source = null, string reason = null )
		=> new() { Op = "set", Path = path, Value = value, Source = source, Reason = reason };

	public static NetworkStorageOperation Increment( string path, double amount, string source = null, string reason = null )
		=> new() { Op = "inc", Path = path, Value = amount, Source = source, Reason = reason };

	public static NetworkStorageOperation Push( string path, object value )
		=> new() { Op = "push", Path = path, Value = value };

	public static NetworkStorageOperation Pull( string path, object match )
		=> new() { Op = "pull", Path = path, Match = match };

	public static NetworkStorageOperation Remove( string path, object value )
		=> new() { Op = "remove", Path = path, Value = value };
}
