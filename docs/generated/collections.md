# Collections

Collections define where and how your data is stored. Each collection is a JSON file in `Editor/Network Storage/collections/`.

## Collection JSON Format

```json
{
  "name": "players",
  "description": "Player save data",
  "collectionType": "per-steamid",
  "accessMode": "public",
  "maxRecords": 1,
  "allowRecordDelete": false,
  "requireSaveVersion": false,
  "rateLimits": { "mode": "none" },
  "rateLimitAction": "reject",
  "webhookOnRateLimit": false,
  "schema": {
    "currency": { "type": "number", "default": 0 },
    "xp": { "type": "number", "default": 0 },
    "inventory": {
      "type": "object",
      "properties": {
        "items": { "type": "array", "default": [] }
      }
    }
  },
  "constants": [
    {
      "group": "progression",
      "values": { "xp_per_level": 1000, "max_level": 50 }
    }
  ],
  "tables": [
    {
      "name": "ore_types",
      "columns": ["id", "name", "tier", "basePrice"],
      "rows": [
        ["iron", "Iron Ore", 1, 10],
        ["copper", "Copper", 2, 25]
      ]
    }
  ]
}
```

## Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Collection identifier (lowercase alphanumeric + underscores, e.g. `players`, `game_config`) |
| `description` | string | No | Human-readable description |
| `collectionType` | string | No | `per-steamid` (one record per player) or `global` (shared data). Default: `per-steamid` |
| `accessMode` | string | No | `public` or `private`. Default: `public` |
| `maxRecords` | number | No | Max records per user (usually 1 for per-steamid). Default: 1 |
| `allowRecordDelete` | boolean | No | Whether records can be deleted. Default: false |
| `requireSaveVersion` | boolean | No | Whether save version tracking is required. Default: false |
| `rateLimits` | object | No | Rate limiting config with `mode` field |
| `rateLimitAction` | string | No | Action on rate limit: `reject`. Default: `reject` |
| `webhookOnRateLimit` | boolean | No | Send webhook on rate limit. Default: false |
| `schema` | object | Yes | Field definitions with types and defaults |
| `constants` | array | No | Game config values grouped by category |
| `tables` | array | No | Structured data tables (referenced in endpoints as `values.tableName`) |

## Schema Field Types

Each field in `schema` must have a `type`: `string`, `number`, `boolean`, `object`, or `array`.

- `default` — the initial value when a new record is created
- For `object` types, use `properties` to define nested fields
- For `array` types, use `default: []`

## Naming Rules

Collection names must match `/^[a-z0-9_]+$/` — lowercase letters, digits, and underscores only. Max 50 collections per project.

## Validation Rules

The MCP validates collections against these rules:

- `name` is required, must be a string matching `/^[a-z0-9_]+$/`
- `schema` is required, must be a non-empty object
- Each schema field must have a `type` property (one of: `string`, `number`, `boolean`, `object`, `array`)
- `collectionType` must be `per-steamid` or `global`
- `accessMode` must be `public` or `private`
- `maxRecords` must be a positive number
- `constants` must be an array of objects with `group` (string) and `values` (object)
- `tables` must be an array of objects with `name` (string), `columns` (string[]), and `rows` (array[]) where each row length matches column count
- Warning: `maxRecords > 10` for `per-steamid` collections is flagged as unusual
