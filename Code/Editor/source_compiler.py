#!/usr/bin/env python3
"""Reference compiler for Network Storage source authoring.

This is the local/tooling mirror of the backend compiler contract. It validates
strict YAML source files and legacy JSON resources, emits deterministic
canonical definitions, builds canonical execution-plan metadata, and records
fingerprints/hashes used for stale-plan detection.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import re
import sys
from pathlib import Path
from typing import Any

try:
    import yaml
    from yaml.constructor import ConstructorError
    from yaml.resolver import BaseResolver
    from yaml.tokens import AliasToken, AnchorToken, TagToken
except Exception as exc:  # pragma: no cover - exercised only when PyYAML is absent.
    yaml = None
    ConstructorError = Exception
    BaseResolver = None
    AliasToken = AnchorToken = TagToken = ()  # type: ignore
    YAML_IMPORT_ERROR = exc
else:
    YAML_IMPORT_ERROR = None


RESOURCE_KINDS = {"collection", "endpoint", "workflow", "test", "library"}
SOURCE_VERSION = 1
SOURCE_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
LEGACY_STEP_TYPES = {"read", "write", "transform", "condition", "lookup", "filter", "workflow", "compute", "random", "random_select"}
NATIVE_STEP_TYPES = {"block", "while", "until", "foreach", "call", "return"}
CANONICAL_STEP_TYPES = LEGACY_STEP_TYPES | NATIVE_STEP_TYPES
DEFAULT_BUDGET_LIMITS = {
    "compiledNodes": 500,
    "reads": 50,
    "filters": 50,
    "writes": 100,
    "writeOps": 250,
    "maxIterations": 1000,
    "nestedCalls": 32,
    "debugTraceBytes": 65536,
}

COMPILER_FINGERPRINT = {
    "compilerPackage": "sboxcool.network-storage.source-compiler",
    "compilerVersion": "0.1.0",
    "compilerCommit": "local",
    "sourceSchemaVersion": SOURCE_VERSION,
    "canonicalModelVersion": 1,
    "executionPlanVersion": 1,
    "legacyJsonAdapterVersion": 1,
}


class StrictSafeLoader(yaml.SafeLoader if yaml else object):
    pass


def _strict_mapping(loader: StrictSafeLoader, node: Any, deep: bool = False) -> dict[str, Any]:
    mapping: dict[str, Any] = {}
    for key_node, value_node in node.value:
        if getattr(key_node, "value", None) == "<<":
            raise ConstructorError("while constructing a mapping", node.start_mark, "merge keys are not supported", key_node.start_mark)
        key = loader.construct_object(key_node, deep=deep)
        if key in mapping:
            raise ConstructorError("while constructing a mapping", node.start_mark, f"duplicate key: {key}", key_node.start_mark)
        mapping[key] = loader.construct_object(value_node, deep=deep)
    return mapping


if yaml:
    StrictSafeLoader.add_constructor(BaseResolver.DEFAULT_MAPPING_TAG, _strict_mapping)


def diagnostic(severity: str, code: str, message: str, **context: Any) -> dict[str, Any]:
    item = {"severity": severity, "code": code, "message": message}
    item.update({k: v for k, v in context.items() if v is not None})
    return item


def stable_json(value: Any) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def stable_hash(value: Any) -> str:
    if isinstance(value, str):
        data = value.encode("utf-8")
    else:
        data = stable_json(value).encode("utf-8")
    return hashlib.sha256(data).hexdigest()


def compiler_fingerprint_hash(fingerprint: dict[str, Any] | None = None) -> str:
    return stable_hash(fingerprint or COMPILER_FINGERPRINT)


def _scan_unsupported_yaml(text: str, source_path: str) -> list[dict[str, Any]]:
    if yaml is None:
        return [diagnostic("error", "YAML_UNAVAILABLE", f"PyYAML is required: {YAML_IMPORT_ERROR}", sourcePath=source_path)]

    diagnostics: list[dict[str, Any]] = []
    try:
        for token in yaml.scan(text):
            if isinstance(token, AnchorToken):
                diagnostics.append(diagnostic("error", "UNSUPPORTED_YAML_ANCHOR", "YAML anchors are not supported.", sourcePath=source_path, line=token.start_mark.line + 1, column=token.start_mark.column + 1))
            elif isinstance(token, AliasToken):
                diagnostics.append(diagnostic("error", "UNSUPPORTED_YAML_ALIAS", "YAML aliases are not supported.", sourcePath=source_path, line=token.start_mark.line + 1, column=token.start_mark.column + 1))
            elif isinstance(token, TagToken):
                diagnostics.append(diagnostic("error", "UNSUPPORTED_YAML_TAG", "YAML tags are not supported.", sourcePath=source_path, line=token.start_mark.line + 1, column=token.start_mark.column + 1))
    except Exception as exc:
        diagnostics.append(diagnostic("error", "INVALID_YAML", str(exc), sourcePath=source_path))
    return diagnostics


def parse_source_yaml(text: str, source_path: str) -> tuple[Any | None, list[dict[str, Any]]]:
    diagnostics = _scan_unsupported_yaml(text, source_path)
    if any(d["severity"] == "error" for d in diagnostics):
        return None, diagnostics
    try:
        data = yaml.load(text, Loader=StrictSafeLoader)
    except Exception as exc:
        return None, [diagnostic("error", "INVALID_YAML", str(exc), sourcePath=source_path)]
    return data, diagnostics


def _resource_id_from_path(path: str, kind: str) -> str | None:
    name = Path(path).name
    for suffix in (f".{kind}.yaml", f".{kind}.yml", ".json"):
        if name.endswith(suffix):
            return name[: -len(suffix)]
    return None


def validate_source_object(data: Any, source_path: str = "") -> list[dict[str, Any]]:
    diags: list[dict[str, Any]] = []
    if not isinstance(data, dict):
        return [diagnostic("error", "INVALID_SOURCE_OBJECT", "Source must be a YAML mapping.", sourcePath=source_path)]

    for key in ("sourceVersion", "kind", "id", "definition"):
        if key not in data:
            diags.append(diagnostic("error", "MISSING_FIELD", f"Missing required field: {key}.", sourcePath=source_path, sourcePointer=f"/{key}"))

    extra = set(data) - {"sourceVersion", "kind", "id", "name", "description", "notes", "imports", "metadata", "definition"}
    for key in sorted(extra):
        diags.append(diagnostic("warning", "UNKNOWN_TOP_LEVEL_FIELD", f"Unknown top-level source field: {key}.", sourcePath=source_path, sourcePointer=f"/{key}"))

    if data.get("sourceVersion") != SOURCE_VERSION:
        diags.append(diagnostic("error", "UNSUPPORTED_SOURCE_VERSION", f"sourceVersion must be {SOURCE_VERSION}.", sourcePath=source_path, sourcePointer="/sourceVersion"))

    kind = data.get("kind")
    if kind not in RESOURCE_KINDS:
        diags.append(diagnostic("error", "INVALID_RESOURCE_KIND", f"kind must be one of: {', '.join(sorted(RESOURCE_KINDS))}.", sourcePath=source_path, sourcePointer="/kind"))

    resource_id = data.get("id")
    if not isinstance(resource_id, str) or not SOURCE_ID_PATTERN.match(resource_id):
        diags.append(diagnostic("error", "INVALID_RESOURCE_ID", "id must start with an alphanumeric character and contain only letters, numbers, _, ., or -.", sourcePath=source_path, sourcePointer="/id"))

    if kind in RESOURCE_KINDS:
        path_id = _resource_id_from_path(source_path, kind)
        if path_id and resource_id and path_id != resource_id:
            diags.append(diagnostic("warning", "FILE_ID_MISMATCH", f"File name id '{path_id}' differs from source id '{resource_id}'.", sourcePath=source_path))
        if source_path and f".{kind}." not in Path(source_path).name:
            diags.append(diagnostic("error", "FILE_KIND_MISMATCH", f"File name must include .{kind}.yaml or .{kind}.yml.", sourcePath=source_path))

    imports = data.get("imports", [])
    if imports is None:
        imports = []
    if not isinstance(imports, list) or any(not isinstance(x, str) for x in imports):
        diags.append(diagnostic("error", "INVALID_IMPORTS", "imports must be an array of strings.", sourcePath=source_path, sourcePointer="/imports"))
    elif len(imports) != len(set(imports)):
        diags.append(diagnostic("error", "DUPLICATE_IMPORT", "imports must not contain duplicate entries.", sourcePath=source_path, sourcePointer="/imports"))

    definition = data.get("definition")
    if not isinstance(definition, dict):
        diags.append(diagnostic("error", "INVALID_DEFINITION", "definition must be a mapping.", sourcePath=source_path, sourcePointer="/definition"))
    else:
        diags.extend(validate_definition(kind, definition, source_path))

    return diags


def validate_definition(kind: str, definition: dict[str, Any], source_path: str) -> list[dict[str, Any]]:
    diags: list[dict[str, Any]] = []
    if kind == "endpoint" and definition.get("method", "POST") not in ("GET", "POST"):
        diags.append(diagnostic("error", "INVALID_METHOD", "Endpoint method must be GET or POST.", sourcePath=source_path, sourcePointer="/definition/method"))
    if kind == "collection" and not any(k in definition for k in ("schema", "fields", "tables", "constants")):
        diags.append(diagnostic("warning", "EMPTY_COLLECTION_DEFINITION", "Collection source has no schema, fields, tables, or constants.", sourcePath=source_path, sourcePointer="/definition"))
    if "steps" in definition:
        if not isinstance(definition["steps"], list):
            diags.append(diagnostic("error", "INVALID_STEPS", "steps must be an array.", sourcePath=source_path, sourcePointer="/definition/steps"))
        else:
            diags.extend(validate_steps(definition["steps"], source_path, "/definition/steps"))
    if kind == "library":
        for block_id, block in (definition.get("blocks") or {}).items():
            steps = block.get("steps") if isinstance(block, dict) else None
            if steps is not None:
                diags.extend(validate_steps(steps, source_path, f"/definition/blocks/{block_id}/steps"))
    return diags


def validate_steps(steps: Any, source_path: str, pointer: str) -> list[dict[str, Any]]:
    diags: list[dict[str, Any]] = []
    if not isinstance(steps, list):
        return [diagnostic("error", "INVALID_STEPS", "steps must be an array.", sourcePath=source_path, sourcePointer=pointer)]
    ids: set[str] = set()
    for index, step in enumerate(steps):
        step_pointer = f"{pointer}/{index}"
        if not isinstance(step, dict):
            diags.append(diagnostic("error", "INVALID_STEP", "Step must be a mapping.", sourcePath=source_path, sourcePointer=step_pointer))
            continue
        step_id = step.get("id")
        step_type = step.get("type")
        if not isinstance(step_id, str) or not step_id:
            diags.append(diagnostic("error", "MISSING_STEP_ID", "Step requires an id.", sourcePath=source_path, sourcePointer=f"{step_pointer}/id"))
        elif step_id in ids:
            diags.append(diagnostic("warning", "DUPLICATE_STEP_ID", f"Step id is reused and will map to a unique canonical node: {step_id}.", sourcePath=source_path, sourcePointer=f"{step_pointer}/id"))
        else:
            ids.add(step_id)
        if step_type not in CANONICAL_STEP_TYPES:
            diags.append(diagnostic("error", "INVALID_STEP_TYPE", f"Step type must be one of: {', '.join(sorted(CANONICAL_STEP_TYPES))}.", sourcePath=source_path, sourcePointer=f"{step_pointer}/type"))
            continue
        if step_type in ("while", "until", "foreach"):
            if not isinstance(step.get("maxIterations"), int) or step.get("maxIterations") < 1:
                diags.append(diagnostic("error", "MISSING_MAX_ITERATIONS", f"{step_type} requires maxIterations >= 1.", sourcePath=source_path, sourcePointer=f"{step_pointer}/maxIterations"))
            if not isinstance(step.get("body"), list):
                diags.append(diagnostic("error", "MISSING_LOOP_BODY", f"{step_type} requires a body array.", sourcePath=source_path, sourcePointer=f"{step_pointer}/body"))
        if step_type == "foreach":
            for key in ("from", "item"):
                if key not in step:
                    diags.append(diagnostic("error", "MISSING_FOREACH_FIELD", f"foreach requires {key}.", sourcePath=source_path, sourcePointer=f"{step_pointer}/{key}"))
        if step_type == "call" and not step.get("target"):
            diags.append(diagnostic("error", "MISSING_CALL_TARGET", "call requires target.", sourcePath=source_path, sourcePointer=f"{step_pointer}/target"))
        for child_key in ("body", "then", "else"):
            if child_key in step and isinstance(step[child_key], list):
                diags.extend(validate_steps(step[child_key], source_path, f"{step_pointer}/{child_key}"))
    return diags


def _copy_known(data: dict[str, Any], keys: list[str]) -> dict[str, Any]:
    return {key: copy.deepcopy(data[key]) for key in keys if key in data}


def canonical_definition_from_source(source: dict[str, Any]) -> dict[str, Any]:
    kind = source["kind"]
    definition = copy.deepcopy(source["definition"])
    if kind == "endpoint":
        canonical = _copy_known(source, ["id", "name", "description", "notes", "metadata"])
        canonical["slug"] = source["id"]
        canonical.update(definition)
    elif kind == "collection":
        canonical = _copy_known(source, ["id", "name", "description", "notes", "metadata"])
        canonical["name"] = source["id"]
        canonical.update(definition)
    elif kind == "workflow":
        canonical = _copy_known(source, ["id", "name", "description", "notes", "metadata"])
        canonical.update(definition)
    else:
        canonical = _copy_known(source, ["id", "name", "description", "notes", "metadata"])
        canonical["definition"] = definition
    return canonical


def canonical_definition_from_legacy(data: dict[str, Any]) -> dict[str, Any]:
    return json.loads(stable_json(data))


def infer_legacy_kind(data: dict[str, Any], source_path: str = "") -> str:
    path = source_path.replace("\\", "/")
    if "/collections/" in path or "schema" in data or "collectionType" in data:
        return "collection"
    if "/endpoints/" in path or "slug" in data or "method" in data:
        return "endpoint"
    if "/workflows/" in path or ("id" in data and ("condition" in data or "steps" in data)):
        return "workflow"
    if "/tests/" in path:
        return "test"
    return "library"


def _resource_id(kind: str, canonical: dict[str, Any], source_path: str) -> str:
    return str(canonical.get("slug") or canonical.get("name") or canonical.get("id") or _resource_id_from_path(source_path, kind) or "unknown")


def _steps_for_resource(kind: str, canonical: dict[str, Any]) -> list[dict[str, Any]]:
    if kind in ("endpoint", "workflow"):
        return copy.deepcopy(canonical.get("steps") or [])
    if kind == "library":
        steps: list[dict[str, Any]] = []
        for block_id, block in (canonical.get("definition", {}).get("blocks") or {}).items():
            if isinstance(block, dict):
                steps.append({"id": block_id, "type": "block", "body": copy.deepcopy(block.get("steps") or [])})
        return steps
    return []


def _compile_step(step: dict[str, Any], source_path: str, pointer: str) -> dict[str, Any]:
    step_type = step.get("type", "unknown")
    canonical_node_id = f"{step.get('id', 'node')}@{pointer}"
    node = {
        "id": step.get("id", pointer.rsplit("/", 1)[-1]),
        "type": step_type,
        "sourceMap": {
            "sourcePath": source_path,
            "sourcePointer": pointer,
            "canonicalNodeId": canonical_node_id,
        },
    }
    for key, value in step.items():
        if key not in ("body", "then", "else"):
            node[key] = copy.deepcopy(value)
    for child_key in ("body", "then", "else"):
        if isinstance(step.get(child_key), list):
            node[child_key] = [_compile_step(child, source_path, f"{pointer}/{child_key}/{i}") for i, child in enumerate(step[child_key])]
    return node


def _walk_nodes(nodes: list[dict[str, Any]]) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    for node in nodes:
        out.append(node)
        for child_key in ("body", "then", "else"):
            if isinstance(node.get(child_key), list):
                out.extend(_walk_nodes(node[child_key]))
    return out


def build_execution_plan(kind: str, resource_id: str, canonical: dict[str, Any], source_path: str) -> dict[str, Any]:
    steps = _steps_for_resource(kind, canonical)
    nodes = [_compile_step(step, source_path, f"/steps/{index}") for index, step in enumerate(steps) if isinstance(step, dict)]
    budget = estimate_budgets(nodes)
    return {
        "planVersion": COMPILER_FINGERPRINT["executionPlanVersion"],
        "kind": kind,
        "id": resource_id,
        "nodes": nodes,
        "budgetEstimate": budget,
    }


def estimate_budgets(nodes: list[dict[str, Any]]) -> dict[str, int]:
    flat = _walk_nodes(nodes)
    return {
        "compiledNodes": len(flat),
        "reads": sum(1 for n in flat if n.get("type") in ("read", "lookup")),
        "filters": sum(1 for n in flat if n.get("type") == "filter"),
        "writes": sum(1 for n in flat if n.get("type") == "write"),
        "writeOps": sum(len(n.get("ops") or []) for n in flat if n.get("type") == "write"),
        "maxIterations": sum(int(n.get("maxIterations") or 0) for n in flat if n.get("type") in ("while", "until", "foreach")),
        "nestedCalls": sum(1 for n in flat if n.get("type") in ("workflow", "call")),
        "debugTraceBytes": 0,
    }


def budget_diagnostics(plan: dict[str, Any], limits: dict[str, int] | None = None) -> list[dict[str, Any]]:
    budget = plan.get("budgetEstimate") or {}
    limits = limits or DEFAULT_BUDGET_LIMITS
    diags: list[dict[str, Any]] = []
    for key, limit in limits.items():
        measured = int(budget.get(key) or 0)
        if measured > limit:
            diags.append(diagnostic(
                "error",
                "BUDGET_EXCEEDED",
                f"{key} budget exceeded: {measured} > {limit}.",
                kind=plan.get("kind"),
                id=plan.get("id"),
                budgetCategory=key,
                measured=measured,
                limit=limit,
                suggestion="Reduce the source graph, split the resource, or lower loop maxIterations.",
            ))
    return diags


def compile_source_text(text: str, source_path: str, budget_limits: dict[str, int] | None = None) -> dict[str, Any]:
    source, diags = parse_source_yaml(text, source_path)
    if source is not None:
        diags.extend(validate_source_object(source, source_path))
    if source is None or any(d["severity"] == "error" for d in diags):
        return {
            "kind": None,
            "id": None,
            "authoringMode": "source",
            "sourceFormat": "yaml",
            "sourcePath": source_path,
            "diagnostics": diags,
            "ok": False,
        }
    canonical = canonical_definition_from_source(source)
    kind = source["kind"]
    resource_id = source["id"]
    plan = build_execution_plan(kind, resource_id, canonical, source_path)
    diags.extend(budget_diagnostics(plan, budget_limits))
    dependencies = sorted(source.get("imports") or [])
    return _compiled_resource(kind, resource_id, "source", "yaml", SOURCE_VERSION, text, dependencies, canonical, plan, diags, source_path)


def compile_legacy_json(data: dict[str, Any], source_path: str = "") -> dict[str, Any]:
    kind = infer_legacy_kind(data, source_path)
    canonical = canonical_definition_from_legacy(data)
    resource_id = _resource_id(kind, canonical, source_path)
    plan = build_execution_plan(kind, resource_id, canonical, source_path)
    return _compiled_resource(kind, resource_id, "legacy-json", "json", None, stable_json(data), [], canonical, plan, [], source_path)


def _compiled_resource(kind: str, resource_id: str, mode: str, source_format: str, source_version: int | None, source_text: str, dependencies: list[str], canonical: dict[str, Any], plan: dict[str, Any], diags: list[dict[str, Any]], source_path: str) -> dict[str, Any]:
    dependency_hash = stable_hash(dependencies)
    canonical_hash = stable_hash(canonical)
    plan_hash = stable_hash(plan)
    return {
        "ok": not any(d["severity"] == "error" for d in diags),
        "kind": kind,
        "id": resource_id,
        "authoringMode": mode,
        "sourceFormat": source_format,
        "sourceVersion": source_version,
        "sourcePath": source_path,
        "compilerFingerprint": copy.deepcopy(COMPILER_FINGERPRINT),
        "compilerFingerprintHash": compiler_fingerprint_hash(),
        "sourceHash": stable_hash(source_text),
        "dependencyHash": dependency_hash,
        "canonicalHash": canonical_hash,
        "executionPlanHash": plan_hash,
        "dependencies": dependencies,
        "canonicalDefinition": canonical,
        "executionPlan": plan,
        "diagnostics": diags,
    }


def _has_native_nodes(nodes: list[dict[str, Any]]) -> bool:
    for node in _walk_nodes(nodes):
        if node.get("type") in NATIVE_STEP_TYPES:
            return True
    return False


def export_legacy_json(compiled: dict[str, Any]) -> tuple[dict[str, Any] | None, list[dict[str, Any]]]:
    if not compiled.get("ok"):
        return None, [diagnostic("error", "CANNOT_EXPORT_INVALID_RESOURCE", "Resource must compile before legacy JSON export.")]
    if _has_native_nodes(compiled.get("executionPlan", {}).get("nodes") or []):
        return None, [diagnostic("error", "CANNOT_FLATTEN_NATIVE_SOURCE", "Native canonical nodes cannot be automatically flattened to legacy JSON.", kind=compiled.get("kind"), id=compiled.get("id"), suggestion="Keep this resource in source/canonical mode or rewrite it using legacy step types.")]
    return copy.deepcopy(compiled["canonicalDefinition"]), []


def json_to_yaml_reverse_conversion() -> None:
    raise NotImplementedError("Automatic JSON-to-YAML reverse conversion is intentionally unsupported.")


def is_stale(compiled: dict[str, Any], source_text: str | None = None, dependencies: list[str] | None = None, compiler_fingerprint: dict[str, Any] | None = None) -> bool:
    fingerprint = compiler_fingerprint or COMPILER_FINGERPRINT
    if compiled.get("compilerFingerprintHash") != compiler_fingerprint_hash(fingerprint):
        return True
    if source_text is not None and compiled.get("sourceHash") != stable_hash(source_text):
        return True
    if dependencies is not None and compiled.get("dependencyHash") != stable_hash(sorted(dependencies)):
        return True
    return False


def select_recompiled_plan(previous: dict[str, Any], candidate: dict[str, Any]) -> dict[str, Any]:
    if candidate.get("ok"):
        return {"status": "updated", "activePlan": candidate, "diagnostics": candidate.get("diagnostics", [])}
    return {"status": "recompile_failed", "activePlan": previous, "diagnostics": candidate.get("diagnostics", [])}


def compile_path(path: Path) -> dict[str, Any]:
    text = path.read_text(encoding="utf-8-sig")
    if path.suffix.lower() == ".json":
        return compile_legacy_json(json.loads(text), path.as_posix())
    return compile_source_text(text, path.as_posix())


def validate_source_imports(compiled_resources: list[dict[str, Any]]) -> list[dict[str, Any]]:
    available = {
        str(item.get("id"))
        for item in compiled_resources
        if item.get("ok") and item.get("id")
    }
    diagnostics: list[dict[str, Any]] = []
    for item in compiled_resources:
        for dependency in item.get("dependencies") or []:
            if dependency in available:
                continue
            diagnostics.append(diagnostic(
                "error",
                "UNRESOLVED_IMPORT",
                f"Import could not be resolved: {dependency}.",
                kind=item.get("kind"),
                id=item.get("id"),
                sourcePath=item.get("sourcePath"),
                sourcePointer="/imports",
                suggestion="Add the missing library/workflow source file or remove the import.",
            ))
    return diagnostics


def discover_project_files(project_root: Path) -> list[Path]:
    ns = project_root / "Editor" / "Network Storage"
    files: list[Path] = []
    for folder in ("collections", "endpoints", "workflows", "tests", "libraries"):
        base = ns / folder
        if not base.exists():
            continue
        files.extend(sorted(base.glob("*.json")))
        files.extend(sorted(base.glob("*.yaml")))
        files.extend(sorted(base.glob("*.yml")))
    return files


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Validate and compile Network Storage source/legacy resources.")
    parser.add_argument("--project-root", type=Path, help="Project root. When set, scans Editor/Network Storage.")
    parser.add_argument("--file", type=Path, action="append", help="Specific resource file to compile.")
    parser.add_argument("--json", action="store_true", help="Print compiled resources as JSON.")
    args = parser.parse_args(argv)

    paths: list[Path] = []
    if args.project_root:
        paths.extend(discover_project_files(args.project_root))
    if args.file:
        paths.extend(args.file)
    if not paths:
        parser.error("Provide --project-root or --file")

    compiled = [compile_path(path) for path in paths]
    if args.json:
        print(json.dumps(compiled, indent=2, ensure_ascii=False))
    else:
        for item in compiled:
            state = "ok" if item.get("ok") else "failed"
            print(f"{state}: {item.get('kind')}:{item.get('id')} {item.get('sourcePath')}")
            for diag in item.get("diagnostics", []):
                print(f"  {diag['severity']} {diag['code']}: {diag['message']}")
    return 0 if all(item.get("ok") for item in compiled) else 1


if __name__ == "__main__":
    sys.exit(main())
