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
    "oreType": { "type": "string", "required": true },
    "amount": { "type": "number", "required": true }
  },
  "steps": [
    { "type": "read", "collection": "players", "as": "player" },
    { "type": "lookup", "source": "values", "table": "ore_types", "key": "id", "value": "{{input.oreType}}", "as": "ore" },
    { "type": "condition", "field": "player.phaserTier", "operator": "gte", "value": "{{ore.tier}}", "onFail": "error", "errorMessage": "Phaser tier too low" },
    { "type": "transform", "field": "player.ores.{{input.oreType}}", "operation": "add", "value": "{{input.amount}}" },
    { "type": "write", "collection": "players" }
  ],
  "response": {
    "status": 200,
    "body": { "ok": true }
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
| `input` | object | No | Input field definitions with `type` and optional `required` |
| `steps` | array | Yes | Pipeline steps (max 20). This is where all the logic lives |
| `response` | object | No | Default response with `status` and `body` |

## Slug Rules

Endpoint slugs must match `/^[a-z0-9-]+$/` — lowercase letters, digits, and hyphens only. Max 20 endpoints per project.

## Calling Endpoints from Game Code

```csharp
// POST endpoint with input
var result = await NetworkStorage.CallEndpoint("mine-ore", new { oreType = "iron", amount = 5 });

// GET endpoint (no input)
var data = await NetworkStorage.CallEndpoint("get-leaderboard");
```

## Validation Rules

The MCP validates endpoints against these rules:

- `slug` is required, must match `/^[a-z0-9-]+$/`
- `steps` is required, must be a non-empty array
- Max 20 steps per endpoint
- Max 10 `read`/`lookup`/`filter` steps per endpoint
- `method` must be `GET` or `POST`
- Each step must have a valid `type` (see [Step Types](steps.md))
- Each step must include all required fields for its type
- `write` steps warn if the collection was never `read` in a prior step
- GET endpoints with input fields generate a warning
- All `{{template}}` syntax is validated for correct format
