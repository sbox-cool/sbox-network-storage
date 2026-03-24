# Network Storage — File Reference

## Project Data Files

All game data lives in `Editor/Network Storage/` (configurable via Setup). This folder is the **source of truth** for your backend configuration.

```
Editor/Network Storage/
├── config/
│   ├── .env              ← API credentials (gitignored)
│   └── .gitignore        ← Prevents .env from being committed
├── collections/
│   └── *.json            ← One file per collection (schema + config)
├── endpoints/
│   └── *.json            ← One file per endpoint (pipeline definition)
└── workflows/
    └── *.json            ← One file per workflow (reusable step sequences)
```

### `.env` Format

```env
# Project identifier from sboxcool.com dashboard
SBOXCOOL_PROJECT_ID=your-project-id

# Public API key (sbox_ns_ prefix) — used by game client at runtime
SBOXCOOL_PUBLIC_KEY=sbox_ns_your_public_key

# Secret key (sbox_sk_ prefix) — editor sync tool only, NEVER ships
SBOXCOOL_SECRET_KEY=sbox_sk_your_secret_key

# Base URL (default: https://api.sboxcool.com)
SBOXCOOL_BASE_URL=https://api.sboxcool.com

# API version (default: v3)
SBOXCOOL_API_VERSION=v3

# Editor subfolder for sync data (default: Network Storage)
SBOXCOOL_DATA_FOLDER=Network Storage

# Data source: api_then_json, api_only, json_only
SBOXCOOL_DATA_SOURCE=api_then_json
```

### Collection File Format (`collections/*.json`)

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
      "values": {
        "xp_per_level": 1000,
        "max_level": 50
      }
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

**Fields:**
| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Collection identifier (used in endpoint steps) |
| `description` | string | Human-readable description |
| `collectionType` | string | `per-steamid` (one record per player) or `global` (shared) |
| `accessMode` | string | `public` or `private` |
| `maxRecords` | number | Max records per user (usually 1 for per-steamid) |
| `schema` | object | Field definitions with types and defaults |
| `constants` | array | Game config values grouped by category |
| `tables` | array | Structured data tables (referenced in endpoints as `values.tableName`) |

### Endpoint File Format (`endpoints/*.json`)

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
    {
      "type": "read",
      "collection": "players",
      "as": "player"
    },
    {
      "type": "lookup",
      "source": "values",
      "table": "ore_types",
      "key": "id",
      "value": "{{input.oreType}}",
      "as": "ore"
    },
    {
      "type": "condition",
      "field": "player.phaserTier",
      "operator": "gte",
      "value": "{{ore.tier}}",
      "onFail": "error",
      "errorMessage": "Phaser tier too low"
    },
    {
      "type": "transform",
      "field": "player.ores.{{input.oreType}}",
      "operation": "add",
      "value": "{{input.amount}}"
    },
    {
      "type": "write",
      "collection": "players"
    }
  ],
  "response": {
    "status": 200,
    "body": { "ok": true }
  }
}
```

**Step Types:**
| Type | Description |
|------|-------------|
| `read` | Read a record from a collection into a variable |
| `write` | Write/update a record in a collection |
| `transform` | Modify a field value (add, subtract, set, multiply, etc.) |
| `condition` | Check a condition; fail with error or skip remaining steps |
| `lookup` | Look up a row in a values table by key |
| `filter` | Filter an array field by criteria |

### Workflow File Format (`workflows/*.json`)

```json
{
  "id": "check-currency",
  "name": "Check Currency",
  "description": "Verify player has enough currency",
  "steps": [
    {
      "type": "condition",
      "field": "player.currency",
      "operator": "gte",
      "value": "{{input.cost}}",
      "onFail": "error",
      "errorMessage": "Not enough currency"
    }
  ]
}
```

Workflows are referenced by ID from endpoint steps and execute inline.

## Library Source Files

### Runtime (`Code/`)

| File | Description |
|------|-------------|
| `NetworkStorageClient.cs` | Main HTTP client for runtime API calls (GET/POST) |
| `NetworkStorageExtensions.cs` | Extension methods for common data access patterns |
| `JsonHelpers.cs` | JSON utilities: merge, diff, extract, pretty-print |
| `NetLog.cs` | Scoped logging with `[NetworkStorage]` prefix |
| `SaveStateTracker.cs` | Tracks data changes for auto-save / dirty state detection |

### Editor (`Editor/`)

| File | Description |
|------|-------------|
| `SyncToolConfig.cs` | Loads `.env`, manages all file paths, scaffolds new projects |
| `SyncToolApi.cs` | HTTP client for the management API (secret key auth) |
| `SyncToolTransforms.cs` | Converts between local JSON format ↔ server API format |
| `SyncToolWindow.cs` | Main Sync Tool DockWindow UI (check/push/pull/diff) |
| `SetupWindow.cs` | Credentials setup DockWindow UI |
| `DiffViewWindow.cs` | Side-by-side JSON diff viewer |
| `ConfirmDialog.cs` | Modal confirmation dialog for destructive operations |
