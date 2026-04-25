# Network Storage Source Authoring

Network Storage source authoring treats YAML as source code for resources. Legacy JSON authoring is deprecated; when matching YAML source exists, local JSON endpoint files are ignored by the sync tool.

## File Names

Local projects store source files in the matching resource folder:

```text
Editor/Network Storage/
  collections/<id>.collection.yml
  endpoints/<slug>.endpoint.yml
  workflows/<id>.workflow.yml
  tests/<id>.test.yml
  libraries/<id>.library.yml
```

`.yaml` is also accepted, but project documentation and generated examples should prefer `.yml`. The resource kind is part of the file name and must match the `kind` field inside the source. Existing `.json` resources are legacy resources and should be migrated to YAML before editing.

## Source Model

Every source file uses the same top-level shape:

```yaml
sourceVersion: 1
kind: endpoint
id: mine-ore
name: Mine Ore
description: Mine one ore deposit.
imports: []
definition:
  method: POST
  input: {}
  steps: []
  response:
    status: 200
    body:
      ok: true
```

Required fields:

- `sourceVersion`: integer source schema version. Start with `1`.
- `kind`: one of `collection`, `endpoint`, `workflow`, `test`, or `library`.
- `id`: stable resource id. Endpoint ids are endpoint slugs.
- `definition`: typed resource body.

Optional fields:

- `name`
- `description`
- `notes`
- `imports`
- `metadata`

## YAML Subset

The supported source syntax is a safe YAML 1.2 subset:

- Plain maps, arrays, strings, numbers, booleans, and null.
- Block strings are allowed for notes and long expressions.
- Custom tags are rejected.
- Executable parser features are rejected.
- Anchors and merge keys are not part of the first supported subset.
- Duplicate keys are invalid.

The backend compiler stores original source text and compiles it into a typed execution plan. Request-time execution does not parse YAML.

## Legacy JSON

Legacy JSON resources are deprecated:

- YAML source is authoritative when present.
- JSON endpoint files are ignored when matching YAML source exists.
- The website should label remaining JSON resources as `Legacy JSON`.
- JSON export mode is for compatibility only.

## Compiler Fingerprints

Compiled resources store:

- compiler fingerprint
- source hash
- dependency hash
- canonical definition hash
- execution plan hash

When a compiler changes, resources with stale fingerprints are recompiled in a controlled job. A failed recompile keeps the previous known-good plan active and surfaces diagnostics.

## Execution Budgets

Source definitions compile into bounded execution plans. Budgets protect games from slow requests and protect creators from hidden flat-step explosions.

Initial budget categories:

- `maxCompiledNodes`: total canonical nodes after imports and expansion.
- `maxReads`: direct collection reads and lookup-style reads.
- `maxFilters`: filter scans over arrays or query results.
- `maxWrites`: write steps.
- `maxWriteOps`: individual mutation operations inside write steps.
- `maxIterations`: total loop and foreach iterations.
- `maxNestedCalls`: workflow or library call depth.
- `maxWallTimeMs`: request-time execution wall clock.
- `maxDebugTraceBytes`: size of returned debug and trace data.

Validation should estimate these where it can. Runtime failures should report the resource id, source path or line, canonical node id, budget category, observed value, limit, and a suggested fix.

Common fixes:

- Add or lower `maxIterations` on `while`, `until`, and `foreach` nodes.
- Replace repeated flat steps with `foreach`, `call`, or a library block.
- Move broad scans into a bounded lookup or filter input.
- Split unrelated writes across separate gameplay moments.
- Reduce debug trace output when logic is already verified.

## Local Validation And Canonical Preview

The local reference compiler mirrors the backend source contract for sync tooling, MCP-style helpers, and CI:

```powershell
python Libraries/sboxcool.network-storage/Editor/source_compiler.py --project-root .
python Libraries/sboxcool.network-storage/Editor/source_compiler.py --file "Editor/Network Storage/workflows/factory_route.recompute.workflow.yml" --json
```

The compiler emits:

- source format and authoring mode (`source` or `legacy-json`)
- compiler fingerprint and fingerprint hash
- source, dependency, canonical, and execution-plan hashes
- deterministic canonical definition
- canonical execution plan with source maps
- diagnostics with source paths, object pointers, node ids, and budget categories where available

`sync.py --sources` runs this validation before upload. Invalid YAML source is rejected locally and prints per-resource diagnostics.

Automatic JSON-to-YAML reverse conversion is only used by local migration tooling. Legacy JSON can be imported into canonical definitions and exported as JSON for compatibility, but YAML source should be edited going forward.
