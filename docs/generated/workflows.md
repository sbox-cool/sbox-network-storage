# Workflows

Workflows are reusable validation/logic blocks that endpoints can reference. Each workflow is a JSON file in `Editor/Network Storage/workflows/`.

## Workflow JSON Format

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

## Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | Yes | Workflow identifier (lowercase alphanumeric + hyphens) |
| `name` | string | No | Human-readable name |
| `description` | string | No | Description of what this workflow validates |
| `condition` | object | No | Top-level condition with `field`, `op`, `value` |
| `onFail` | object | No | What happens when condition fails (see below) |

## Condition Format

The `condition` object uses the same format as endpoint condition steps:

| Field | Required | Description |
|-------|----------|-------------|
| `field` | Yes | Value to check (supports `{{templates}}`) |
| `op` | Yes | Operator: `>=`, `<=`, `==`, `!=`, `>`, `<`, `contains`, `exists`, `not_exists` |
| `value` | Yes* | Value to compare against (supports `{{templates}}`) |

\* Not required for `exists`/`not_exists` operators.

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

Use a workflow step in your endpoint with optional `bindings` to map variables:

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
- Simple conditions require `field` and `op`
- `condition.op` must be a valid operator
- Compound conditions (`all`/`any`) must be arrays
- `onFail.severity` must be `"warning"` or `"critical"` if present
- `onFail.status` warns if not 200 (non-200 causes s&box to lose response body)
- `onFail.errorMessage` is checked for valid `{{template}}` syntax
