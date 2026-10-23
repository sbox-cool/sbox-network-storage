using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sandbox;

[TestClass]
public class SyncToolScriptFilesTests
{
	private const string Header = "sourceVersion: 1\nkind: endpoint\nid: hello\n";

	private static string NewFolder()
	{
		var folder = Path.Combine( Path.GetTempPath(), "sync-script-files-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		return folder;
	}

	private static string Write( string folder, string name, string text )
	{
		var path = Path.Combine( folder, name );
		File.WriteAllText( path, text );
		return path;
	}

	[TestMethod]
	public void EncodedScriptReadsBackExactly()
	{
		var text = "a \"quoted\" \\ back\nline\ttab \u0001 \u0085 \u2028 bom\uFEFF end\r\n";
		var encoded = SyncToolConfig.EncodeYamlDoubleQuoted( text );

		Assert.AreEqual( text, JsonSerializer.Deserialize<string>( encoded ) );
	}

	[TestMethod]
	public void SplitMovesStringScriptToSiblingFile()
	{
		var data = new Dictionary<string, object> { ["runtime"] = "javascript", ["script"] = "return 1;\n", ["slug"] = "hello" };

		var envelope = SyncToolConfig.SplitEndpointScript( "hello", data, "endpoints/hello.endpoint.yml", null, out var fileName, out var scriptText );

		Assert.AreEqual( "hello.endpoint.js", fileName );
		Assert.AreEqual( "return 1;\n", scriptText );
		Assert.IsFalse( envelope.ContainsKey( "script" ) );
		Assert.AreEqual( "hello.endpoint.js", (string)envelope["scriptFile"] );
		Assert.AreEqual( "javascript", (string)envelope["runtime"] );
	}

	[TestMethod]
	public void SplitKeepsLocalScriptFileNameWhenItFitsTheRuntime()
	{
		var folder = NewFolder();
		var envelopePath = Write( folder, "hello.endpoint.yml", Header + "runtime: javascript\nscriptFile: custom-name.endpoint.js\n" );
		var data = new Dictionary<string, object> { ["runtime"] = "javascript", ["script"] = "return 2;" };

		var envelope = SyncToolConfig.SplitEndpointScript( "hello", data, envelopePath, out var fileName, out var scriptText );

		Assert.AreEqual( "custom-name.endpoint.js", fileName );
		Assert.AreEqual( "custom-name.endpoint.js", (string)envelope["scriptFile"] );
		Assert.AreEqual( "return 2;", scriptText );
	}

	[TestMethod]
	public void SplitFallsBackToDefaultNameWhenLocalNameDoesNotFitTheRuntime()
	{
		var folder = NewFolder();
		var envelopePath = Write( folder, "hello.endpoint.yml", Header + "runtime: javascript\nscriptFile: custom-name.endpoint.js\n" );
		var data = new Dictionary<string, object> { ["runtime"] = "typescript", ["script"] = "export {};" };

		var envelope = SyncToolConfig.SplitEndpointScript( "hello", data, envelopePath, out var fileName, out _ );

		Assert.AreEqual( "hello.endpoint.ts", fileName );
		Assert.AreEqual( "hello.endpoint.ts", (string)envelope["scriptFile"] );
	}

	[TestMethod]
	public void SplitAcceptsParsedJsonValues()
	{
		var data = JsonSerializer.Deserialize<Dictionary<string, object>>( "{\"runtime\":\"typescript\",\"script\":\"export {};\"}" );

		var envelope = SyncToolConfig.SplitEndpointScript( "hello", data, "endpoints/hello.endpoint.yml", null, out var fileName, out var scriptText );

		Assert.AreEqual( "hello.endpoint.ts", fileName );
		Assert.AreEqual( "export {};", scriptText );
		Assert.IsFalse( envelope.ContainsKey( "script" ) );
	}

	[TestMethod]
	public void SplitKeepsJsonEnvelopeScriptInline()
	{
		var data = new Dictionary<string, object> { ["runtime"] = "javascript", ["script"] = "return 1;" };

		var envelope = SyncToolConfig.SplitEndpointScript( "hello", data, "endpoints/hello.json", null, out var fileName, out var scriptText );

		Assert.IsNull( fileName );
		Assert.IsNull( scriptText );
		Assert.AreEqual( "return 1;", envelope["script"] );
	}

	[TestMethod]
	public void SplitLeavesEndpointWithoutScriptRuntimeAlone()
	{
		var data = new Dictionary<string, object> { ["script"] = "x" };

		var envelope = SyncToolConfig.SplitEndpointScript( "hello", data, "endpoints/hello.endpoint.yml", null, out var fileName, out _ );

		Assert.IsNull( fileName );
		Assert.AreEqual( "x", envelope["script"] );
	}

	[TestMethod]
	public void RawEndpointWithScriptIsSplitOnlyForYamlScriptRuntimes()
	{
		var yaml = Header + "runtime: javascript\nscript: \"return 1;\"\n";

		var split = SyncToolConfig.RawEndpointToSplit( yaml, "endpoints/hello.endpoint.yml" );
		Assert.IsNotNull( split );
		Assert.AreEqual( "return 1;", ((JsonElement)split["script"]).GetString() );

		Assert.IsNull( SyncToolConfig.RawEndpointToSplit( Header + "runtime: javascript\n", "endpoints/hello.endpoint.yml" ) );
		Assert.IsNull( SyncToolConfig.RawEndpointToSplit( yaml, "endpoints/hello.json" ) );
	}

	[TestMethod]
	public void ImplicitScriptFileIsInlinedAsScript()
	{
		var folder = NewFolder();
		try
		{
			var envelope = Write( folder, "hello.endpoint.yml", Header + "runtime: javascript\n" );
			Write( folder, "hello.endpoint.js", "return 1;\n" );

			var inlined = SyncToolConfig.InlineEndpointScriptSource( envelope, File.ReadAllText( envelope ), out var error );

			Assert.IsNull( error );
			Assert.AreEqual( Header + "runtime: javascript\nscript: \"return 1;\\n\"\n", inlined );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void EndpointScriptFileForNamesTheImplicitSibling()
	{
		var folder = NewFolder();
		try
		{
			var envelope = Write( folder, "hello.endpoint.yml", Header + "runtime: typescript\n" );

			Assert.AreEqual( Path.Combine( folder, "hello.endpoint.ts" ), SyncToolConfig.EndpointScriptFileFor( envelope ) );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void ScriptFileWithFoldersIsRejected()
	{
		var folder = NewFolder();
		try
		{
			var text = Header + "runtime: javascript\nscriptFile: sub/hello.endpoint.js\n";
			var envelope = Write( folder, "hello.endpoint.yml", text );

			var inlined = SyncToolConfig.InlineEndpointScriptSource( envelope, text, out var error );

			StringAssert.Contains( error, "without folders" );
			Assert.AreEqual( text, inlined );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void ScriptFileMustEndWithRuntimeExtension()
	{
		var folder = NewFolder();
		try
		{
			var text = Header + "runtime: typescript\nscriptFile: hello.endpoint.js\n";
			var envelope = Write( folder, "hello.endpoint.yml", text );
			Write( folder, "hello.endpoint.js", "return 1;" );

			SyncToolConfig.InlineEndpointScriptSource( envelope, text, out var error );

			StringAssert.Contains( error, "must end with .ts" );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void ScriptFileWithoutRuntimeIsRejected()
	{
		var folder = NewFolder();
		try
		{
			var text = Header + "scriptFile: hello.endpoint.js\n";
			var envelope = Write( folder, "hello.endpoint.yml", text );
			Write( folder, "hello.endpoint.js", "return 1;" );

			SyncToolConfig.InlineEndpointScriptSource( envelope, text, out var error );

			StringAssert.Contains( error, "needs runtime" );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void StrayScriptWithoutRuntimeIsReported()
	{
		var folder = NewFolder();
		try
		{
			var text = Header + "slug: hello\n";
			var envelope = Write( folder, "hello.endpoint.yml", text );
			Write( folder, "hello.endpoint.js", "return 1;" );

			var inlined = SyncToolConfig.InlineEndpointScriptSource( envelope, text, out var error );

			StringAssert.Contains( error, "runtime is not javascript or typescript" );
			Assert.AreEqual( text, inlined );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void JsonEnvelopeCannotReferenceScriptFile()
	{
		var folder = NewFolder();
		try
		{
			var text = "{\"sourceVersion\":1,\"kind\":\"endpoint\",\"id\":\"hello\",\"runtime\":\"javascript\"}";
			var envelope = Write( folder, "hello.json", text );
			Write( folder, "hello.endpoint.js", "return 1;" );

			var inlined = SyncToolConfig.InlineEndpointScriptSource( envelope, text, out var error );

			StringAssert.Contains( error, "JSON envelope" );
			Assert.AreEqual( text, inlined );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void JavaScriptAndTypeScriptFilesAreAmbiguous()
	{
		var folder = NewFolder();
		try
		{
			var text = Header + "runtime: javascript\n";
			var envelope = Write( folder, "hello.endpoint.yml", text );
			Write( folder, "hello.endpoint.js", "return 1;" );
			Write( folder, "hello.endpoint.ts", "export {};" );

			SyncToolConfig.InlineEndpointScriptSource( envelope, text, out var error );

			StringAssert.Contains( error, "remove one of the two files" );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void InlineScriptAndScriptFileConflict()
	{
		var folder = NewFolder();
		try
		{
			var text = Header + "runtime: javascript\nscript: \"old\"\n";
			var envelope = Write( folder, "hello.endpoint.yml", text );
			Write( folder, "hello.endpoint.js", "return 1;" );

			SyncToolConfig.InlineEndpointScriptSource( envelope, text, out var error );

			StringAssert.Contains( error, "set both inline and in" );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void MissingScriptFileIsLeftForTheServer()
	{
		var folder = NewFolder();
		try
		{
			var text = Header + "runtime: javascript\n";
			var envelope = Write( folder, "hello.endpoint.yml", text );

			var inlined = SyncToolConfig.InlineEndpointScriptSource( envelope, text, out var error );

			Assert.IsNull( error );
			Assert.AreEqual( text, inlined );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void YamlOnlyEndpointIsUnchanged()
	{
		var folder = NewFolder();
		try
		{
			var text = Header + "slug: hello\nmethod: GET\n";
			var envelope = Write( folder, "hello.endpoint.yml", text );

			var inlined = SyncToolConfig.InlineEndpointScriptSource( envelope, text, out var error );

			Assert.IsNull( error );
			Assert.AreEqual( text, inlined );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	[TestMethod]
	public void WithoutPropertyDropsOnlyThatProperty()
	{
		var obj = JsonSerializer.Deserialize<JsonElement>( "{\"scriptFile\":\"hello.endpoint.js\",\"runtime\":\"javascript\"}" );

		var stripped = SyncToolConfig.WithoutProperty( obj, "scriptFile" );

		Assert.IsFalse( stripped.TryGetProperty( "scriptFile", out _ ) );
		Assert.AreEqual( "javascript", stripped.GetProperty( "runtime" ).GetString() );
	}
}
