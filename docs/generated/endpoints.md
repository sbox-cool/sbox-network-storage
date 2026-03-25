# Endpoints

Endpoints are server-side pipelines that your game calls via the API. Each endpoint is a JSON file in `Editor/Network Storage/endpoints/`.

## Endpoint JSON Format

```json
{
  "slug": "mine-ore",
  "name": "Mine Ore",
  "method": "POST",
  "description": "Process an ore mining action",
  "enabled": true,
  "input": {
    "type": "object",
    "properties": {
      "oreType": { "type": "string" },
      "amount": { "type": "number" }
    },
    "required": ["oreType", "amount"]
  },
  "steps": [
    { "id": "player", "type": "read", "collection": "players", "key": "{{steamId}}_default" },
    { "id": "ore", "type": "lookup", "source": "values", "table": "ore_types", "where": { "field": "id", "op": "==", "value": "{{input.oreType}}" } },
    { "id": "tier_check", "type": "condition", "check": { "field": "{{player.phaserTier}}", "op": ">=", "value": "{{ore.tier}}" }, "onFail": { "status": 403, "error": "TIER_TOO_LOW", "message": "Phaser tier too low" } },
    { "id": "new_total", "type": "transform", "expression": "{{player.currentOreKg}} + {{input.amount}}" },
    { "id": "save", "type": "write", "collection": "players", "key": "{{steamId}}_default", "ops": [{ "op": "inc", "path": "ores.{{input.oreType}}", "value": "{{input.amount}}" }, { "op": "set", "path": "currentOreKg", "value": "{{new_total}}" }] }
  ],
  "response": {
    "status": 200,
    "body": { "ok": true, "currentOreKg": "{{new_total}}" }
  }
}
```

## Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `slug` | string | Yes | URL slug (lowercase alphanumeric + hyphens, e.g. `mine-ore`, `sell-items`) |
| `name` | string | No | Human-readable name |
| `method` | string | No | `GET` or `POST`. Default: `POST` |
| `description` | string | No | Description (max 256 chars) |
| `enabled` | boolean | No | Whether the endpoint is active. Default: true |
| `input` | object | No | JSON Schema object with `type`, `properties`, and `required` array |
| `steps` | array | Yes | Pipeline steps (max 20). This is where all the logic lives |
| `response` | object | No | Default response with `status` and `body` (body supports `{{templates}}`) |

## Input Schema

Inputs use JSON Schema format:

```json
{
  "input": {
    "type": "object",
    "properties": {
      "ore_id": { "type": "string" },
      "kg": { "type": "number", "min": 0.1 }
    },
    "required": ["ore_id", "kg"]
  }
}
```

Input values are accessed in steps via `{{input.fieldName}}`.

## Slug Rules

Endpoint slugs must match `/^[a-z0-9-]+$/` — lowercase letters, digits, and hyphens only. Max 20 endpoints per project.

## Calling Endpoints from Game Code

```csharp
// POST endpoint with input
var result = await NetworkStorage.CallEndpoint("mine-ore", new { ore_id = "iron", kg = 5.0f });

// GET endpoint (no input)
var data = await NetworkStorage.CallEndpoint("load-player");
```

## Validation Rules

The MCP validates endpoints against these rules:

- `slug` is required, must match `/^[a-z0-9-]+$/`
- `steps` is required, must be a non-empty array
- Max 20 steps per endpoint
- Max 10 `read`/`lookup`/`filter` steps per endpoint
- `method` must be `GET` or `POST`
- Each step must have a valid `type` (see [Step Types](steps.md))
- Each step must include `id` and all required fields for its type
- `write` steps warn if the collection was never `read` in a prior step
- GET endpoints with input fields generate a warning
- All `{{template}}` syntax is validated for correct format
