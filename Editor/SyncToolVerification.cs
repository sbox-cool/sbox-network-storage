#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Pure comparison helpers for post-push verification. Shared by the Sync Tool window
/// and the network_storage MCP tools so both judge a readback identically.
/// </summary>
public static class SyncToolVerification
{
	public enum Match { None, SourceText, Normalized }

	/// <summary>Normalizes a source file for comparison (line endings, trim).</summary>
	public static string NormalizeSourceTextForVerification( string sourceText )
	{
		return (sourceText ?? "").Replace( "\r\n", "\n" ).Replace( '\r', '\n' ).Trim();
	}

	/// <summary>Canonical JSON for comparison: sorted keys, ignored authoring fields removed.</summary>
	public static string NormalizeJson( string json )
	{
		try
		{
			var el = JsonSerializer.Deserialize<JsonElement>( json );
			var sorted = SortJsonElement( el );
			return JsonSerializer.Serialize( sorted, new JsonSerializerOptions { WriteIndented = false } );
		}
		catch { return json.Trim(); }
	}

	public static string PrettyJson( string json )
	{
		if ( string.IsNullOrEmpty( json ) ) return "";
		try
		{
			var el = JsonSerializer.Deserialize<JsonElement>( json );
			return JsonSerializer.Serialize( el, new JsonSerializerOptions { WriteIndented = true } );
		}
		catch { return json; }
	}

	public static bool IsGeneratedMappedCollectionId( string id )
	{
		if ( string.IsNullOrWhiteSpace( id ) || !id.StartsWith( "col_" ) )
			return false;

		var collection = id[4..];
		return SyncToolConfig.SyncMappings.Any( mapping => string.Equals( mapping.Collection, collection, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>
	/// Verifies one endpoint readback against the local maps (slug to normalized JSON / normalized source text).
	/// </summary>
	public static Match MatchEndpoint( string slug, JsonElement remoteEndpoint, Dictionary<string, string> localNormBySlug, Dictionary<string, string> localSourceBySlug )
	{
		var remoteSource = SyncToolTransforms.TryGetSourceText( remoteEndpoint, out var rs ) ? rs : null;
		var remoteNorm = NormalizeJson( JsonSerializer.Serialize( SyncToolTransforms.ServerEndpointToLocal( remoteEndpoint ) ) );
		return CompareResource(
			localNormBySlug.GetValueOrDefault( slug ),
			localSourceBySlug.GetValueOrDefault( slug ),
			remoteNorm,
			remoteSource,
			collectionSemantics: false );
	}

	/// <summary>
	/// Verifies one collection readback. Source text match wins; otherwise normalized JSON or
	/// field-level semantics (ignores non-structural metadata) must match.
	/// </summary>
	public static Match MatchCollection( string name, JsonElement remoteCollection, Dictionary<string, string> localNormByName, Dictionary<string, string> localSourceByName )
	{
		var remoteSource = SyncToolTransforms.TryGetSourceText( remoteCollection, out var rs ) ? rs : null;
		var remoteNorm = NormalizeJson( JsonSerializer.Serialize( SyncToolTransforms.ServerCollectionToLocal( remoteCollection ) ) );
		return CompareResource(
			localNormByName.GetValueOrDefault( name ),
			localSourceByName.GetValueOrDefault( name ),
			remoteNorm,
			remoteSource,
			collectionSemantics: true );
	}

	/// <summary>
	/// Compares a local resource to a remote readback. Local values must already be normalized
	/// (see NormalizeJson / NormalizeSourceTextForVerification); null means absent.
	/// </summary>
	public static Match CompareResource( string localNorm, string localSourceText, string remoteNorm, string remoteSourceText, bool collectionSemantics )
	{
		if ( localSourceText != null && remoteSourceText != null
			&& localSourceText == NormalizeSourceTextForVerification( remoteSourceText ) )
			return Match.SourceText;

		if ( localNorm == null || remoteNorm == null )
			return Match.None;

		if ( localNorm == remoteNorm )
			return Match.Normalized;

		return collectionSemantics && CollectionSemanticsMatch( localNorm, remoteNorm ) ? Match.Normalized : Match.None;
	}

	/// <summary>
	/// Compare two collection resources field-by-field.
	/// Distinguishes schema (structural) from metadata (non-structural) changes.
	/// </summary>
	public static bool CollectionSemanticsMatch( string localJson, string remoteJson )
	{
		try
		{
			var local = JsonSerializer.Deserialize<JsonElement>( localJson );
			var remote = JsonSerializer.Deserialize<JsonElement>( remoteJson );
			var rateLimitDefault = new Dictionary<string, object> { ["mode"] = "none" };
			var emptyObject = new Dictionary<string, object>();

			foreach ( var field in new[] { "name", "description", "notes" } )
			{
				if ( NormalizeCollectionField( local, field, "" ) != NormalizeCollectionField( remote, field, "" ) )
					return false;
			}

			if ( NormalizeCollectionField( local, "collectionType", "per-steamid" ) != NormalizeCollectionField( remote, "collectionType", "per-steamid" ) ) return false;
			if ( NormalizeCollectionField( local, "accessMode", "public" ) != NormalizeCollectionField( remote, "accessMode", "public" ) ) return false;
			// An unset visibility means the server default (it reports e.g. "private" for user collections).
			if ( local.TryGetProperty( "visibility", out _ ) && NormalizeCollectionField( local, "visibility", "" ) != NormalizeCollectionField( remote, "visibility", "" ) ) return false;
			if ( NormalizeCollectionField( local, "maxRecords", 1 ) != NormalizeCollectionField( remote, "maxRecords", 1 ) ) return false;
			if ( NormalizeCollectionField( local, "allowRecordDelete", false ) != NormalizeCollectionField( remote, "allowRecordDelete", false ) ) return false;
			if ( NormalizeCollectionField( local, "requireSaveVersion", false ) != NormalizeCollectionField( remote, "requireSaveVersion", false ) ) return false;
			if ( NormalizeCollectionField( local, "webhookOnRateLimit", false ) != NormalizeCollectionField( remote, "webhookOnRateLimit", false ) ) return false;
			if ( NormalizeCollectionField( local, "rateLimitAction", "reject" ) != NormalizeCollectionField( remote, "rateLimitAction", "reject" ) ) return false;
			if ( NormalizeCollectionField( local, "rateLimits", rateLimitDefault ) != NormalizeCollectionField( remote, "rateLimits", rateLimitDefault ) ) return false;
			if ( NormalizeCollectionField( local, "schema", emptyObject ) != NormalizeCollectionField( remote, "schema", emptyObject ) ) return false;
			if ( NormalizeCollectionField( local, "constants", emptyObject ) != NormalizeCollectionField( remote, "constants", emptyObject ) ) return false;
			if ( NormalizeCollectionField( local, "tables", emptyObject ) != NormalizeCollectionField( remote, "tables", emptyObject ) ) return false;

			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string NormalizeCollectionField( JsonElement element, string field, object defaultValue )
	{
		var json = element.ValueKind == JsonValueKind.Object && element.TryGetProperty( field, out var value )
			? value.GetRawText()
			: JsonSerializer.Serialize( defaultValue );
		return NormalizeJson( json );
	}

	private static object SortJsonElement( JsonElement el )
	{
		switch ( el.ValueKind )
		{
			case JsonValueKind.Object:
				var sorted = new SortedDictionary<string, object>();
				foreach ( var prop in el.EnumerateObject() )
				{
					if ( IsIgnoredComparisonField( prop.Name ) )
						continue;
					sorted[prop.Name] = SortJsonElement( prop.Value );
				}
				return sorted.Count == 0 ? null : sorted;
			case JsonValueKind.Array:
				var arr = new List<object>();
				foreach ( var item in el.EnumerateArray() )
					arr.Add( SortJsonElement( item ) );
				return arr;
			case JsonValueKind.String:
				return el.GetString();
			case JsonValueKind.Number:
				return el.TryGetInt64( out var l ) ? (object)l : el.GetDouble();
			case JsonValueKind.True:
				return true;
			case JsonValueKind.False:
				return false;
			default:
				return null;
		}
	}

	private static bool IsIgnoredComparisonField( string name )
	{
		return name is "authoringMode"
			or "sourceText"
			or "sourceFormat"
			or "sourceVersion"
			or "sourcePath"
			or "compilerFingerprint"
			or "compilerFingerprintHash"
			or "sourceHash"
			or "dependencyHash"
			or "canonicalHash"
			or "executionPlanHash"
			or "dependencies"
			or "executionPlan"
			or "diagnostics"
			or "canonicalDefinition";
	}
}
