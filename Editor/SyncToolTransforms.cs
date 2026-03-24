using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Transforms between local JSON file format and the server's expected format.
/// Handles endpoints and collections (with constants/tables for game config).
/// </summary>
public static class SyncToolTransforms
{
	/// <summary>
	/// Convert local endpoint definitions to server format, preserving IDs by slug.
	/// </summary>
	public static JsonElement EndpointsToServer( List<JsonElement> localEndpoints, JsonElement? existingServer )
	{
		// Build slug → existing endpoint map for ID preservation
		var slugToExisting = new Dictionary<string, JsonElement>();
		if ( existingServer.HasValue && existingServer.Value.TryGetProperty( "data", out var data ) )
		{
			foreach ( var ep in data.EnumerateArray() )
			{
				if ( ep.TryGetProperty( "slug", out var s ) )
					slugToExisting[s.GetString()] = ep;
			}
		}

		var result = new List<Dictionary<string, object>>();

		foreach ( var ep in localEndpoints )
		{
			var slug = ep.TryGetProperty( "slug", out var s ) ? s.GetString() : "";
			var existing = slugToExisting.GetValueOrDefault( slug );
			var existingId = existing.ValueKind != JsonValueKind.Undefined && existing.TryGetProperty( "id", out var id ) ? id.GetString() : Guid.NewGuid().ToString( "N" )[..16];

			var entry = new Dictionary<string, object>
			{
				["id"] = existingId,
				["name"] = ep.TryGetProperty( "name", out var n ) ? n.GetString() : slug.Replace( "-", " " ),
				["slug"] = slug,
				["method"] = ep.TryGetProperty( "method", out var m ) ? m.GetString() : "POST",
				["description"] = ep.TryGetProperty( "description", out var d ) ? d.GetString() : "",
				["enabled"] = !ep.TryGetProperty( "enabled", out var en ) || en.ValueKind != JsonValueKind.False,
				["input"] = ep.TryGetProperty( "input", out var inp ) ? (object)inp : new Dictionary<string, object>(),
				["steps"] = ep.TryGetProperty( "steps", out var st ) ? (object)st : new List<object>(),
				["response"] = ep.TryGetProperty( "response", out var resp ) ? (object)resp : new Dictionary<string, object> { ["status"] = 200, ["body"] = new Dictionary<string, object> { ["ok"] = true } }
			};

			result.Add( entry );
		}

		return JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( result ) );
	}


	// ──────────────────────────────────────────────────────
	//  Server → Local (Pull / Reverse Transforms)
	// ──────────────────────────────────────────────────────

	/// <summary>
	/// Convert a single server endpoint to local file format.
	/// Always includes all fields with defaults so local matches server exactly.
	/// Strips only server-managed fields (id, createdAt).
	/// </summary>
	public static Dictionary<string, object> ServerEndpointToLocal( JsonElement ep )
	{
		var slugStr = ep.TryGetProperty( "slug", out var slug ) ? slug.GetString() : "";

		return new Dictionary<string, object>
		{
			["slug"] = slugStr,
			["name"] = ep.TryGetProperty( "name", out var name ) ? name.GetString() : slugStr.Replace( "-", " " ),
			["method"] = ep.TryGetProperty( "method", out var method ) ? method.GetString() : "POST",
			["description"] = ep.TryGetProperty( "description", out var desc ) ? desc.GetString() : "",
			["enabled"] = !ep.TryGetProperty( "enabled", out var enabled ) || enabled.ValueKind != JsonValueKind.False,
			["input"] = ep.TryGetProperty( "input", out var input ) ? (object)input : new Dictionary<string, object>(),
			["steps"] = ep.TryGetProperty( "steps", out var steps ) ? (object)steps : new List<object>(),
			["response"] = ep.TryGetProperty( "response", out var response )
				? (object)response
				: new Dictionary<string, object> { ["status"] = 200, ["body"] = new Dictionary<string, object> { ["ok"] = true } }
		};
		// Intentionally omit: id, createdAt (server-managed)
	}

	/// <summary>
	/// Convert a single server collection to local file format.
	/// Includes schema + config fields. Strips server-managed fields (id, createdAt, version).
	/// Each collection is saved as its own file: collections/{name}.json
	/// </summary>
	public static Dictionary<string, object> ServerCollectionToLocal( JsonElement col )
	{
		var nameStr = col.TryGetProperty( "name", out var n ) ? n.GetString() : "unknown";

		var local = new Dictionary<string, object>
		{
			["name"] = nameStr,
			["description"] = col.TryGetProperty( "description", out var desc ) ? desc.GetString() : "",
			["collectionType"] = col.TryGetProperty( "collectionType", out var ct ) ? ct.GetString() : "per-steamid",
			["accessMode"] = col.TryGetProperty( "accessMode", out var am ) ? am.GetString() : "public",
			["maxRecords"] = col.TryGetProperty( "maxRecords", out var mr ) ? mr.GetInt32() : 1,
			["allowRecordDelete"] = col.TryGetProperty( "allowRecordDelete", out var ard ) && ard.ValueKind == JsonValueKind.True,
			["requireSaveVersion"] = col.TryGetProperty( "requireSaveVersion", out var rsv ) && rsv.ValueKind == JsonValueKind.True,
			["webhookOnRateLimit"] = col.TryGetProperty( "webhookOnRateLimit", out var wrl ) && wrl.ValueKind == JsonValueKind.True,
			["rateLimitAction"] = col.TryGetProperty( "rateLimitAction", out var rla ) ? rla.GetString() : "reject",
		};

		if ( col.TryGetProperty( "rateLimits", out var rl ) )
			local["rateLimits"] = rl;
		else
			local["rateLimits"] = new Dictionary<string, object> { ["mode"] = "none" };

		if ( col.TryGetProperty( "schema", out var schema ) )
			local["schema"] = schema;
		else
			local["schema"] = new Dictionary<string, object>();

		// Game config data (constants = groups, tables = structured data)
		if ( col.TryGetProperty( "constants", out var constants ) )
			local["constants"] = constants;
		if ( col.TryGetProperty( "tables", out var tables ) )
			local["tables"] = tables;

		// Intentionally omit: id, createdAt, version (server-managed)
		return local;
	}

	/// <summary>
	/// Parse server collections GET response into a list of collection objects.
	/// </summary>
	public static List<(string Name, Dictionary<string, object> Local)> ServerToCollections( JsonElement serverResponse )
	{
		var result = new List<(string, Dictionary<string, object>)>();

		var data = serverResponse;
		if ( serverResponse.TryGetProperty( "data", out var d ) )
			data = d;

		if ( data.ValueKind != JsonValueKind.Array ) return result;

		foreach ( var col in data.EnumerateArray() )
		{
			var local = ServerCollectionToLocal( col );
			var name = local["name"]?.ToString() ?? "unknown";
			result.Add( (name, local) );
		}

		return result;
	}

	/// <summary>
	/// Convert local collection files to server push format.
	/// Includes all fields needed for both creation and update.
	/// Server creates collections that don't exist and updates ones that do.
	/// </summary>
	public static JsonElement CollectionsToServer( List<Dictionary<string, object>> localCollections )
	{
		var payload = new List<Dictionary<string, object>>();
		foreach ( var col in localCollections )
		{
			var entry = new Dictionary<string, object>
			{
				["name"] = col.GetValueOrDefault( "name", "unknown" ),
				["schema"] = col.GetValueOrDefault( "schema", new Dictionary<string, object>() ),
			};
			// Fields for both create and update
			if ( col.TryGetValue( "description", out var desc ) )
				entry["description"] = desc;
			if ( col.TryGetValue( "accessMode", out var am ) )
				entry["accessMode"] = am;
			if ( col.TryGetValue( "constants", out var constants ) )
				entry["constants"] = constants;
			if ( col.TryGetValue( "tables", out var tables ) )
				entry["tables"] = tables;
			// Fields for creation (ignored on update)
			if ( col.TryGetValue( "collectionType", out var ct ) )
				entry["collectionType"] = ct;
			if ( col.TryGetValue( "maxRecords", out var mr ) )
				entry["maxRecords"] = mr;
			if ( col.TryGetValue( "allowRecordDelete", out var ard ) )
				entry["allowRecordDelete"] = ard;
			if ( col.TryGetValue( "requireSaveVersion", out var rsv ) )
				entry["requireSaveVersion"] = rsv;
			if ( col.TryGetValue( "rateLimits", out var rl ) )
				entry["rateLimits"] = rl;
			if ( col.TryGetValue( "rateLimitAction", out var rla ) )
				entry["rateLimitAction"] = rla;
			payload.Add( entry );
		}
		return JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( payload ) );
	}

	/// <summary>
	/// Extract a JsonElement value to a plain .NET object for serialization.
	/// </summary>
	private static object ExtractValue( JsonElement val )
	{
		return val.ValueKind switch
		{
			JsonValueKind.Number => val.TryGetInt32( out var i ) ? (object)i : val.GetDouble(),
			JsonValueKind.String => val.GetString(),
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			_ => val.ToString()
		};
	}
}
