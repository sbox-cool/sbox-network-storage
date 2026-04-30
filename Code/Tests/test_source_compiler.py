import json
import unittest
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "Editor"))

from source_compiler import (  # noqa: E402
    compile_legacy_json,
    compile_path,
    compile_source_text,
    export_legacy_json,
    is_stale,
    json_to_yaml_reverse_conversion,
    select_recompiled_plan,
)


class SourceCompilerTests(unittest.TestCase):
    def test_legacy_json_is_rejected(self):
        endpoint = {
            "slug": "mine-ore",
            "method": "POST",
            "steps": [
                {"id": "player", "type": "read", "collection": "players", "key": "{{steamId}}"},
                {"id": "save", "type": "write", "collection": "players", "key": "{{steamId}}", "ops": [{"op": "inc", "path": "ore", "value": 1}]},
            ],
        }

        compiled = compile_legacy_json(endpoint, "Editor/Network Storage/endpoints/mine-ore.json")

        self.assertFalse(compiled["ok"])
        self.assertEqual("unsupported-json", compiled["authoringMode"])
        self.assertTrue(any(d["code"] == "JSON_UNSUPPORTED" for d in compiled["diagnostics"]))
        exported, diagnostics = export_legacy_json(compiled)
        self.assertIsNone(exported)
        self.assertEqual("JSON_EXPORT_UNSUPPORTED", diagnostics[0]["code"])

    def test_source_yaml_route_walk_uses_native_nodes(self):
        path = ROOT / "Examples" / "SourceAuthoring" / "factory-route-walk.workflow.yml"
        compiled = compile_path(path)
        flat_types = json.dumps(compiled["executionPlan"])

        self.assertTrue(compiled["ok"])
        self.assertIn('"while"', flat_types)
        self.assertIn('"foreach"', flat_types)
        self.assertIn('"return"', flat_types)
        self.assertGreater(compiled["executionPlan"]["budgetEstimate"]["maxIterations"], 0)

        exported, diagnostics = export_legacy_json(compiled)
        self.assertIsNone(exported)
        self.assertEqual("JSON_EXPORT_UNSUPPORTED", diagnostics[0]["code"])

    def test_canonical_control_flow_nodes_and_budget_failure(self):
        source = """
sourceVersion: 1
kind: workflow
id: runtime-control-flow
definition:
  steps:
    - id: guard_block
      type: block
      body:
        - id: normalize
          type: call
          target: route.normalize
          input:
            routeId: "{{input.routeId}}"
        - id: each_node
          type: foreach
          from: "{{normalize.nodes}}"
          item: node
          maxIterations: 5
          body:
            - id: usable
              type: condition
              field: "{{node.enabled}}"
              operator: equals
              value: true
              then:
                - id: write_node
                  type: write
                  collection: routes
                  ops:
                    - op: set
                      path: "{{node.id}}"
                      value: "{{node}}"
        - id: wait_for_ready
          type: while
          condition: "{{state.loading}}"
          maxIterations: 3
          body:
            - id: poll_done
              type: until
              condition: "{{state.ready}}"
              maxIterations: 2
              body:
                - id: done
                  type: return
                  value:
                    ok: true
                    source: runtime
"""
        compiled = compile_source_text(source, "workflows/runtime-control-flow.workflow.yml")
        plan_json = json.dumps(compiled["executionPlan"])

        self.assertTrue(compiled["ok"])
        for node_type in ("block", "call", "foreach", "condition", "write", "while", "until", "return"):
            self.assertIn(f'"type": "{node_type}"', plan_json)
        self.assertEqual(10, compiled["executionPlan"]["budgetEstimate"]["maxIterations"])
        self.assertEqual(1, compiled["executionPlan"]["budgetEstimate"]["nestedCalls"])

        over_budget = source.replace("maxIterations: 3", "maxIterations: 1200")
        failed = compile_source_text(over_budget, "workflows/runtime-control-flow.workflow.yml")

        self.assertFalse(failed["ok"])
        self.assertTrue(any(
            d["code"] == "BUDGET_EXCEEDED" and d.get("budgetCategory") == "maxIterations"
            for d in failed["diagnostics"]
        ))

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

    def test_fingerprint_stale_detection_and_atomic_plan_swap(self):
        source = """
sourceVersion: 1
kind: workflow
id: check
definition:
  steps:
    - id: ok
      type: return
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
