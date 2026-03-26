# Workflows

Workflows are reusable logic blocks that endpoints can reference. Each workflow is a JSON file in `Editor/Network Storage/workflows/`. Workflows support two formats: the **legacy condition-only format** (a single condition check) and the **enhanced multi-step format** (a full pipeline with typed params and returns).

## Legacy Condition-Only Format

The simplest workflow is a single condition check. This format is fully supported and backwards-compatible.

```json
{
  "id": "check-currency",
  "name": "Check Currency",
  "description": "Verify player has enough currency",
  "condition": {
    "field": "{{player.currency}}",
    "op": ">=",
    "value": "{{cost}}"
  },
  "onFail": {
    "reject": true,
    "errorCode": "NOT_ENOUGH_CURRENCY",
    "errorMessage": "Not enough currency. Have: {{player.currency}}, need: {{cost}}"
  }
}
```

## Enhanced Multi-Step Format

Workflows can contain multiple steps, accept typed parameters, and return values to the calling endpoint. This enables complex reusable logic like "validate a purchase" or "calculate reward tiers."

```json
{
  "id": "validate-purchase",
  "name": "Validate Purchase",
  "description": "Validate that a player can purchase an item",
  "params": {
    "player": { "type": "object" },
    "item_id": { "type": "string" }
  },
  "steps": [
    {
      "id": "item",
      "type": "lookup",
      "source": "values",
      "table": "shop_items",
      "where": { "field": "id", "op": "==", "value": "{{item_id}}" }
    },
    {
      "id": "cost_check",
      "type": "condition",
      "check": {
        "field": "{{player.currency}}",
        "op": ">=",
        "value": "{{item.cost}}"
      },
      "onFail": {
        "status": 403,
        "error": "NOT_ENOUGH_CURRENCY",
        "message": "Need {{item.cost}}, have {{player.currency}}"
      }
    },
    {
      "id": "already_owned_check",
      "type": "condition",
      "check": {
        "field": "{{player.purchasedItems}}",
        "op": "not_contains",
        "value": "{{item_id}}"
      },
      "onFail": {
        "status": 403,
        "error": "ALREADY_OWNED",
        "message": "You already own this item"
      }
    }
  ],
  "returns": {
    "item": "{{item}}"
  }
}
```

## Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | Yes | Workflow identifier (lowercase alphanumeric + hyphens) |
| `name` | string | No | Human-readable name |
| `description` | string | No | Description of what this workflow does |
| `condition` | object | No | Top-level condition (legacy format) with `field`, `op`, `value` |
| `onFail` | object | No | What happens when legacy condition fails (see below) |
| `params` | object | No | Typed parameter definitions for multi-step workflows (see below) |
| `steps` | array | No | Pipeline steps for multi-step workflows (see below) |
| `returns` | object | No | Values to return to the calling endpoint (see below) |

### `params` object

Defines the typed parameters the workflow accepts. Each key is a parameter name, and the value describes its type:

```json
{
  "params": {
    "player": { "type": "object" },
    "item_id": { "type": "string" },
    "quantity": { "type": "number" }
  }
}
```

Parameters are accessible within the workflow's steps as `{{param_name}}` (e.g., `{{player.currency}}`, `{{item_id}}`).

### `steps` array

Multi-step workflows support these step types: `lookup`, `filter`, `transform`, `condition`, and `workflow` (nested). Steps work the same as endpoint steps — see [Step Types](steps.md) for full details.

### `returns` object

Maps values from the workflow's execution context back to the calling endpoint. The calling endpoint can then reference these as `{{workflowStepId.returnKey}}`.

```json
{
  "returns": {
    "item": "{{item}}",
    "calculated_cost": "{{final_cost}}"
  }
}
```

If the calling endpoint step has `"id": "validate"`, the returned values are accessible as `{{validate.item}}` and `{{validate.calculated_cost}}`.

### Limits

| Limit | Value |
|-------|-------|
| Max steps per workflow | 10 |
| Max nesting depth (workflow calling workflow) | 3 |

## Condition Format (Legacy)

The `condition` object uses the same format as endpoint condition steps:

| Field | Required | Description |
|-------|----------|-------------|
| `field` | Yes | Value to check (supports `{{templates}}`) |
| `op` | Yes | Operator: `>=`, `<=`, `==`, `!=`, `>`, `<`, `contains`, `not_contains`, `exists`, `not_exists` |
| `value` | Yes* | Value to compare against (supports `{{templates}}`) |

\* Not required for `exists`/`not_exists` operators.

**Field aliases:** `left` can be used as an alias for `field`, and `right` as an alias for `value`.

## onFail Options

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `reject` | boolean | true | Short-circuit and return error to client |
| `errorCode` | string | `"CONDITION_FAILED"` | Error code in response |
| `errorMessage` | string | `"Condition check failed."` | Human-readable message (supports `{{templates}}`) |
| `severity` | string | null | `"warning"` or `"critical"` — logged for monitoring |
| `status` | number | 200 | HTTP status code (keep at 200 so s&box client can read response body) |
| `flag` | boolean | false | Log a player flag for anti-cheat review |
| `webhook` | boolean | false | Send Discord webhook on failure |
| `clamp` | boolean | false | Cap the input value instead of rejecting |

## Referencing from Endpoints

### Legacy condition-only workflows

Use a workflow step with `type: "condition"` and optional `bindings` to map variables:

```json
{
  "id": "currency_check",
  "type": "condition",
  "workflow": "check-currency",
  "bindings": {
    "cost": "upgrade_cost"
  }
}
```

The `bindings` object maps workflow variable names (left) to step IDs from the current endpoint pipeline (right). This allows you to pass computed values from earlier steps into the workflow.

### Multi-step workflows

Use a workflow step with `type: "workflow"` and `params` to pass typed values:

```json
{
  "id": "validate",
  "type": "workflow",
  "workflow": "validate-purchase",
  "params": {
    "player": "{{player}}",
    "item_id": "{{input.item_id}}"
  }
}
```

The workflow executes its steps, and its `returns` are mapped back into the endpoint context under the step's `id`. For example, if the workflow returns `{ "item": "{{item}}" }`, subsequent endpoint steps can reference `{{validate.item}}`.

## Compound Conditions

Use `all` (AND) or `any` (OR) for compound checks:

```json
{
  "condition": {
    "all": [
      { "field": "{{player.level}}", "op": ">=", "value": 5 },
      { "field": "{{player.currency}}", "op": ">=", "value": "{{cost}}" }
    ]
  }
}
```

## Validation Rules

The MCP validates workflows against these rules:

- `id` is required, must match `/^[a-z0-9-]+$/`
- Simple conditions require `field` (or `left`) and `op`
- `condition.op` must be a valid operator
- Compound conditions (`all`/`any`) must be arrays
- `onFail.severity` must be `"warning"` or `"critical"` if present
- `onFail.status` warns if not 200 (non-200 causes s&box to lose response body)
- `onFail.errorMessage` is checked for valid `{{template}}` syntax
- Multi-step workflows: `steps` must be an array with max 10 steps
- Multi-step workflows: `params` values must have a `type` field
- Nested workflow calls have a max depth of 3
