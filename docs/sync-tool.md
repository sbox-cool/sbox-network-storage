# Network Storage — Sync Tool

> **Note:** For the most up-to-date documentation, visit https://sbox.cool/wiki/network-storage-v3 — these repo docs may be outdated.

The Sync Tool is the main editor window for managing your game's backend data. Open it via **Editor → Network Storage → Sync Tool**.

## What It Does

The Sync Tool compares your local JSON files (in `Editor/Network Storage/`) with the remote state on sbox.cool, and lets you push or pull changes.

## Status Indicators

| Icon | Badge | Meaning |
|------|-------|---------|
| ✓ | Synced | Local matches remote |
| ▲ | Local, not pushed | Exists locally but not on server |
| ▼ | Remote only | Exists on server but not locally |
| ● | Changed | Local and remote have different content |

## Operations

### Check Remote
Click **Check Remote** (or open the window) to compare local vs remote for all resources. This fetches the current server state and diffs it against your local files.

### Push
Uploads your local files to the server.

- **Push** (per item) — pushes a single endpoint, collection, or workflow
- **Push All** — pushes all local resources at once
- If the remote version is newer, you get a warning dialog before overwriting

After a successful push, the item's status changes to **Synced**.

### Pull
Downloads the server's version to your local files.

- **Pull** (per item) — downloads a single resource
- Overwrites the local JSON file with the server's version

### View Diff
For items that differ, click **Diff** to open a side-by-side comparison showing exactly what changed (added, removed, modified lines).

## Resource Types

### Endpoints (`endpoints/*.json`)
Server-side pipelines that your game calls via the API. Each endpoint is a sequence of steps (read, write, condition, transform, etc.) that execute atomically on the server.

Example: `endpoints/sell-ore.json`
```json
{
  "slug": "sell-ore",
  "description": "Sell ore from player inventory",
  "steps": [
    { "type": "read", "collection": "players", "as": "player" },
    { "type": "condition", "field": "player.ores.{{input.oreType}}", "operator": "gte", "value": "{{input.amount}}" },
    { "type": "transform", "field": "player.currency", "operation": "add", "value": "{{input.amount * values.ore_price}}" },
    { "type": "write", "collection": "players", "data": { "currency": "{{player.currency}}" } }
  ]
}
```

### Collections (`collections/*.json`)
Define the schema and configuration for data collections. Collections store per-player or global data.

Example: `collections/players.json`
```json
{
  "name": "players",
  "collectionType": "per-steamid",
  "schema": {
    "currency": { "type": "number", "default": 0 },
    "xp": { "type": "number", "default": 0 },
    "level": { "type": "number", "default": 1 }
  }
}
```

### Workflows (`workflows/*.json`)
Reusable step sequences that can be referenced by endpoints. Use workflows for common validation patterns (e.g., "has enough currency", "has inventory space").

## File Format

All files use standard JSON. The Sync Tool handles the conversion between local format and the server's expected format via `SyncToolTransforms`.

Key differences between local and server format:
- Local files omit server-managed fields (`id`, `createdAt`, `version`)
- Server format includes `id` fields — the Sync Tool preserves these during push by matching on `slug` (endpoints) or `name` (collections)
- New resources get auto-generated IDs on first push

## How Push Works (Technical)

1. Load local JSON files from disk
2. Fetch current remote state from API
3. Transform local format → server format (preserving existing IDs by matching slug/name)
4. PUT the transformed data to the management API
5. Clear cached remote state so next check fetches fresh data
6. Update status indicators (LocalOnly → InSync on success)

## Management API Authentication

The Sync Tool uses the Management API, which requires dual-key authentication. Every request includes both keys as HTTP headers:

| Header | Value | Description |
|--------|-------|-------------|
| `x-api-key` | `sbox_sk_...` | Your 128-character secret key (editor-only, never shipped) |
| `x-public-key` | `sbox_ns_...` | Your public API key |

Both headers are required. If either is missing or invalid, the server returns HTTP 401 with a specific error code and an `expected` object showing the required headers.

### Management API Endpoints

Base URL: `https://api.sboxcool.com/v3/manage/{projectId}`

| Method | Path | Permission | Description |
|--------|------|------------|-------------|
| GET | `/endpoints` | `endpoints: r` | Fetch all endpoint definitions |
| PUT | `/endpoints` | `endpoints: rw` | Push endpoint definitions |
| GET | `/collections` | `collections: r` | Fetch all collection schemas |
| PUT | `/collections` | `collections: rw` | Push collection schemas |
| GET | `/workflows` | `workflows: r` | Fetch all workflow definitions |
| PUT | `/workflows` | `workflows: rw` | Push workflow definitions |
| GET | `/game-values` | `game_values: r` | Fetch game values |
| PUT | `/game-values` | `game_values: rw` | Push game values |
| GET | `/rate-limit-rules` | `rate_limits: r` | Fetch rate limit rules |
| PUT | `/rate-limit-rules` | `rate_limits: rw` | Push rate limit rules |
| GET | `/validate` | (none) | Test credentials |
| DELETE | `/keys` | (none) | Destroy a key pair |

### Example Request

```
GET https://api.sboxcool.com/v3/manage/your-project-id/endpoints
x-api-key: sbox_sk_a1b2c3...
x-public-key: sbox_ns_d4e5f6...
```

### Authentication Error Codes

| Code | Meaning |
|------|---------|
| `MISSING_SECRET_KEY` | `x-api-key` header missing or does not start with `sbox_sk_` |
| `MISSING_PUBLIC_KEY` | `x-public-key` header missing or does not start with `sbox_ns_` |
| `INVALID_SECRET_KEY` | Secret key not recognized or does not belong to this project |
| `INVALID_PUBLIC_KEY` | Public key not recognized or does not belong to this project |
| `PROJECT_MISMATCH` | Secret key belongs to a different project than the one in the URL |
| `KEY_DISABLED` | The API key has been disabled on the dashboard |
| `KEY_UPGRADE_REQUIRED` | Secret key uses the old format -- generate a new one from the dashboard |
| `FORBIDDEN` | Key lacks the required permission scope for this operation |
| `PROJECT_NOT_FOUND` | No project found with the given ID |

Error responses include an `expected` object:

```json
{
  "ok": false,
  "error": "MISSING_SECRET_KEY",
  "message": "Missing or invalid x-api-key header. Must be a secret key (sbox_sk_*).",
  "expected": {
    "headers": {
      "x-api-key": "sbox_sk_... (128-character secret key)",
      "x-public-key": "sbox_ns_... (public API key)"
    }
  }
}
```

### Key Permissions

Secret keys can be scoped to limit access. Each scope supports `none`, `r` (read-only), or `rw` (read+write):

| Scope | Controls |
|-------|----------|
| `endpoints` | Endpoint pipeline definitions |
| `collections` | Collection schemas and settings |
| `workflows` | Validation workflow definitions |
| `game_values` | Constants and lookup tables |
| `rate_limits` | Rate limit rules |

A key with `null` permissions has full access to all scopes.
