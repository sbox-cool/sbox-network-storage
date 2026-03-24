# Endpoint Step Types — Complete Reference

Endpoints execute a pipeline of steps in order. Each step has a `type` field and type-specific properties.

## read — Load a Collection Record

Loads a player's record from a collection into a named variable.

```json
{ "type": "read", "collection": "players", "as": "player" }
```

| Field | Required | Description |
|-------|----------|-------------|
| `collection` | Yes | Collection name to read from |
| `as` | Yes | Variable name to store the result (used in later steps as `{{player.fieldName}}`) |

## write — Save a Collection Record

Writes/updates a record in a collection. All writes are **deferred** until all conditions pass.

```json
{ "type": "write", "collection": "players" }
```

| Field | Required | Description |
|-------|----------|-------------|
| `collection` | Yes | Collection name to write to (must have been `read` earlier) |

## transform — Modify a Field Value

Modifies a field value using an operation.

```json
{ "type": "transform", "field": "player.currency", "operation": "add", "value": 100 }
{ "type": "transform", "field": "player.ores.{{input.oreType}}", "operation": "add", "value": "{{input.amount}}" }
```

| Field | Required | Description |
|-------|----------|-------------|
| `field` | Yes | Dot-path to the field (supports `{{templates}}`) |
| `operation` | Yes | One of: `add`, `subtract`, `set`, `multiply`, `divide`, `append`, `remove` |
| `value` | Yes | The value to use (supports `{{templates}}`) |

**Operations:**
- `add` — add numeric value
- `subtract` — subtract numeric value
- `set` — overwrite with new value
- `multiply` — multiply by value
- `divide` — divide by value
- `append` — add item to array
- `remove` — remove item from array

## condition — Validate a Check

Checks a condition and either rejects the request or skips remaining steps.

```json
{
  "type": "condition",
  "field": "player.currency",
  "operator": "gte",
  "value": "{{input.cost}}",
  "onFail": "error",
  "errorMessage": "Not enough currency"
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `field` | Yes | Dot-path to the field to check |
| `operator` | Yes | One of: `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `contains`, `exists`, `not_exists` |
| `value` | Yes* | Value to compare against (*not required for `exists`/`not_exists`) |
| `onFail` | Yes | `"error"` (reject request) or `"skip"` (skip remaining steps) |
| `errorMessage` | No | Human-readable error message (supports `{{templates}}`) |
| `errorCode` | No | Custom error code (e.g. `"BACKPACK_FULL"`) |

**Operators:**
- `eq` — equals
- `neq` — not equals
- `gt` — greater than
- `gte` — greater than or equal
- `lt` — less than
- `lte` — less than or equal
- `contains` — array contains value or string contains substring
- `exists` — field exists and is not null/undefined
- `not_exists` — field does not exist or is null/undefined

## lookup — Find a Table Row

Looks up a single row in a values table by key match.

```json
{ "type": "lookup", "source": "values", "table": "ore_types", "key": "id", "value": "{{input.oreType}}", "as": "ore" }
```

| Field | Required | Description |
|-------|----------|-------------|
| `source` | Yes | Data source (typically `"values"`) |
| `table` | Yes | Table name (defined in collection's `tables` array) |
| `key` | Yes | Column to match against |
| `value` | Yes | Value to match (supports `{{templates}}`) |
| `as` | Yes | Variable name for the matched row |

## filter — Find Multiple Records

Filters records by field match. Max 500 records scanned.

```json
{ "type": "filter", "source": "leaderboard", "field": "score", "operator": "gte", "value": 100, "as": "topPlayers" }
```

| Field | Required | Description |
|-------|----------|-------------|
| `source` | Yes | Collection or data source to filter |
| `field` | Yes | Field to match against |
| `operator` | Yes | Comparison operator |
| `value` | Yes | Value to compare |
| `as` | Yes | Variable name for the filtered results |

## workflow — Run a Reusable Workflow

Executes a workflow by its ID. The workflow's steps run inline.

```json
{ "type": "workflow", "workflow": "check-currency" }
```

| Field | Required | Description |
|-------|----------|-------------|
| `workflow` | Yes | The workflow ID to execute |

## Required Fields Summary

| Step Type | Required Fields |
|-----------|----------------|
| `read` | `collection`, `as` |
| `write` | `collection` |
| `transform` | `field`, `operation`, `value` |
| `condition` | `field`, `operator`, `value`*, `onFail` |
| `lookup` | `source`, `table`, `key`, `value`, `as` |
| `filter` | `source`, `field`, `operator`, `value`, `as` |
| `workflow` | `workflow` |

*`value` not required for `exists`/`not_exists` operators.
