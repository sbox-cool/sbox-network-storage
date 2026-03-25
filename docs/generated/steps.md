# Endpoint Step Types — Complete Reference

Endpoints execute a pipeline of steps in order. Each step has an `id`, a `type`, and type-specific properties. The `id` is used to reference the step's result in later steps via `{{id.field}}` templates.

## read — Load a Collection Record

Loads a record from a collection by key. The result is available in later steps as `{{id.fieldName}}`.

```json
{
  "id": "player",
  "type": "read",
  "collection": "players",
  "key": "{{steamId}}_default"
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Variable name for the result (used as `{{player.currency}}`, `{{player.ores.iron}}`, etc.) |
| `collection` | Yes | Collection name to read from |
| `key` | Yes | Record key (supports `{{templates}}`). Typically `{{steamId}}` or `{{steamId}}_default` |

## write — Save a Collection Record

Persists changes to a collection record using atomic operations. All writes are **deferred** until all conditions pass.

```json
{
  "id": "save",
  "type": "write",
  "collection": "players",
  "key": "{{steamId}}_default",
  "ops": [
    { "op": "inc", "path": "xp", "value": "{{xp_reward}}", "source": "mining", "reason": "Mined {{input.kg}}kg" },
    { "op": "set", "path": "currentOreKg", "value": "{{new_total}}" },
    { "op": "push", "path": "purchasedUpgrades", "value": "{{input.upgrade_id}}" }
  ]
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Step identifier |
| `collection` | Yes | Collection name to write to (should have been `read` earlier) |
| `key` | Yes | Record key (supports `{{templates}}`) |
| `ops` | Yes | Array of operations to apply atomically |

**Operations (`ops` array):**

| Op | Description | Extra Fields |
|----|-------------|-------------|
| `set` | Overwrite field with new value | `path`, `value` |
| `inc` | Increment numeric field | `path`, `value`, `source`*, `reason`* |
| `push` | Append item to array field | `path`, `value` |

\* `source` and `reason` are required for ledger-tracked fields (fields with `_ledger: true` in schema). They provide an audit trail.

**Op fields:**

| Field | Required | Description |
|-------|----------|-------------|
| `op` | Yes | Operation type: `set`, `inc`, or `push` |
| `path` | Yes | Dot-path to the field (supports `{{templates}}` in path segments, e.g. `ores.{{input.ore_id}}`) |
| `value` | Yes | The value to apply (supports `{{templates}}`) |
| `source` | For ledger fields | Source category (e.g. `"mining"`, `"ore_sale"`, `"upgrade_purchase"`) |
| `reason` | For ledger fields | Human-readable reason (supports `{{templates}}`) |

## transform — Compute a Value

Evaluates a math expression and stores the result. Reference the result in later steps as `{{id}}` (NOT `{{id.result}}`).

```json
{ "id": "sale_value", "type": "transform", "expression": "round({{ore.value_per_kg}} * {{input.kg}})" }
{ "id": "neg_cost", "type": "transform", "expression": "0 - {{upgrade_cost}}" }
{ "id": "capacity", "type": "transform", "expression": "max(0 - {{-player.backpackCapacity}}, 100)" }
```

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Variable name for the result (referenced as `{{sale_value}}`, `{{neg_cost}}`, etc.) |
| `expression` | Yes | Math expression (supports `{{templates}}` and math functions) |

**Supported math functions:** `floor()`, `ceil()`, `round()`, `min()`, `max()`, `abs()`

**Supported operators:** `+`, `-`, `*`, `/`, `%`

**Important:** Transform results are referenced as `{{id}}` directly, not `{{id.result}}` or `{{id.value}}`.

## condition — Validate a Check

Checks a condition and rejects the request if it fails. Uses a `check` object with `field`, `op`, and `value`.

```json
{
  "id": "currency_check",
  "type": "condition",
  "check": {
    "field": "{{player.currency}}",
    "op": ">=",
    "value": "{{upgrade_cost}}"
  },
  "onFail": {
    "status": 403,
    "error": "NOT_ENOUGH_CURRENCY",
    "message": "Need {{upgrade_cost}} QC. You have {{player.currency}} QC."
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Step identifier |
| `check` | Yes | Condition object (see below) |
| `onFail` | Yes | Error response if condition fails (see below) |

**`check` object:**

| Field | Required | Description |
|-------|----------|-------------|
| `field` | Yes | Left-hand value to check (supports `{{templates}}`) |
| `op` | Yes | Comparison operator (see table below) |
| `value` | Yes* | Right-hand value to compare against (supports `{{templates}}`) |

\* `value` not required for `exists` / `not_exists` operators.

**Operators:**

| Op | Description |
|----|-------------|
| `==` | Equals |
| `!=` | Not equals |
| `>` | Greater than |
| `>=` | Greater than or equal |
| `<` | Less than |
| `<=` | Less than or equal |
| `contains` | Array contains value or string contains substring |
| `exists` | Field exists and is not null/undefined |
| `not_exists` | Field does not exist or is null/undefined |

**`onFail` object:**

| Field | Required | Description |
|-------|----------|-------------|
| `status` | Yes | HTTP status code (use 403 for validation failures) |
| `error` | Yes | Error code string (e.g. `"NOT_ENOUGH_CURRENCY"`) |
| `message` | No | Human-readable message (supports `{{templates}}`) |

**Compound conditions** — use `all` (AND) or `any` (OR):

```json
{
  "id": "multi_check",
  "type": "condition",
  "check": {
    "all": [
      { "field": "{{player.currency}}", "op": ">=", "value": "{{item.cost}}" },
      { "field": "{{player.xp}}", "op": ">=", "value": "{{item.xp_required}}" }
    ]
  },
  "onFail": {
    "status": 403,
    "error": "REQUIREMENTS_NOT_MET",
    "message": "You don't meet the requirements for this purchase."
  }
}
```

## lookup — Find a Single Table Row

Looks up a single row from a game values table. Returns the first match. Fails with `NOT_FOUND` if no row matches.

```json
{
  "id": "ore",
  "type": "lookup",
  "source": "values",
  "table": "ore_types",
  "where": {
    "field": "ore_id",
    "op": "==",
    "value": "{{input.ore_id}}"
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Variable name for the matched row (e.g. `{{ore.value_per_kg}}`, `{{ore.tier}}`) |
| `source` | Yes | Data source (typically `"values"` for game values tables) |
| `table` | Yes | Table name (defined in the collection's `tables` array) |
| `where` | Yes | Match criteria with `field`, `op`, `value` |

**`where` object:**

| Field | Required | Description |
|-------|----------|-------------|
| `field` | Yes | Column name to match against |
| `op` | Yes | Comparison operator (typically `"=="`) |
| `value` | Yes | Value to match (supports `{{templates}}`) |

## filter — Find Multiple Table Rows

Queries multiple rows from a game values table. Returns matching rows as an array.

```json
{
  "id": "tier_ores",
  "type": "filter",
  "source": "values",
  "table": "ore_types",
  "where": {
    "field": "tier",
    "op": "<=",
    "value": "{{player_tier}}"
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Variable name for results (access as `{{id.rows}}` for the array, `{{id.count}}` for row count) |
| `source` | Yes | Data source (typically `"values"`) |
| `table` | Yes | Table name to filter |
| `where` | Yes | Filter criteria with `field`, `op`, `value` |

**`where` object:** Same format as lookup (see above).

**Result access:**
- `{{id.rows}}` — the array of matching rows
- `{{id.count}}` — number of matching rows

**Note:** Filter results (`count`, `rows`) may not be available in all contexts (e.g. transform expressions). Test your usage.

## workflow — Run a Reusable Workflow

Executes a workflow by its ID. The workflow's condition is evaluated inline. Use `bindings` to map step results to the workflow's expected variables.

```json
{
  "id": "space_check",
  "type": "condition",
  "workflow": "check-inventory-space",
  "bindings": {
    "new_ore_total": "new_ore_total",
    "capacity": "capacity",
    "current_kg": "actualOreKg"
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Step identifier |
| `workflow` | Yes | The workflow ID to execute |
| `bindings` | No | Map of workflow variable names to step IDs from the current pipeline |

## Required Fields Summary

| Step Type | Required Fields |
|-----------|----------------|
| `read` | `id`, `collection`, `key` |
| `write` | `id`, `collection`, `key`, `ops` |
| `transform` | `id`, `expression` |
| `condition` | `id`, `check` (`field`, `op`, `value`), `onFail` (`status`, `error`) |
| `lookup` | `id`, `source`, `table`, `where` (`field`, `op`, `value`) |
| `filter` | `id`, `source`, `table`, `where` (`field`, `op`, `value`) |
| `workflow` | `id`, `workflow` |
