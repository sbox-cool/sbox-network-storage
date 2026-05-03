# Network Storage Source Authoring

Network Storage source authoring treats YAML as source code for collections, endpoints, reusable logic, tests, and libraries. The backend compiler is the source of truth: local tools validate early, then `sync.py --sources` asks the backend to canonicalize and safely upgrade each source before upload.

## File Names

Local projects store source files in the matching resource folder:

```text
Editor/Network Storage/
  collections/<id>.collection.yml   # preferred typed name
  collections/<id>.yml              # also accepted inside the folder
  endpoints/<slug>.endpoint.yml
  endpoints/<slug>.yml
  workflows/<id>.workflow.yml
  tests/<id>.test.yml
  libraries/<id>.library.yml
```

Prefer `.yml` for new files. `.yaml` is accepted. Typed names are preferred, but plain names inside the correct folder are supported for backwards compatibility.

## Endpoint Exposure

The new model uses one resource kind for callable logic: `kind: endpoint`.

Use `exposure` to choose how it is used:

| Exposure | Meaning | Client call |
| --- | --- | --- |
| `public` | Publicly accessible game API endpoint | `NetworkStorage.CallEndpoint("slug")` |
| `internal` | Reusable Logic, private to backend endpoint/workflow calls | not directly callable by clients |

Public endpoints can still be called internally. Internal endpoints are reusable logic only.

```yaml
sourceVersion: 1
kind: endpoint
id: mine-ore
name: Mine Ore
exposure: public
method: POST
input:
  type: object
  properties:
    amount:
      type: number
steps:
  - id: validate_amount
    type: condition
    check:
      field: "{{input.amount}}"
      op: ">"
      value: 0
    routes:
      false:
        action: reject
        status: 400
        error: INVALID_AMOUNT
        message: Amount must be positive.
        webhook: true
response:
  status: 200
  body:
    ok: true
```

```yaml
sourceVersion: 1
kind: endpoint
id: debit-currency
name: Debit Currency
exposure: internal
params:
  amount:
    type: number
steps:
  - id: debit
    type: write
    collection: players
    key: "{{steamId}}"
    ops:
      - op: inc
        path: gold
        value: "{{-params.amount}}"
```

## Flat And Wrapper Layouts

New source should be flat: resource fields live at the top level next to `sourceVersion`, `kind`, and `id`.

The older wrapper layout is still accepted:

```yaml
sourceVersion: 1
kind: endpoint
id: mine-ore
definition:
  method: POST
  steps: []
```

The backend source-upgrade route can rewrite safe wrapper layouts into the flat layout. If the backend marks an upgrade unsafe, the sync tool prints a warning and leaves the file unchanged.

## Routes, Rejection, And Webhooks

Use canonical `routes` for branching and rejects. Legacy `onFail` is still accepted for backwards compatibility, but new files should use `routes.false`.

A generic condition failure (`CONDITION_FAILED` / `Condition check failed.`) is treated as actionable debugging signal and reports to error webhooks. Domain-specific expected rejections stay quiet unless the route sets `webhook: true`.

## Recursion And Runtime Protection

Recursive calls and reusable logic are bounded by backend limits. Current defaults are:

- max workflow depth: `4`
- max step visits: `20`
- max sleep per request: `1000ms`
- max wall time: `5000ms`

When a limit is hit, the endpoint response includes structured error details and the backend reports it through the error path.

## Legacy JSON

Legacy JSON is still accepted for compatibility and can be compiled into canonical metadata. Keep JSON fixtures/tests around when checking compatibility. Prefer YAML for new authoring.

## YAML Subset

The supported source syntax is a safe YAML subset:

- Plain maps, arrays, strings, numbers, booleans, and null.
- Block strings are allowed for notes and long expressions.
- Custom tags are rejected.
- Anchors, aliases, and merge keys are rejected.
- Duplicate keys are invalid.

## Local Validation And Canonical Preview

```powershell
python Libraries/sboxcool.network-storage/Editor/source_compiler.py --project-root .
python Libraries/sboxcool.network-storage/Editor/source_compiler.py --file "Editor/Network Storage/endpoints/mine-ore.endpoint.yml" --json
```

`sync.py --sources` validates locally, calls the backend `source-upgrade` route, writes safe backend upgrades, then uploads canonical definitions through the kind-specific management routes.
