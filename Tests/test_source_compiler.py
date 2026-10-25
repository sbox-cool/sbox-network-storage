import json
import tempfile
import unittest
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "Editor"))

from source_compiler import (  # noqa: E402
    CANONICAL_STEP_TYPES,
    UNSUPPORTED_STEP_TYPES,
    compile_legacy_json,
    compile_path,
    compile_source_text,
    export_legacy_json,
    is_stale,
    json_to_yaml_reverse_conversion,
    select_recompiled_plan,
)
from sync_sources import load_source_definitions  # noqa: E402
import sync  # noqa: E402


class SourceCompilerTests(unittest.TestCase):
    def test_legacy_json_still_compiles_for_compatibility(self):
        endpoint = {
            "slug": "mine-ore",
            "method": "POST",
            "steps": [
                {"id": "player", "type": "read", "collection": "players", "key": "{{steamId}}"},
                {"id": "save", "type": "write", "collection": "players", "key": "{{steamId}}", "ops": [{"op": "inc", "path": "ore", "value": 1}]},
            ],
        }

        compiled = compile_legacy_json(endpoint, "Editor/Network Storage/endpoints/mine-ore.endpoint.json")

        self.assertTrue(compiled["ok"])
        self.assertEqual("legacy-json", compiled["authoringMode"])
        self.assertEqual("json", compiled["sourceFormat"])
        exported, diagnostics = export_legacy_json(compiled)
        self.assertEqual([], diagnostics)
        self.assertEqual("mine-ore", exported["slug"])

    def test_flat_endpoint_exposure_and_routes_compile(self):
        source = """
sourceVersion: 1
kind: endpoint
id: debit-currency
name: Debit Currency
exposure: internal
params:
  amount:
    type: number
steps:
  - id: validate_amount
    type: condition
    check:
      field: "{{params.amount}}"
      op: ">"
      value: 0
    routes:
      false:
        action: reject
        status: 400
        error: INVALID_AMOUNT
        message: Amount must be positive.
  - id: wait
    type: sleep
    ms: 250
response:
  status: 200
  body:
    ok: true
"""
        compiled = compile_source_text(source, "endpoints/debit-currency.yml")

        self.assertTrue(compiled["ok"], compiled["diagnostics"])
        canonical = compiled["canonicalDefinition"]
        self.assertEqual("debit-currency", canonical["slug"])
        self.assertEqual("internal", canonical["exposure"])
        self.assertEqual(250, compiled["executionPlan"]["budgetEstimate"]["maxSleepMs"])

    def test_unsupported_loop_example_is_rejected(self):
        path = ROOT / "Examples" / "SourceAuthoring" / "Invalid" / "unsupported-loop-steps.workflow.yml"
        compiled = compile_path(path)
        flagged = [d for d in compiled["diagnostics"] if d["code"] == "INVALID_STEP_TYPE"]

        self.assertFalse(compiled["ok"])
        self.assertEqual(2, len(flagged), compiled["diagnostics"])
        self.assertIn('"while"', flagged[0]["message"])
        self.assertIn('"return"', flagged[1]["message"])

    def test_unsupported_control_flow_step_types_are_flagged(self):
        source = """
sourceVersion: 1
kind: workflow
id: runtime-control-flow
steps:
  - id: normalize
    type: call
    target: route.normalize
  - id: each_node
    type: foreach
    from: "{{normalize.nodes}}"
    item: node
    maxIterations: 5
    body: []
  - id: wait_for_ready
    type: while
    condition: "{{state.loading}}"
    maxIterations: 3
    body: []
  - id: poll_done
    type: until
    condition: "{{state.ready}}"
    maxIterations: 2
    body: []
  - id: done
    type: return
    value:
      ok: true
  - id: done_on
    type: return_on
    value:
      ok: true
"""
        compiled = compile_source_text(source, "workflows/runtime-control-flow.workflow.yml")
        flagged = [d for d in compiled["diagnostics"] if d["code"] == "INVALID_STEP_TYPE"]

        self.assertFalse(compiled["ok"])
        self.assertEqual(6, len(flagged))
        for node_type in ("call", "foreach", "while", "until", "return", "return_on"):
            self.assertTrue(any(f'"{node_type}"' in d["message"] for d in flagged), node_type)

    def test_sleep_budget_failure_is_reported(self):
        source = """
sourceVersion: 1
kind: workflow
id: sleepy
steps:
  - id: pause
    type: sleep
    ms: 250
"""
        self.assertTrue(compile_source_text(source, "workflows/sleepy.workflow.yml")["ok"])
        failed = compile_source_text(source, "workflows/sleepy.workflow.yml", budget_limits={"maxSleepMs": 100})

        self.assertFalse(failed["ok"])
        self.assertTrue(any(
            d["code"] == "BUDGET_EXCEEDED" and d.get("budgetCategory") == "maxSleepMs"
            for d in failed["diagnostics"]
        ))

    def test_schema_step_types_match_compiler_and_exclude_unsupported_types(self):
        schema = json.loads((ROOT / "source-authoring.schema.json").read_text(encoding="utf-8"))
        schema_types = set(schema["$defs"]["step"]["properties"]["type"]["enum"])

        self.assertEqual(CANONICAL_STEP_TYPES, schema_types)
        self.assertFalse(schema_types & UNSUPPORTED_STEP_TYPES)

    def test_invalid_yaml_anchor_rejected(self):
        path = ROOT / "Examples" / "SourceAuthoring" / "Invalid" / "unsupported-anchor.workflow.yml"
        compiled = compile_path(path)

        self.assertFalse(compiled["ok"])
        self.assertTrue(any(d["code"] == "UNSUPPORTED_YAML_ANCHOR" for d in compiled["diagnostics"]))

    def test_invalid_kind_and_missing_version_rejected(self):
        invalid_kind = compile_path(ROOT / "Examples" / "SourceAuthoring" / "Invalid" / "invalid-kind.collection.yml")
        missing_version = compile_path(ROOT / "Examples" / "SourceAuthoring" / "Invalid" / "missing-source-version.endpoint.yml")

        self.assertFalse(invalid_kind["ok"])
        self.assertTrue(any(d["code"] == "INVALID_RESOURCE_KIND" for d in invalid_kind["diagnostics"]))
        self.assertFalse(missing_version["ok"])
        self.assertTrue(any(d["code"] == "MISSING_FIELD" for d in missing_version["diagnostics"]))

    def test_json_to_yaml_reverse_conversion_is_rejected(self):
        with self.assertRaises(NotImplementedError):
            json_to_yaml_reverse_conversion()

    def test_plain_yml_files_are_discovered_inside_kind_folders(self):
        with tempfile.TemporaryDirectory() as tmp:
            ns = Path(tmp) / "Editor" / "Network Storage"
            endpoints = ns / "endpoints"
            endpoints.mkdir(parents=True)
            (endpoints / "plain-endpoint.yml").write_text("""
sourceVersion: 1
kind: endpoint
id: plain-endpoint
exposure: public
method: POST
steps: []
response:
  status: 200
  body:
    ok: true
""", encoding="utf-8")

            sources = load_source_definitions(ns)

        self.assertEqual(1, len(sources))
        self.assertEqual("plain-endpoint", sources[0]["id"])
        self.assertEqual("endpoint", sources[0]["kind"])

    def test_backend_source_upgrade_safely_rewrites_and_returns_canonical(self):
        with tempfile.TemporaryDirectory() as tmp:
            ns = Path(tmp)
            endpoint_dir = ns / "endpoints"
            endpoint_dir.mkdir()
            source_path = endpoint_dir / "wrapped.endpoint.yml"
            source_path.write_text("old", encoding="utf-8")
            sync.NS_DIR = ns

            captured = {}
            original_api_request = sync.api_request

            def fake_api_request(config, method, path, body=None):
                captured.update({"method": method, "path": path, "body": body})
                return {
                    "ok": True,
                    "canonicalDefinition": {"slug": "wrapped", "exposure": "internal", "steps": []},
                    "upgradedSourceText": "sourceVersion: 1\nkind: endpoint\nid: wrapped\nexposure: internal\nsteps: []\n",
                    "safeAutoWrite": True,
                }

            try:
                sync.api_request = fake_api_request
                canonical = sync.upgrade_source_with_backend({}, {
                    "kind": "endpoint",
                    "id": "wrapped",
                    "sourceFormat": "yaml",
                    "sourcePath": "endpoints/wrapped.endpoint.yml",
                    "sourceText": "old",
                })
            finally:
                sync.api_request = original_api_request
                sync.NS_DIR = None

            self.assertEqual("POST", captured["method"])
            self.assertEqual("source-upgrade", captured["path"])
            self.assertEqual("internal", canonical["exposure"])
            self.assertIn("exposure: internal", source_path.read_text(encoding="utf-8"))


    def test_fingerprint_stale_detection_and_atomic_plan_swap(self):
        source = """
sourceVersion: 1
kind: workflow
id: check
steps:
  - id: ok
    type: object
    value:
      ok: true
"""
        previous = compile_source_text(source, "workflows/check.workflow.yml")
        self.assertFalse(is_stale(previous, source_text=source, dependencies=[]))

        changed_compiler = dict(previous["compilerFingerprint"])
        changed_compiler["compilerVersion"] = "0.2.0"
        self.assertTrue(is_stale(previous, source_text=source, dependencies=[], compiler_fingerprint=changed_compiler))

        updated = compile_source_text(source.replace("ok: true", "ok: false"), "workflows/check.workflow.yml")
        self.assertEqual("updated", select_recompiled_plan(previous, updated)["status"])

        failed = compile_source_text(source.replace("sourceVersion: 1", ""), "workflows/check.workflow.yml")
        decision = select_recompiled_plan(previous, failed)
        self.assertEqual("recompile_failed", decision["status"])
        self.assertEqual(previous["executionPlanHash"], decision["activePlan"]["executionPlanHash"])


if __name__ == "__main__":
    unittest.main()
