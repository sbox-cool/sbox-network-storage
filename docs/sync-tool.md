# Network Storage — Sync Tool

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
