import tempfile
import unittest
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "Editor"))

from endpoint_scripts import ScriptFileError, inline_script_sources, source_text_with_script  # noqa: E402
from source_compiler import parse_source_yaml  # noqa: E402


def endpoint_item(ns_dir, endpoint_id, source_text):
    return {
        "kind": "endpoint",
        "id": endpoint_id,
        "authoringMode": "source",
        "sourceFormat": "yaml",
        "sourcePath": f"endpoints/{endpoint_id}.endpoint.yml",
        "sourceText": source_text,
    }


class EndpointScriptTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.ns_dir = Path(self._tmp.name)
        self.endpoints = self.ns_dir / "endpoints"
        self.endpoints.mkdir()

    def tearDown(self):
        self._tmp.cleanup()

    def write(self, name, text, encoding="utf-8"):
        path = self.endpoints / name
        if isinstance(text, bytes):
            path.write_bytes(text)
        else:
            path.write_bytes(text.encode(encoding))
        return path

    def inlined_script(self, item):
        data, diagnostics = parse_source_yaml(item["sourceText"], item["sourcePath"])
        self.assertEqual([d for d in diagnostics if d["severity"] == "error"], [])
        return data.get("script")

    def test_javascript_file_is_inlined_and_reads_back_exactly(self):
        script = "export default function (ns) {\r\n  return \"  spaced\\t\";\r\n}\r\n"
        self.write("hello.endpoint.js", script)
        item = endpoint_item(self.ns_dir, "hello", "sourceVersion: 1\nkind: endpoint\nid: hello\nruntime: javascript\nscriptFile: hello.endpoint.js\n")

        [result] = inline_script_sources([item], self.ns_dir)

        self.assertEqual(self.inlined_script(result), script)
        self.assertIn("scriptFile: hello.endpoint.js", result["sourceText"])

    def test_typescript_file_with_bom_and_unicode_is_inlined(self):
        script = "\ufeffexport const label: string = \"caf\u00e9 \u2028 \u00a0\";\n\x7f\n"
        self.write("hello.endpoint.ts", script.encode("utf-8"))
        item = endpoint_item(self.ns_dir, "hello", "sourceVersion: 1\nid: hello\nruntime: typescript\n")

        [result] = inline_script_sources([item], self.ns_dir)

        self.assertEqual(self.inlined_script(result), script.lstrip("\ufeff"))

    def test_implicit_file_is_used_without_scriptFile(self):
        self.write("hello.endpoint.js", "return 1;\n")
        item = endpoint_item(self.ns_dir, "hello", "id: hello\nruntime: javascript\n")

        [result] = inline_script_sources([item], self.ns_dir)

        self.assertEqual(self.inlined_script(result), "return 1;\n")

    def test_yaml_only_endpoint_is_returned_unchanged(self):
        item = endpoint_item(self.ns_dir, "plain", "id: plain\nname: Plain\n")

        [result] = inline_script_sources([item], self.ns_dir)

        self.assertIs(result, item)

    def test_runtime_without_file_is_returned_unchanged(self):
        item = endpoint_item(self.ns_dir, "hello", "id: hello\nruntime: javascript\n")

        [result] = inline_script_sources([item], self.ns_dir)

        self.assertIs(result, item)

    def test_non_endpoint_items_are_not_touched(self):
        collection = {"kind": "collection", "id": "c", "sourcePath": "collections/c.collection.yml", "sourceText": "id: c\n"}

        [result] = inline_script_sources([collection], self.ns_dir)

        self.assertIs(result, collection)

    def test_inline_script_plus_file_is_an_error(self):
        self.write("hello.endpoint.js", "return 1;\n")
        item = endpoint_item(self.ns_dir, "hello", "id: hello\nruntime: javascript\nscript: \"return 2;\"\n")

        with self.assertRaisesRegex(ScriptFileError, "both inline and in hello.endpoint.js"):
            inline_script_sources([item], self.ns_dir)

    def test_missing_explicit_script_file_is_an_error(self):
        item = endpoint_item(self.ns_dir, "hello", "id: hello\nruntime: javascript\nscriptFile: missing.endpoint.js\n")

        with self.assertRaisesRegex(ScriptFileError, "missing.endpoint.js not found"):
            inline_script_sources([item], self.ns_dir)

    def test_scriptFile_with_folders_or_parent_references_is_rejected(self):
        for bad in ("../hello.endpoint.js", "sub/hello.endpoint.js", "..\\hello.endpoint.js", "..", "."):
            with self.subTest(scriptFile=bad):
                item = endpoint_item(self.ns_dir, "hello", f"id: hello\nruntime: javascript\nscriptFile: \"{bad.replace(chr(92), chr(92) * 2)}\"\n")
                with self.assertRaisesRegex(ScriptFileError, "without folders"):
                    inline_script_sources([item], self.ns_dir)

    def test_scriptFile_extension_must_match_runtime(self):
        self.write("hello.endpoint.ts", "const x = 1;\n")
        item = endpoint_item(self.ns_dir, "hello", "id: hello\nruntime: javascript\nscriptFile: hello.endpoint.ts\n")

        with self.assertRaisesRegex(ScriptFileError, "must end with .js"):
            inline_script_sources([item], self.ns_dir)

    def test_scriptFile_without_runtime_is_an_error(self):
        self.write("hello.endpoint.js", "return 1;\n")
        item = endpoint_item(self.ns_dir, "hello", "id: hello\nscriptFile: hello.endpoint.js\n")

        with self.assertRaisesRegex(ScriptFileError, "scriptFile needs runtime"):
            inline_script_sources([item], self.ns_dir)

    def test_stray_script_file_without_runtime_is_an_error(self):
        self.write("hello.endpoint.js", "return 1;\n")
        item = endpoint_item(self.ns_dir, "hello", "id: hello\n")

        with self.assertRaisesRegex(ScriptFileError, "runtime is not javascript or typescript"):
            inline_script_sources([item], self.ns_dir)

    def test_json_envelope_cannot_reference_a_script_file(self):
        self.write("hello.endpoint.js", "return 1;\n")
        item = endpoint_item(self.ns_dir, "hello", "id: hello\nruntime: javascript\n")
        item = {**item, "sourcePath": "endpoints/hello.endpoint.json"}

        with self.assertRaisesRegex(ScriptFileError, "need a .endpoint.yml envelope"):
            inline_script_sources([item], self.ns_dir)

    def test_both_js_and_ts_files_are_ambiguous(self):
        self.write("hello.endpoint.js", "return 1;\n")
        self.write("hello.endpoint.ts", "const x = 1;\n")
        item = endpoint_item(self.ns_dir, "hello", "id: hello\nruntime: javascript\n")

        with self.assertRaisesRegex(ScriptFileError, "remove one of the two files"):
            inline_script_sources([item], self.ns_dir)

    def test_non_utf8_script_is_an_error(self):
        self.write("hello.endpoint.js", b"return \xff;\n")
        item = endpoint_item(self.ns_dir, "hello", "id: hello\nruntime: javascript\n")

        with self.assertRaisesRegex(ScriptFileError, "not valid UTF-8"):
            inline_script_sources([item], self.ns_dir)

    def test_all_problems_are_reported_together(self):
        self.write("a.endpoint.js", "return 1;\n")
        self.write("b.endpoint.js", "return 2;\n")
        bad_a = endpoint_item(self.ns_dir, "a", "id: a\nruntime: javascript\nscriptFile: nope.endpoint.js\n")
        bad_b = {**endpoint_item(self.ns_dir, "b", "id: b\nruntime: javascript\nscript: \"x\"\n"), "sourcePath": "endpoints/b.endpoint.yml"}

        with self.assertRaises(ScriptFileError) as raised:
            inline_script_sources([bad_a, bad_b], self.ns_dir)

        message = str(raised.exception)
        self.assertIn("nope.endpoint.js not found", message)
        self.assertIn("both inline and in b.endpoint.js", message)

    def test_source_text_with_script_round_trips_escapes(self):
        script = "a\r\nb\u2028c\u2029d\x85e\x7f\ufeff\t\"\\\n"
        text = source_text_with_script("id: x\nruntime: javascript\n", script)

        data, diagnostics = parse_source_yaml(text, "endpoints/x.endpoint.yml")

        self.assertEqual([d for d in diagnostics if d["severity"] == "error"], [])
        self.assertEqual(data["script"], script)

    def test_source_text_without_trailing_newline_gets_one(self):
        text = source_text_with_script("id: x", "return 1;")

        self.assertEqual(text, 'id: x\nscript: "return 1;"\n')

    def test_wrapper_layout_endpoint_is_inlined_from_its_script_file(self):
        script = "return 1;\n"
        self.write("hello.endpoint.js", script)
        item = endpoint_item(self.ns_dir, "hello", "sourceVersion: 1\nkind: endpoint\nid: hello\ndefinition:\n  runtime: javascript\n  scriptFile: hello.endpoint.js\n  method: GET\n")

        [result] = inline_script_sources([item], self.ns_dir)

        self.assertEqual(self.inlined_script(result), script)


if __name__ == "__main__":
    unittest.main()
