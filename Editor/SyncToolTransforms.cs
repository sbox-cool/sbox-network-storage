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
	private static readonly HashSet<string> ServerManagedFields = new() { "id", "createdAt", "version" };
	private static readonly HashSet<string> AuthoringOnlyFields = new() { "authoringMode", "sourceFormat", "sourcePath", "sourceText", "sourceVersion" };

	/// <summary>
	/// Strip server-managed and authoring-only fields from local Dictionary data for fair comparison.
	/// </summary>
	public static Dictionary<string, object> StripServerManagedFields( Dictionary<string, object> local )
	{
		var result = new Dictionary<string, object>();
		foreach ( var kv in local )
		{
			if ( !ServerManagedFields.Contains( kv.Key ) && !AuthoringOnlyFields.Contains( kv.Key ) )
				result[kv.Key] = kv.Value;
		}
		return result;
	}

	public static bool TryGetSourceText( JsonElement resource, out string sourceText )
	{
		if ( resource.TryGetProperty( "sourceText", out var value ) && value.ValueKind == JsonValueKind.String )
		{
			sourceText = value.GetString();
			return !string.IsNullOrWhiteSpace( sourceText );
		}

		sourceText = null;
		return false;
	}

	public static string GetSourcePath( JsonElement resource )
	{
		return resource.TryGetProperty( "sourcePath", out var value ) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
	}

	public static bool TryGetCanonicalDefinition( JsonElement resource, out JsonElement canonical )
	{
		if ( resource.TryGetProperty( "canonicalDefinition", out var value ) && value.ValueKind == JsonValueKind.Object )
		{
			canonical = value;
			return true;
		}

		canonical = default;
		return false;
	}

	private static JsonElement GetComparableResourceView( JsonElement resource )
	{
		if ( TryGetCanonicalDefinition( resource, out var canonical ) )
			return canonical;

		if ( resource.ValueKind == JsonValueKind.Object
			&& resource.TryGetProperty( "definition", out var definition )
			&& definition.ValueKind == JsonValueKind.Object )
		{
			var flattened = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
			foreach ( var prop in resource.EnumerateObject() )
			{
				if ( prop.NameEquals( "definition" ) )
					continue;
				if ( IsSourceEnvelopeField( prop.Name ) || IsServerManagedOrCompilerField( prop.Name ) )
					continue;

				flattened[prop.Name] = JsonElementToObject( prop.Value );
			}

			foreach ( var prop in definition.EnumerateObject() )
				flattened[prop.Name] = JsonElementToObject( prop.Value );

			return JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( flattened ) );
		}

		return resource;
	}

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
				["notes"] = ep.TryGetProperty( "notes", out var notes ) ? notes.GetString() : "",
				["enabled"] = !ep.TryGetProperty( "enabled", out var en ) || en.ValueKind != JsonValueKind.False,
				["input"] = ep.TryGetProperty( "input", out var inp ) ? (object)inp : new Dictionary<string, object>(),
				["steps"] = ep.TryGetProperty( "steps", out var st ) ? (object)st : new List<object>(),
				["response"] = ep.TryGetProperty( "response", out var resp ) ? (object)resp : new Dictionary<string, object> { ["status"] = 200, ["body"] = new Dictionary<string, object> { ["ok"] = true } }
			};

			if ( SyncToolConfig.IsEndpointDeprecated( ep ) )
				entry["deprecated"] = true;

			TryAddSourceEnvelope( ep, entry );

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
		var source = GetComparableResourceView( ep );
		var slugStr = source.TryGetProperty( "slug", out var slug ) ? slug.GetString() : "";
		if ( string.IsNullOrEmpty( slugStr ) && ep.TryGetProperty( "slug", out var wrappedSlug ) )
			slugStr = wrappedSlug.GetString();

		var local = new Dictionary<string, object>
		{
			["slug"] = slugStr,
			["name"] = source.TryGetProperty( "name", out var name ) ? name.GetString() : slugStr.Replace( "-", " " ),
			["method"] = source.TryGetProperty( "method", out var method ) ? method.GetString() : "POST",
			["enabled"] = !source.TryGetProperty( "enabled", out var enabled ) || enabled.ValueKind != JsonValueKind.False,
			["input"] = source.TryGetProperty( "input", out var input ) ? (object)input : new Dictionary<string, object>(),
			["steps"] = source.TryGetProperty( "steps", out var steps ) ? (object)steps : new List<object>(),
			["response"] = source.TryGetProperty( "response", out var response )
				? (object)response
				: new Dictionary<string, object> { ["status"] = 200, ["body"] = new Dictionary<string, object> { ["ok"] = true } }
		};

		if ( source.TryGetProperty( "description", out var desc ) && !string.IsNullOrEmpty( desc.GetString() ) )
			local["description"] = desc.GetString();
		if ( source.TryGetProperty( "notes", out var notes ) && !string.IsNullOrEmpty( notes.GetString() ) )
			local["notes"] = notes.GetString();

		if ( SyncToolConfig.IsEndpointDeprecated( ep ) || SyncToolConfig.IsEndpointDeprecated( source ) )
			local["deprecated"] = true;

		// Intentionally omit: id, createdAt (server-managed)
		return local;
	}

	/// <summary>
	/// Convert a single server collection to local file format.
	/// Includes schema + config fields. Strips server-managed fields (id, createdAt, version).
	/// Each collection is saved as its own source file: collections/{name}.collection.yml
	/// </summary>
	public static Dictionary<string, object> ServerCollectionToLocal( JsonElement col )
	{
		var source = GetComparableResourceView( col );
		var nameStr = source.TryGetProperty( "name", out var n ) ? n.GetString() : "unknown";
		if ( string.IsNullOrEmpty( nameStr ) && col.TryGetProperty( "name", out var wrappedName ) )
			nameStr = wrappedName.GetString();

		var local = new Dictionary<string, object>
		{
			["name"] = nameStr,
			["collectionType"] = source.TryGetProperty( "collectionType", out var ct ) ? ct.GetString() : "per-steamid",
			["accessMode"] = source.TryGetProperty( "accessMode", out var am ) ? am.GetString() : "public",
			["maxRecords"] = source.TryGetProperty( "maxRecords", out var mr ) ? mr.GetInt32() : 1,
			["allowRecordDelete"] = source.TryGetProperty( "allowRecordDelete", out var ard ) && ard.ValueKind == JsonValueKind.True,
			["requireSaveVersion"] = source.TryGetProperty( "requireSaveVersion", out var rsv ) && rsv.ValueKind == JsonValueKind.True,
			["webhookOnRateLimit"] = source.TryGetProperty( "webhookOnRateLimit", out var wrl ) && wrl.ValueKind == JsonValueKind.True,
			["rateLimitAction"] = source.TryGetProperty( "rateLimitAction", out var rla ) ? rla.GetString() : "reject",
		};

		if ( source.TryGetProperty( "description", out var desc ) && !string.IsNullOrEmpty( desc.GetString() ) )
			local["description"] = desc.GetString();
		if ( source.TryGetProperty( "notes", out var notes ) && !string.IsNullOrEmpty( notes.GetString() ) )
			local["notes"] = notes.GetString();

		if ( source.TryGetProperty( "rateLimits", out var rl ) )
			local["rateLimits"] = rl;
		else
			local["rateLimits"] = new Dictionary<string, object> { ["mode"] = "none" };

		if ( source.TryGetProperty( "schema", out var schema ) )
			local["schema"] = schema;
		else
			local["schema"] = new Dictionary<string, object>();

		// Game config data (constants = groups, tables = structured data)
		if ( source.TryGetProperty( "constants", out var constants ) )
			local["constants"] = constants;
		if ( source.TryGetProperty( "tables", out var tables ) )
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
			TryAddSourceEnvelope( col, entry );
			payload.Add( entry );
		}
		return JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( payload ) );
	}

	// ──────────────────────────────────────────────────────
	//  Workflows
	// ──────────────────────────────────────────────────────

	/// <summary>
	/// Parse server workflows GET response into a list of (Id, LocalDict) tuples.
	/// </summary>
	public static List<(string Id, Dictionary<string, object> Local)> ServerToWorkflows( JsonElement serverResponse )
	{
		var result = new List<(string, Dictionary<string, object>)>();

		var data = serverResponse;
		if ( serverResponse.TryGetProperty( "data", out var d ) )
			data = d;

		if ( data.ValueKind != JsonValueKind.Array ) return result;

		foreach ( var wf in data.EnumerateArray() )
		{
			var local = ServerWorkflowToLocal( wf );
			var id = local["id"]?.ToString() ?? "unknown";
			result.Add( (id, local) );
		}

		return result;
	}

	/// <summary>
	/// Convert a single server workflow to local file format.
	/// Copies all user-defined fields, strips server-managed fields (createdAt, updatedAt, versionHash).
	/// </summary>
	public static Dictionary<string, object> ServerWorkflowToLocal( JsonElement wf )
	{
		var source = GetComparableResourceView( wf );
		var local = new Dictionary<string, object>();

		foreach ( var prop in source.EnumerateObject() )
		{
			if ( IsServerManagedOrCompilerField( prop.Name ) )
				continue;

			local[prop.Name] = prop.Value.ValueKind switch
			{
				JsonValueKind.String => (object)prop.Value.GetString(),
				JsonValueKind.Number => prop.Value.TryGetInt32( out var i ) ? i : prop.Value.GetDouble(),
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				_ => prop.Value // Objects and arrays stay as JsonElement (serializes correctly)
			};
		}

		return local;
	}

	private static bool IsServerManagedOrCompilerField( string name )
	{
		return name is "createdAt"
			or "updatedAt"
			or "versionHash"
			or "sourceText"
			or "sourceFormat"
			or "sourceVersion"
			or "sourcePath"
			or "authoringMode"
			or "compilerFingerprint"
			or "compilerFingerprintHash"
			or "sourceHash"
			or "dependencyHash"
			or "canonicalHash"
			or "executionPlanHash"
			or "dependencies"
			or "canonicalDefinition"
			or "executionPlan"
			or "diagnostics";
	}

	private static bool IsSourceEnvelopeField( string name )
	{
		return name is "sourceText"
			or "sourceFormat"
			or "sourceVersion"
			or "sourcePath"
			or "authoringMode";
	}

	/// <summary>
	/// Convert local workflow definitions to server format, preserving IDs.
	/// Passes through all fields — the backend validates what it needs.
	/// </summary>
	public static JsonElement WorkflowsToServer( List<JsonElement> localWorkflows, JsonElement? existingServer = null )
	{
		var idToExisting = new Dictionary<string, JsonElement>();
		if ( existingServer.HasValue )
		{
			var data = existingServer.Value;
			if ( data.TryGetProperty( "data", out var d ) ) data = d;
			if ( data.ValueKind == JsonValueKind.Array )
			{
				foreach ( var wf in data.EnumerateArray() )
				{
					if ( wf.TryGetProperty( "id", out var wfId ) )
						idToExisting[wfId.GetString()] = wf;
				}
			}
		}

		var result = new List<Dictionary<string, object>>();

		foreach ( var wf in localWorkflows )
		{
			var entry = ServerWorkflowToLocal( wf );
			TryAddSourceEnvelope( wf, entry );

			// Preserve server-managed fields from existing if available
			var wfIdStr = wf.TryGetProperty( "id", out var id ) ? id.GetString() : "";
			if ( !string.IsNullOrEmpty( wfIdStr ) && idToExisting.TryGetValue( wfIdStr, out var existing ) )
			{
				if ( existing.TryGetProperty( "createdAt", out var ca ) )
					entry["createdAt"] = ca.GetString();
			}

			result.Add( entry );
		}

		return JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( result ) );
	}

	// ── Tests ──

	/// <summary>Parse server tests response into id → local dict pairs.</summary>
	private static void TryAddSourceEnvelope( JsonElement resource, Dictionary<string, object> entry )
	{
		if ( resource.TryGetProperty( "authoringMode", out var authoringMode ) && authoringMode.ValueKind == JsonValueKind.String )
			entry["authoringMode"] = authoringMode.GetString();
		if ( resource.TryGetProperty( "sourceFormat", out var sourceFormat ) && sourceFormat.ValueKind == JsonValueKind.String )
			entry["sourceFormat"] = sourceFormat.GetString();
		if ( resource.TryGetProperty( "sourceVersion", out var sourceVersion ) )
			entry["sourceVersion"] = JsonElementToObject( sourceVersion );
		if ( resource.TryGetProperty( "sourcePath", out var sourcePath ) && sourcePath.ValueKind == JsonValueKind.String )
			entry["sourcePath"] = sourcePath.GetString();
		if ( resource.TryGetProperty( "sourceText", out var sourceText ) && sourceText.ValueKind == JsonValueKind.String )
			entry["sourceText"] = sourceText.GetString();
	}

	private static void TryAddSourceEnvelope( Dictionary<string, object> resource, Dictionary<string, object> entry )
	{
		foreach ( var key in new[] { "authoringMode", "sourceFormat", "sourceVersion", "sourcePath", "sourceText" } )
		{
			if ( resource.TryGetValue( key, out var value ) && value != null )
				entry[key] = value;
		}
	}

	private static object JsonElementToObject( JsonElement value )
	{
		return value.ValueKind switch
		{
			JsonValueKind.String => value.GetString(),
			JsonValueKind.Number => value.TryGetInt32( out var i ) ? i : value.GetDouble(),
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.Null => null,
			_ => value.Clone()
		};
	}

	public static Dictionary<string, Dictionary<string, object>> ServerToTests( JsonElement serverResponse )
	{
		var result = new Dictionary<string, Dictionary<string, object>>();
		var data = serverResponse;
		if ( data.TryGetProperty( "data", out var d ) ) data = d;
		if ( data.ValueKind != JsonValueKind.Array ) return result;

		foreach ( var test in data.EnumerateArray() )
		{
			var id = test.TryGetProperty( "id", out var testId ) ? testId.GetString() : "";
			if ( string.IsNullOrEmpty( id ) ) continue;
			result[id] = ServerTestToLocal( test );
		}
		return result;
	}

	/// <summary>Convert a server test to local file format — strips server-managed fields.</summary>
	public static Dictionary<string, object> ServerTestToLocal( JsonElement test )
	{
		var local = new Dictionary<string, object>();
		foreach ( var prop in test.EnumerateObject() )
		{
			if ( prop.Name is "createdAt" or "updatedAt" ) continue;
			local[prop.Name] = prop.Value.ValueKind switch
			{
				JsonValueKind.String => (object)prop.Value.GetString(),
				JsonValueKind.Number => prop.Value.TryGetInt32( out var i ) ? i : prop.Value.GetDouble(),
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				_ => prop.Value
			};
		}
		return local;
	}

	/// <summary>Convert local test definitions to server format.</summary>
	public static JsonElement TestsToServer( List<JsonElement> localTests, JsonElement? existingServer = null )
	{
		var idToExisting = new Dictionary<string, JsonElement>();
		if ( existingServer.HasValue )
		{
			var data = existingServer.Value;
			if ( data.TryGetProperty( "data", out var d ) ) data = d;
			if ( data.ValueKind == JsonValueKind.Array )
			{
				foreach ( var t in data.EnumerateArray() )
					if ( t.TryGetProperty( "id", out var tId ) )
						idToExisting[tId.GetString()] = t;
			}
		}

		var result = new List<Dictionary<string, object>>();
		foreach ( var test in localTests )
		{
			var entry = ServerTestToLocal( test );
			var testId = test.TryGetProperty( "id", out var id ) ? id.GetString() : "";
			if ( !string.IsNullOrEmpty( testId ) && idToExisting.TryGetValue( testId, out var existing ) )
			{
				if ( existing.TryGetProperty( "createdAt", out var ca ) )
					entry["createdAt"] = ca.GetString();
			}
			result.Add( entry );
		}
		return JsonSerializer.Deserialize<JsonElement>( JsonSerializer.Serialize( result ) );
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
