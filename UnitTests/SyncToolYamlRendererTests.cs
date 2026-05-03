using Sandbox;

[TestClass]
public class SyncToolYamlRendererTests
{
	[TestMethod]
	public void EmptyInputProducesEmptyOutput()
	{
		Assert.AreEqual( "", SyncToolYamlRenderer.RenderFromJson( null ) );
		Assert.AreEqual( "", SyncToolYamlRenderer.RenderFromJson( "" ) );
	}

	[TestMethod]
	public void EmptyObjectAndArrayRenderAsFlowStyle()
	{
		Assert.AreEqual( "{}\n", SyncToolYamlRenderer.RenderFromJson( "{}" ) );
		Assert.AreEqual( "[]\n", SyncToolYamlRenderer.RenderFromJson( "[]" ) );
	}

	[TestMethod]
	public void InvalidJsonReturnsInputUnchanged()
	{
		var notJson = "not: real: json: ::";
		Assert.AreEqual( notJson, SyncToolYamlRenderer.RenderFromJson( notJson ) );
	}

	[TestMethod]
	public void TopLevelKeysAreSortedAlphabetically()
	{
		var json = "{\"zeta\":1,\"alpha\":2,\"mu\":3}";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		Assert.AreEqual( "alpha: 2\nmu: 3\nzeta: 1\n", yaml );
	}

	[TestMethod]
	public void NestedObjectsAreSortedRecursively()
	{
		var json = "{\"outer\":{\"zeta\":1,\"alpha\":2}}";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		Assert.AreEqual( "outer:\n  alpha: 2\n  zeta: 1\n", yaml );
	}

	[TestMethod]
	public void DifferentKeyOrdersProduceIdenticalOutput()
	{
		var a = "{\"slug\":\"hello\",\"method\":\"POST\",\"enabled\":true}";
		var b = "{\"enabled\":true,\"method\":\"POST\",\"slug\":\"hello\"}";

		Assert.AreEqual(
			SyncToolYamlRenderer.RenderFromJson( a ),
			SyncToolYamlRenderer.RenderFromJson( b )
		);
	}

	[TestMethod]
	public void OutputUsesYamlSyntaxNotJsonSyntax()
	{
		var json = "{\"name\":\"hooked\",\"enabled\":true,\"max\":42}";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		// Smoke check: the rendered text must look like YAML, not JSON.
		// This is the regression we're guarding: prior to the fix the diff
		// view rendered structured data as JSON.
		StringAssert.DoesNotMatch( yaml, new System.Text.RegularExpressions.Regex( @"^\s*\{" ) );
		StringAssert.Contains( yaml, "enabled: true" );
		StringAssert.Contains( yaml, "max: 42" );
		StringAssert.Contains( yaml, "name: \"hooked\"" );
	}

	[TestMethod]
	public void StringValuesAreQuotedAndEscapedSafely()
	{
		var json = "{\"text\":\"a:b\\nc\"}";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		// Strings go through JSON quoting which is also valid YAML.
		// The colon/newline must not leak as YAML structure.
		Assert.AreEqual( "text: \"a:b\\nc\"\n", yaml );
	}

	[TestMethod]
	public void BooleanAndNullAreUnquoted()
	{
		var json = "{\"a\":true,\"b\":false,\"c\":null}";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		Assert.AreEqual( "a: true\nb: false\nc: null\n", yaml );
	}

	[TestMethod]
	public void IntegerAndDoubleAreUnquoted()
	{
		var json = "{\"i\":7,\"d\":1.5}";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		Assert.AreEqual( "d: 1.5\ni: 7\n", yaml );
	}

	[TestMethod]
	public void ArraysOfObjectsRenderAsBlockSequence()
	{
		var json = "{\"steps\":[{\"name\":\"first\",\"id\":1},{\"name\":\"second\",\"id\":2}]}";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		var expected =
			"steps:\n" +
			"  -\n" +
			"    id: 1\n" +
			"    name: \"first\"\n" +
			"  -\n" +
			"    id: 2\n" +
			"    name: \"second\"\n";

		Assert.AreEqual( expected, yaml );
	}

	[TestMethod]
	public void ArraysOfScalarsRenderAsBlockSequence()
	{
		var json = "{\"tags\":[\"a\",\"b\",\"c\"]}";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		Assert.AreEqual( "tags:\n  - \"a\"\n  - \"b\"\n  - \"c\"\n", yaml );
	}

	[TestMethod]
	public void TopLevelArrayRenders()
	{
		var json = "[1,2,3]";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		Assert.AreEqual( "- 1\n- 2\n- 3\n", yaml );
	}

	[TestMethod]
	public void KeysWithNonIdentifierCharactersAreQuoted()
	{
		var json = "{\"weird key\":1,\"x:y\":2}";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		// Bare YAML keys must not contain spaces or colons, so the renderer
		// quotes them. Sort order is by raw key (Ordinal).
		StringAssert.Contains( yaml, "\"weird key\": 1" );
		StringAssert.Contains( yaml, "\"x:y\": 2" );
	}

	[TestMethod]
	public void EmptyNestedObjectAndArrayUseFlowStyle()
	{
		var json = "{\"obj\":{},\"arr\":[]}";
		var yaml = SyncToolYamlRenderer.RenderFromJson( json );

		Assert.AreEqual( "arr: []\nobj: {}\n", yaml );
	}
}
