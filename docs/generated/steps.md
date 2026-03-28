# Endpoint Step Types — Complete Reference

> **Note:** For the most up-to-date documentation, visit https://sbox.cool/wiki/network-storage-v3 — these repo docs may be outdated.

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
| `pull` | Remove item from array by match object | `path`, `match` |
| `remove` | Remove item from array by exact value | `path`, `value` |

\* `source` and `reason` are required for ledger-tracked fields (fields with `_ledger: true` in schema). They provide an audit trail.

**Op fields:**

| Field | Required | Description |
|-------|----------|-------------|
| `op` | Yes | Operation type: `set`, `inc`, `push`, `pull`, or `remove` |
| `path` | Yes | Dot-path to the field (supports `{{templates}}` in path segments, e.g. `ores.{{input.ore_id}}`) |
| `value` | Yes (except `pull`) | The value to apply (supports `{{templates}}`) |
| `match` | For `pull` | Match criteria for finding the item to remove from an array (see below) |
| `source` | For ledger fields | Source category (e.g. `"mining"`, `"ore_sale"`, `"upgrade_purchase"`) |
| `reason` | For ledger fields | Human-readable reason (supports `{{templates}}`) |

### `pull` operation

Removes the first matching item from an array. The `match` object specifies which field/value pair to match against. For arrays of objects, match on an object property. For arrays of primitives, use `match.value`.

```json
// Remove object from array by matching a property
{ "op": "pull", "path": "inventory", "match": { "item_id": "{{input.item_id}}" } }

// Remove primitive from array by matching the value directly
{ "op": "pull", "path": "tags", "match": { "value": "old_tag" } }
```

### `remove` operation

Removes items from an array by exact value match. Works for both primitives and objects. Unlike `pull`, it matches the entire value rather than a single property.

```json
// Remove a string from an array
{ "op": "remove", "path": "tags", "value": "old_tag" }

// Remove an object from an array by exact match
{ "op": "remove", "path": "inventory", "value": { "item_id": "sword" } }
```

**`pull` vs `remove`:** Use `pull` when you want to match on a specific property of objects in an array (e.g., find the item where `item_id == "sword"`). Use `remove` when you want to remove by exact value match, which is simpler for primitives and works for full object matches.

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

| Op | Aliases | Description |
|----|---------|-------------|
| `==` | `eq` | Equals |
| `!=` | `neq`, `ne` | Not equals |
| `>` | `gt` | Greater than |
| `>=` | `gte`, `ge` | Greater than or equal |
| `<` | `lt` | Less than |
| `<=` | `lte`, `le` | Less than or equal |
| `contains` | `includes`, `has` | Array contains value or string contains substring |
| `not_contains` | `not_includes`, `not_has`, `notcontains` | Array does NOT contain value or string does NOT include substring |
| `exists` | | Field exists and is not null/undefined |
| `not_exists` | `not_exist`, `notexists` | Field does not exist or is null/undefined |

**Field aliases:** The `check` object also accepts `left` as an alias for `field` and `right` as an alias for `value`. This can improve readability in some cases:

```json
{
  "check": {
    "left": "{{player.tags}}",
    "op": "not_contains",
    "right": "banned"
  }
}
```

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

Executes a workflow by its ID. Workflows can be simple (legacy condition-only format) or multi-step (containing their own pipeline of steps). Multi-step workflows support all step types -- read, write, delete, lookup, filter, transform, condition, and nested workflow calls. Write and delete steps inside workflows are deferred to the parent endpoint's write queue, preserving atomicity. Use `params` to pass values into the workflow and receive return values mapped back into the endpoint context.

### Simple workflow call (legacy condition-only)

For backwards-compatible condition-only workflows, use `bindings` to map step results to the workflow's expected variables:

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

### Multi-step workflow call

For enhanced multi-step workflows, use `params` to pass typed values and receive `returns`:

```json
{
  "id": "validate",
  "type": "workflow",
  "workflow": "validate_purchase",
  "params": {
    "player": "{{player}}",
    "item_id": "{{input.item_id}}"
  }
}
```

The workflow's return values are mapped into the endpoint context. For example, if the workflow returns `{ "item": "{{item}}" }`, you can reference `{{validate.item}}` in subsequent steps.

Workflows can nest up to 8 levels deep. Read, lookup, and filter steps are tracked globally across all nested workflows with a combined limit of 10.

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Step identifier |
| `workflow` | Yes | The workflow ID to execute |
| `bindings` | No | Map of workflow variable names to step IDs (legacy condition-only workflows) |
| `params` | No | Map of parameter names to values (multi-step workflows, supports `{{templates}}`) |

See the [Workflows](workflows.md) documentation for details on defining multi-step workflows with typed params and returns.

## delete — Remove a Collection Record

Removes a record entirely from a collection. Like writes, deletes are **deferred** until all conditions in the pipeline pass. This ensures that a failed condition won't result in data loss.

```json
{
  "id": "wipe",
  "type": "delete",
  "collection": "players",
  "key": "{{steamId}}_default"
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Step identifier |
| `collection` | Yes | Collection name to delete from |
| `key` | Yes | Record key to delete (supports `{{templates}}`) |

**Important:** The collection's `allowRecordDelete` must be set to `true` in the collection schema for delete operations to succeed. This is a safety measure to prevent accidental data loss.

## Required Fields Summary

| Step Type | Required Fields |
|-----------|----------------|
| `read` | `id`, `collection`, `key` |
| `write` | `id`, `collection`, `key`, `ops` |
| `transform` | `id`, `expression` |
| `condition` | `id`, `check` (`field`/`left`, `op`, `value`/`right`), `onFail` (`status`, `error`) |
| `lookup` | `id`, `source`, `table`, `where` (`field`, `op`, `value`) |
| `filter` | `id`, `source`, `table`, `where` (`field`, `op`, `value`) |
| `workflow` | `id`, `workflow` |
| `delete` | `id`, `collection`, `key` |
