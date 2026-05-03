using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sandbox;

[TestClass]
public class SyncToolFlowCanonicalizerTests
{
	// ──────────────────────────────────────────────────────
	// The cases the user reported: legacy onFail vs canonical
	// routes.{true,false} must compare as semantically identical
	// after canonicalization, regardless of which side authored
	// which shape.
	// ──────────────────────────────────────────────────────

	[TestMethod]
	public void LegacyOnFailMapsToCanonicalRoutesFalseReject()
	{
		var legacy = ParseStep( @"{
			""id"": ""guard"",
			""type"": ""condition"",
			""check"": { ""field"": ""x"", ""op"": "">"", ""value"": 0 },
			""onFail"": { ""status"": 400, ""error"": ""BAD"", ""message"": ""bad"" }
		}" );

		var canonical = ParseStep( @"{
			""id"": ""guard"",
			""type"": ""condition"",
			""check"": { ""field"": ""x"", ""op"": "">"", ""value"": 0 },
			""routes"": {
				""false"": { ""action"": ""reject"", ""status"": 400, ""error"": ""BAD"", ""message"": ""bad"" }
			}
		}" );

		AssertSemanticallyEqual( legacy, canonical );
	}

	[TestMethod]
	public void LegacyOnFailStringSkipMapsToRoutesFalseSkip()
	{
		var legacy = ParseStep( @"{
			""id"": ""guard"",
			""type"": ""condition"",
			""check"": { ""field"": ""x"", ""op"": ""=="", ""value"": true },
			""onFail"": ""skip""
		}" );

		var canonical = ParseStep( @"{
			""id"": ""guard"",
			""type"": ""condition"",
			""check"": { ""field"": ""x"", ""op"": ""=="", ""value"": true },
			""routes"": {
				""false"": { ""action"": ""skip"", ""count"": 1 }
			}
		}" );

		AssertSemanticallyEqual( legacy, canonical );
	}

	[TestMethod]
	public void OnTrueOnFalseShorthandMapsToRoutes()
	{
		var legacy = ParseStep( @"{
			""id"": ""guard"",
			""type"": ""condition"",
			""onTrue"": { ""action"": ""continue"" },
			""onFalse"": { ""action"": ""reject"", ""status"": 403 }
		}" );

		var canonical = ParseStep( @"{
			""id"": ""guard"",
			""type"": ""condition"",
			""routes"": {
				""true"": { ""action"": ""continue"" },
				""false"": { ""action"": ""reject"", ""status"": 403 }
			}
		}" );

		AssertSemanticallyEqual( legacy, canonical );
	}

	[TestMethod]
	public void ConditionStepWithoutAnyRouteFieldsGetsDefaultRoutes()
	{
		// A bare condition step with no route hints normalizes to the implicit
		// continue/reject pair that the runtime applies anyway.
		var bare = ParseStep( @"{
			""id"": ""guard"",
			""type"": ""condition"",
			""check"": { ""field"": ""x"", ""op"": "">"", ""value"": 0 }
		}" );

		var withDefaults = ParseStep( @"{
			""id"": ""guard"",
			""type"": ""condition"",
			""check"": { ""field"": ""x"", ""op"": "">"", ""value"": 0 },
			""routes"": {
				""true"": { ""action"": ""continue"" },
				""false"": { ""action"": ""reject"" }
			}
		}" );

		AssertSemanticallyEqual( bare, withDefaults );
	}

	[TestMethod]
	public void NonConditionStepsWithoutRouteFieldsArePassedThrough()
	{
		var step = ParseStep( @"{
			""id"": ""write"",
			""type"": ""write"",
			""collection"": ""players"",
			""key"": ""{{steamId}}""
		}" );

		var normalized = (Dictionary<string, object>)SyncToolFlowCanonicalizer.NormalizeStepRoutes( step );
		Assert.IsFalse( normalized.ContainsKey( "routes" ) );
		Assert.AreEqual( "write", normalized["type"] );
	}

	[TestMethod]
	public void RealValueDifferencesStillSurface()
	{
		// Same logical shape, different status code — must NOT compare as equal.
		var a = ParseStep( @"{
			""id"": ""guard"",
			""type"": ""condition"",
			""onFail"": { ""status"": 400, ""error"": ""BAD"" }
		}" );

		var b = ParseStep( @"{
			""id"": ""guard"",
			""type"": ""condition"",
			""onFail"": { ""status"": 403, ""error"": ""BAD"" }
		}" );

		var aJson = SerializeNormalized( a );
		var bJson = SerializeNormalized( b );
		Assert.AreNotEqual( aJson, bJson );
	}

	[TestMethod]
	public void NestedStepsAreNormalizedRecursively()
	{
		var legacy = ParseStep( @"{
			""id"": ""outer"",
			""type"": ""group"",
			""steps"": [
				{
					""id"": ""inner"",
					""type"": ""condition"",
					""onFail"": { ""status"": 409, ""error"": ""CONFLICT"" }
				}
			]
		}" );

		var canonical = ParseStep( @"{
			""id"": ""outer"",
			""type"": ""group"",
			""steps"": [
				{
					""id"": ""inner"",
					""type"": ""condition"",
					""routes"": {
						""false"": { ""action"": ""reject"", ""status"": 409, ""error"": ""CONFLICT"" }
					}
				}
			]
		}" );

		AssertSemanticallyEqual( legacy, canonical );
	}

	[TestMethod]
	public void NormalizeStepsAcceptsJsonElementArray()
	{
		var doc = JsonDocument.Parse( @"[
			{ ""id"": ""guard"", ""type"": ""condition"", ""onFail"": { ""status"": 400 } }
		]" );

		var result = SyncToolFlowCanonicalizer.NormalizeSteps( doc.RootElement );

		Assert.IsNotNull( result );
		Assert.AreEqual( 1, result.Count );
		var step = (Dictionary<string, object>)result[0];
		Assert.IsTrue( step.ContainsKey( "routes" ) );
		Assert.IsFalse( step.ContainsKey( "onFail" ) );
	}

	[TestMethod]
	public void NormalizeStepsReturnsNullForNonArrayInput()
	{
		Assert.IsNull( SyncToolFlowCanonicalizer.NormalizeSteps( null ) );
		Assert.IsNull( SyncToolFlowCanonicalizer.NormalizeSteps( "not-an-array" ) );
		Assert.IsNull( SyncToolFlowCanonicalizer.NormalizeSteps( 42 ) );
	}

	// ──────────────────────────────────────────────────────
	// Helpers — parse JSON into the dict shape the canonicalizer
	// accepts, then check post-normalization equivalence by
	// comparing canonical-form JSON.
	// ──────────────────────────────────────────────────────

	private static Dictionary<string, object> ParseStep( string json )
	{
		using var doc = JsonDocument.Parse( json );
		return ToDict( doc.RootElement );
	}

	private static Dictionary<string, object> ToDict( JsonElement el )
	{
		var dict = new Dictionary<string, object>();
		foreach ( var prop in el.EnumerateObject() )
			dict[prop.Name] = ToObject( prop.Value );
		return dict;
	}

	private static object ToObject( JsonElement el )
	{
		switch ( el.ValueKind )
		{
			case JsonValueKind.Object:
				return ToDict( el );
			case JsonValueKind.Array:
				return el.EnumerateArray().Select( ToObject ).ToList();
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

	private static string SerializeNormalized( Dictionary<string, object> step )
	{
		var normalized = SyncToolFlowCanonicalizer.NormalizeStepRoutes( step );
		// Run through the YAML renderer's underlying sort so two dicts with
		// the same content but different insertion order serialize identically.
		var json = JsonSerializer.Serialize( normalized );
		return SyncToolYamlRenderer.RenderFromJson( json );
	}

	private static void AssertSemanticallyEqual( Dictionary<string, object> a, Dictionary<string, object> b )
	{
		var aOut = SerializeNormalized( a );
		var bOut = SerializeNormalized( b );
		Assert.AreEqual( aOut, bOut, $"Expected canonicalized YAML to match.\n--- A ---\n{aOut}\n--- B ---\n{bOut}" );
	}
}
