import { McpServer, ResourceTemplate } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { readFileSync } from "fs";
import { resolve, dirname } from "path";
import { fileURLToPath } from "url";

// ─── Resolve repo root ───────────────────────────────────────────────────────

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const REPO_ROOT = resolve(__dirname, "..");

// ─── Validation Constants ─────────────────────────────────────────────────────

const VALID_STEP_TYPES = ["read", "write", "transform", "condition", "lookup", "filter", "workflow"] as const;
const VALID_OPERATIONS = ["add", "subtract", "set", "multiply", "divide", "append", "remove"] as const;
const VALID_OPERATORS = ["eq", "neq", "gt", "gte", "lt", "lte", "contains", "exists", "not_exists"] as const;
const VALID_ONFAIL = ["error", "skip"] as const;
const VALID_COLLECTION_TYPES = ["per-steamid", "global"] as const;
const VALID_ACCESS_MODES = ["public", "private"] as const;
const VALID_FIELD_TYPES = ["string", "number", "boolean", "object", "array"] as const;
const VALID_METHODS = ["GET", "POST"] as const;
const VALID_DATA_SOURCES = ["api_then_json", "api_only", "json_only"] as const;

const COLLECTION_NAME_PATTERN = /^[a-z0-9_]+$/;
const ENDPOINT_SLUG_PATTERN = /^[a-z0-9-]+$/;
const WORKFLOW_ID_PATTERN = /^[a-z0-9-]+$/;
const TEMPLATE_PATTERN = /\{\{([^}]*)\}\}/g;

const MAX_STEPS_PER_ENDPOINT = 20;
const MAX_READ_LOOKUP_FILTER_STEPS = 10;
const MAX_ENDPOINTS_PER_PROJECT = 20;
const MAX_COLLECTIONS_PER_PROJECT = 50;

const REQUIRED_ENV_KEYS = ["SBOXCOOL_PROJECT_ID", "SBOXCOOL_PUBLIC_KEY", "SBOXCOOL_SECRET_KEY"] as const;
const OPTIONAL_ENV_KEYS = ["SBOXCOOL_BASE_URL", "SBOXCOOL_API_VERSION", "SBOXCOOL_DATA_FOLDER", "SBOXCOOL_DATA_SOURCE"] as const;

const STEP_REQUIRED_FIELDS: Record<string, string[]> = {
  read: ["collection", "as"],
  write: ["collection"],
  transform: ["field", "operation", "value"],
  condition: ["field", "operator", "value", "onFail"],
  lookup: ["source", "table", "key", "value", "as"],
  filter: ["source", "field", "operator", "value", "as"],
  workflow: ["workflow"],
};

// ─── Documentation Content ────────────────────────────────────────────────────

const DOCUMENTATION: Record<string, string> = {
  overview: `# Network Storage — Overview

Network Storage is an s&box editor library providing a complete backend-as-a-service for s&box games. It connects your game to the sbox.cool cloud platform for persistent player data, game configuration, and server-side logic — all managed through JSON files.

## Two Halves

1. **Runtime Client** (\`Code/\`) — ships with your game, makes API calls to read/write data and call endpoints
2. **Editor Sync Tool** (\`Editor/\`) — developer-only, manages collections/endpoints/workflows via GUI that syncs with sbox.cool

## Data Flow

- Developers edit JSON files in \`Editor/Network Storage/\`
- Sync Tool pushes changes to sbox.cool (live immediately)
- Game code uses \`NetworkStorageClient\` to call endpoints
- Server executes pipelines atomically (read → validate → transform → write → respond)

## Key Principles

- **Editor/ never ships** — secrets and management tools stay on dev machines
- **JSON as source of truth** — version-controllable, human-readable, PR-reviewable
- **Backend-first design** — all validation/logic runs server-side via endpoints
- **HTTP 200 for rejections** — s&box's Http API throws on 4xx/5xx, so errors return 200 with \`ok: false\``,

  collections: `# Collections

Collections define where and how your data is stored. Each collection is a JSON file in \`Editor/Network Storage/collections/\`.

## Collection JSON Format

\`\`\`json
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
\`\`\`

## Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| \`name\` | string | Yes | Collection identifier (lowercase alphanumeric + underscores, e.g. \`players\`, \`game_config\`) |
| \`description\` | string | No | Human-readable description |
| \`collectionType\` | string | No | \`per-steamid\` (one record per player) or \`global\` (shared data). Default: \`per-steamid\` |
| \`accessMode\` | string | No | \`public\` or \`private\`. Default: \`public\` |
| \`maxRecords\` | number | No | Max records per user (usually 1 for per-steamid). Default: 1 |
| \`allowRecordDelete\` | boolean | No | Whether records can be deleted. Default: false |
| \`requireSaveVersion\` | boolean | No | Whether save version tracking is required. Default: false |
| \`rateLimits\` | object | No | Rate limiting config with \`mode\` field |
| \`rateLimitAction\` | string | No | Action on rate limit: \`reject\`. Default: \`reject\` |
| \`webhookOnRateLimit\` | boolean | No | Send webhook on rate limit. Default: false |
| \`schema\` | object | Yes | Field definitions with types and defaults |
| \`constants\` | array | No | Game config values grouped by category |
| \`tables\` | array | No | Structured data tables (referenced in endpoints as \`values.tableName\`) |

## Schema Field Types

Each field in \`schema\` must have a \`type\`: \`string\`, \`number\`, \`boolean\`, \`object\`, or \`array\`.

- \`default\` — the initial value when a new record is created
- For \`object\` types, use \`properties\` to define nested fields
- For \`array\` types, use \`default: []\`

## Naming Rules

Collection names must match \`/^[a-z0-9_]+$/\` — lowercase letters, digits, and underscores only. Max 50 collections per project.`,

  endpoints: `# Endpoints

Endpoints are server-side pipelines that your game calls via the API. Each endpoint is a JSON file in \`Editor/Network Storage/endpoints/\`.

## Endpoint JSON Format

\`\`\`json
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
\`\`\`

## Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| \`slug\` | string | Yes | URL slug (lowercase alphanumeric + hyphens, e.g. \`mine-ore\`, \`sell-items\`) |
| \`name\` | string | No | Human-readable name |
| \`method\` | string | No | \`GET\` or \`POST\`. Default: \`POST\` |
| \`description\` | string | No | Description (max 256 chars) |
| \`enabled\` | boolean | No | Whether the endpoint is active. Default: true |
| \`input\` | object | No | Input field definitions with \`type\` and optional \`required\` |
| \`steps\` | array | Yes | Pipeline steps (max 20). This is where all the logic lives |
| \`response\` | object | No | Default response with \`status\` and \`body\` |

## Slug Rules

Endpoint slugs must match \`/^[a-z0-9-]+$/\` — lowercase letters, digits, and hyphens only. Max 20 endpoints per project.

## Calling Endpoints from Game Code

\`\`\`csharp
// POST endpoint with input
var result = await NetworkStorage.CallEndpoint("mine-ore", new { oreType = "iron", amount = 5 });

// GET endpoint (no input)
var data = await NetworkStorage.CallEndpoint("get-leaderboard");
\`\`\``,

  workflows: `# Workflows

Workflows are reusable validation/logic blocks that endpoints can reference. Each workflow is a JSON file in \`Editor/Network Storage/workflows/\`.

## Workflow JSON Format

\`\`\`json
{
  "id": "check-currency",
  "name": "Check Currency",
  "description": "Verify player has enough currency",
  "condition": {
    "field": "player.currency",
    "op": "gte",
    "value": "{{input.cost}}"
  },
  "onFail": {
    "reject": true,
    "errorCode": "NOT_ENOUGH_CURRENCY",
    "errorMessage": "Not enough currency. Have: {{player.currency}}, need: {{input.cost}}"
  },
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
\`\`\`

## Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| \`id\` | string | Yes | Workflow identifier (lowercase alphanumeric + hyphens) |
| \`name\` | string | No | Human-readable name |
| \`description\` | string | No | Description of what this workflow validates |
| \`condition\` | object | No | Top-level condition with \`field\`, \`op\`, \`value\` |
| \`onFail\` | string/object | No | What happens when condition fails (see below) |
| \`steps\` | array | No | Steps to execute (same step types as endpoints) |

## onFail Options

\`onFail\` can be a simple string (\`"error"\`) or a detailed object:

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| \`reject\` | boolean | true | Short-circuit and return error to client |
| \`errorCode\` | string | \`"CONDITION_FAILED"\` | Error code in response |
| \`errorMessage\` | string | \`"Condition check failed."\` | Human-readable message (supports \`{{templates}}\`) |
| \`severity\` | string | null | \`"warning"\` or \`"critical"\` — logged for monitoring |
| \`status\` | number | 200 | HTTP status code (keep at 200 so s&box client can read response body) |
| \`flag\` | boolean | false | Log a player flag for anti-cheat review |
| \`webhook\` | boolean | false | Send Discord webhook on failure |
| \`clamp\` | boolean | false | Cap the input value instead of rejecting |

## Referencing from Endpoints

Use a \`workflow\` step in your endpoint:

\`\`\`json
{ "type": "workflow", "workflow": "check-currency" }
\`\`\`

## Compound Conditions

Use \`all\` (AND) or \`any\` (OR) for compound checks:

\`\`\`json
{
  "condition": {
    "all": [
      { "field": "player.level", "op": "gte", "value": 5 },
      { "field": "player.currency", "op": "gte", "value": "{{input.cost}}" }
    ]
  }
}
\`\`\``,

  steps: `# Endpoint Step Types — Complete Reference

Endpoints execute a pipeline of steps in order. Each step has a \`type\` field and type-specific properties.

## read — Load a Collection Record

Loads a player's record from a collection into a named variable.

\`\`\`json
{ "type": "read", "collection": "players", "as": "player" }
\`\`\`

| Field | Required | Description |
|-------|----------|-------------|
| \`collection\` | Yes | Collection name to read from |
| \`as\` | Yes | Variable name to store the result (used in later steps as \`{{player.fieldName}}\`) |

## write — Save a Collection Record

Writes/updates a record in a collection. All writes are **deferred** until all conditions pass.

\`\`\`json
{ "type": "write", "collection": "players" }
\`\`\`

| Field | Required | Description |
|-------|----------|-------------|
| \`collection\` | Yes | Collection name to write to (must have been \`read\` earlier) |

## transform — Modify a Field Value

Modifies a field value using an operation.

\`\`\`json
{ "type": "transform", "field": "player.currency", "operation": "add", "value": 100 }
{ "type": "transform", "field": "player.ores.{{input.oreType}}", "operation": "add", "value": "{{input.amount}}" }
\`\`\`

| Field | Required | Description |
|-------|----------|-------------|
| \`field\` | Yes | Dot-path to the field (supports \`{{templates}}\`) |
| \`operation\` | Yes | One of: \`add\`, \`subtract\`, \`set\`, \`multiply\`, \`divide\`, \`append\`, \`remove\` |
| \`value\` | Yes | The value to use (supports \`{{templates}}\`) |

**Operations:**
- \`add\` — add numeric value
- \`subtract\` — subtract numeric value
- \`set\` — overwrite with new value
- \`multiply\` — multiply by value
- \`divide\` — divide by value
- \`append\` — add item to array
- \`remove\` — remove item from array

## condition — Validate a Check

Checks a condition and either rejects the request or skips remaining steps.

\`\`\`json
{
  "type": "condition",
  "field": "player.currency",
  "operator": "gte",
  "value": "{{input.cost}}",
  "onFail": "error",
  "errorMessage": "Not enough currency"
}
\`\`\`

| Field | Required | Description |
|-------|----------|-------------|
| \`field\` | Yes | Dot-path to the field to check |
| \`operator\` | Yes | One of: \`eq\`, \`neq\`, \`gt\`, \`gte\`, \`lt\`, \`lte\`, \`contains\`, \`exists\`, \`not_exists\` |
| \`value\` | Yes* | Value to compare against (*not required for \`exists\`/\`not_exists\`) |
| \`onFail\` | Yes | \`"error"\` (reject request) or \`"skip"\` (skip remaining steps) |
| \`errorMessage\` | No | Human-readable error message (supports \`{{templates}}\`) |
| \`errorCode\` | No | Custom error code (e.g. \`"BACKPACK_FULL"\`) |

**Operators:**
- \`eq\` — equals
- \`neq\` — not equals
- \`gt\` — greater than
- \`gte\` — greater than or equal
- \`lt\` — less than
- \`lte\` — less than or equal
- \`contains\` — array contains value or string contains substring
- \`exists\` — field exists and is not null/undefined
- \`not_exists\` — field does not exist or is null/undefined

## lookup — Find a Table Row

Looks up a single row in a values table by key match.

\`\`\`json
{ "type": "lookup", "source": "values", "table": "ore_types", "key": "id", "value": "{{input.oreType}}", "as": "ore" }
\`\`\`

| Field | Required | Description |
|-------|----------|-------------|
| \`source\` | Yes | Data source (typically \`"values"\`) |
| \`table\` | Yes | Table name (defined in collection's \`tables\` array) |
| \`key\` | Yes | Column to match against |
| \`value\` | Yes | Value to match (supports \`{{templates}}\`) |
| \`as\` | Yes | Variable name for the matched row |

## filter — Find Multiple Records

Filters records by field match. Max 500 records scanned.

\`\`\`json
{ "type": "filter", "source": "leaderboard", "field": "score", "operator": "gte", "value": 100, "as": "topPlayers" }
\`\`\`

| Field | Required | Description |
|-------|----------|-------------|
| \`source\` | Yes | Collection or data source to filter |
| \`field\` | Yes | Field to match against |
| \`operator\` | Yes | Comparison operator |
| \`value\` | Yes | Value to compare |
| \`as\` | Yes | Variable name for the filtered results |

## workflow — Run a Reusable Workflow

Executes a workflow by its ID. The workflow's steps run inline.

\`\`\`json
{ "type": "workflow", "workflow": "check-currency" }
\`\`\`

| Field | Required | Description |
|-------|----------|-------------|
| \`workflow\` | Yes | The workflow ID to execute |`,

  templates: `# Template Syntax — \`{{template}}\` Reference

Templates let you reference dynamic values in endpoint steps. They are resolved at execution time by the server.

## Syntax

\`{{source.path}}\` — reference a value from the execution context.

## Sources

| Source | Description | Example |
|--------|------------|---------|
| \`input\` | Request input fields | \`{{input.oreType}}\`, \`{{input.amount}}\` |
| \`{alias}\` | Data from a \`read\` or \`lookup\` step | \`{{player.currency}}\`, \`{{ore.tier}}\` |
| \`values\` | Game values / tables | \`{{values.ore_types}}\` |

## Path Traversal

Use dots to access nested fields:
- \`{{player.inventory.items}}\` — nested object access
- \`{{player.ores.iron}}\` — dynamic key access

## Dynamic Keys

Templates can be used inside field paths:
- \`{{player.ores.{{input.oreType}}}}\` — resolves the ore type from input first

## Negation

Prefix with \`-\` for numeric negation:
- \`{{-input.amount}}\` — negates the value

## Where Templates Work

Templates are resolved in these step fields:
- \`transform.field\` — the target field path
- \`transform.value\` — the value to apply
- \`condition.field\` — the field to check
- \`condition.value\` — the comparison value
- \`lookup.value\` — the lookup key value
- \`filter.value\` — the filter comparison value
- \`response.body\` — values in the response body
- \`onFail.errorMessage\` — workflow error messages

## Common Patterns

\`\`\`json
// Reference input
"value": "{{input.amount}}"

// Reference read data
"field": "player.currency"
"value": "{{player.currency}}"

// Dynamic field path
"field": "player.ores.{{input.oreType}}"

// Negation
"value": "{{-input.cost}}"
\`\`\``,

  "env-config": `# Environment Configuration (.env)

The \`.env\` file stores API credentials for the editor Sync Tool. Located at \`Editor/Network Storage/config/.env\` (gitignored).

## Format

\`\`\`env
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
\`\`\`

## Required Keys

| Key | Prefix | Description |
|-----|--------|-------------|
| \`SBOXCOOL_PROJECT_ID\` | — | Your project ID from the sbox.cool dashboard |
| \`SBOXCOOL_PUBLIC_KEY\` | \`sbox_ns_\` | Public key for game client (safe to ship) |
| \`SBOXCOOL_SECRET_KEY\` | \`sbox_sk_\` | Secret key for editor only (NEVER ship) |

## Optional Keys

| Key | Default | Description |
|-----|---------|-------------|
| \`SBOXCOOL_BASE_URL\` | \`https://api.sboxcool.com\` | API base URL |
| \`SBOXCOOL_API_VERSION\` | \`v3\` | API version |
| \`SBOXCOOL_DATA_FOLDER\` | \`Network Storage\` | Editor subfolder name |
| \`SBOXCOOL_DATA_SOURCE\` | \`api_then_json\` | Data source mode: \`api_then_json\`, \`api_only\`, \`json_only\` |

## Security

- The \`.env\` file is gitignored — never commit it
- The secret key (\`sbox_sk_\`) must NEVER ship with your game
- The public key (\`sbox_ns_\`) is safe to include in published builds
- \`Editor/\` directory is never published by s&box`,

  setup: `# Setup Guide

## Installation

1. In the s&box editor, open **Library Manager**
2. Search for **Network Storage** by \`sboxcool\`
3. Click **Add to Project**

On first load, the library scaffolds:
\`\`\`
Editor/Network Storage/
├── config/.env           ← Placeholder credentials
├── config/.gitignore     ← Keeps .env out of git
├── collections/players.json ← Sample collection
├── endpoints/init-player.json ← Sample endpoint
└── workflows/            ← Empty
\`\`\`

## Get API Keys

1. Go to https://sbox.cool/tools/network-storage and sign in with Steam
2. Create a project or open existing
3. Go to **API Keys** → create a Public key (\`sbox_ns_\`) and Secret key (\`sbox_sk_\`)

## Enter Credentials

1. Open **Editor → Network Storage → Setup**
2. Paste Project ID, Public Key, and Secret Key
3. Click **Save Configuration**

This writes two files:
- \`Editor/Network Storage/config/.env\` — all keys (editor only, gitignored)
- \`Assets/network-storage.credentials.json\` — Project ID + Public Key (ships with game)

## Test Connection

Click **Test Connection** to verify credentials. Green checkmarks for Project ID, Secret Key, and Public Key.

## Credential Flow

\`\`\`
sbox.cool Dashboard → Project ID + Public Key + Secret Key
    ↓
Setup Window (Save Configuration)
    ├→ .env (editor only, gitignored)
    └→ credentials.json (ships with game)
        ↓
    NetworkStorage.AutoConfigure() (reads on first API call)
        ↓
    NetworkStorage.CallEndpoint() (uses Project ID + Public Key + Steam token)
\`\`\``,

  "runtime-client": `# Runtime Client API

The runtime client (\`NetworkStorageClient\`) is what your game code uses at runtime. It ships with your game.

## Auto-Configuration

The client **automatically reads** from \`Assets/network-storage.credentials.json\` on first API use:

\`\`\`csharp
// No Configure() needed — auto-configures
var player = await NetworkStorage.CallEndpoint("load-player");
var values = await NetworkStorage.GetGameValues();
\`\`\`

## Manual Configuration

\`\`\`csharp
NetworkStorage.Configure("your-project-id", "sbox_ns_your_key");
\`\`\`

## API Methods

### CallEndpoint
\`\`\`csharp
// POST with input
var result = await NetworkStorage.CallEndpoint("sell-ore", new { oreType = "iron", amount = 50 });

// GET (no input)
var data = await NetworkStorage.CallEndpoint("get-leaderboard");
\`\`\`

### GetGameValues
\`\`\`csharp
var values = await NetworkStorage.GetGameValues();
\`\`\`

### GetDocument
\`\`\`csharp
var playerData = await NetworkStorage.GetDocument("players");
var doc = await NetworkStorage.GetDocument("players", "76561198012345678");
\`\`\`

## Properties

| Property | Type | Description |
|----------|------|-------------|
| \`IsConfigured\` | bool | True after config loads |
| \`ProjectId\` | string | Active project ID |
| \`ApiKey\` | string | Active public API key |
| \`BaseUrl\` | string | API base URL |
| \`ApiVersion\` | string | API version |
| \`ApiRoot\` | string | Full versioned root |

## Error Handling

All methods return \`JsonElement?\` — \`null\` on failure. Errors are logged to console with \`[NetworkStorage]\` prefix.

\`\`\`csharp
var result = await NetworkStorage.CallEndpoint("buy-upgrade", input);
if (result == null)
{
    // Server rejected or network failed — check console for details
    return;
}
\`\`\``,

  "error-handling": `# Error Handling & Rejection Guide

## How Rejections Work

\`\`\`
Client calls endpoint → Server runs steps → Condition fails
    → HTTP 200 with { ok: false, error: { code, message } }
    → Client returns null → Optimistic update reverts → UI shows error
\`\`\`

**Why HTTP 200?** s&box's \`Http.RequestStringAsync\` throws on 4xx/5xx, losing the response body. HTTP 200 with \`ok: false\` lets the client read the full error.

## Response Formats

### Success
\`\`\`json
{ "ok": true, "success": true, "xp": 240, "currency": 500 }
\`\`\`

### Rejection
\`\`\`json
{ "ok": false, "error": { "code": "BACKPACK_FULL", "message": "Not enough space." }, "severity": "warning" }
\`\`\`

### System Error
\`\`\`json
{ "ok": false, "error": { "code": "INTERNAL_ERROR", "message": "An internal error occurred." } }
\`\`\`

## Common Error Codes

| Code | Meaning |
|------|---------|
| \`UNAUTHORIZED\` | Invalid/missing API key |
| \`PROJECT_DISABLED\` | Project disabled on sbox.cool |
| \`QUOTA_EXCEEDED\` | Monthly usage limit hit |
| \`SCHEMA_VALIDATION_FAILED\` | Data doesn't match schema |
| \`ENDPOINT_NOT_FOUND\` | Endpoint slug doesn't exist on server |
| \`ENDPOINT_DISABLED\` | Endpoint exists but \`enabled: false\` |
| \`CONDITION_FAILED\` | Generic condition failure |
| \`INTERNAL_ERROR\` | Server crash — check logs |
| \`RATE_LIMIT_DAILY\` | Rate limit exceeded |
| \`INVALID_JSON\` | Request body not valid JSON |
| \`INVALID_BODY\` | Request body shape wrong |

## Optimistic Update Pattern

\`\`\`csharp
// 1. Apply optimistic update
CurrentOreKg += kg;
// 2. Call server
var result = await NetworkStorage.CallEndpoint("mine-ore", input);
if (result.HasValue)
    Apply(result.Value);  // 3a. Success
else
    CurrentOreKg -= kg;    // 3b. Revert
\`\`\``,

  "sync-tool": `# Sync Tool

Open via **Editor → Network Storage → Sync Tool**.

## What It Does

Compares local JSON files with sbox.cool server state, lets you push/pull changes.

## Status Indicators

| Icon | Meaning |
|------|---------|
| ✓ | Synced — local matches remote |
| ▲ | Local only — needs push |
| ▼ | Remote only — needs pull |
| ● | Changed — local and remote differ |

## Operations

- **Check Remote** — compare local vs remote
- **Push** (per item) — upload one resource
- **Push All** — upload all local resources
- **Pull** (per item) — download server version
- **Diff** — side-by-side comparison

## Important

- Always push **collections before endpoints** — endpoints reference collections
- New resources get auto-generated IDs on first push
- Changes are live immediately after push`,

  constraints: `# Constraints & Limits

## Naming Rules
- Collection names: \`/^[a-z0-9_]+$/\` (lowercase, digits, underscores)
- Endpoint slugs: \`/^[a-z0-9-]+$/\` (lowercase, digits, hyphens)
- Workflow IDs: \`/^[a-z0-9-]+$/\` (lowercase, digits, hyphens)

## Maximums
- Steps per endpoint: 20
- Read/lookup/filter steps per endpoint: 10
- Endpoints per project: 20
- Collections per project: 50
- Records scanned in filter: 500

## Valid Enums
- Step types: read, write, transform, condition, lookup, filter, workflow
- Transform operations: add, subtract, set, multiply, divide, append, remove
- Condition operators: eq, neq, gt, gte, lt, lte, contains, exists, not_exists
- onFail values: error, skip
- Collection types: per-steamid, global
- Access modes: public, private
- HTTP methods: GET, POST
- Data sources: api_then_json, api_only, json_only

## API Keys
- Public key prefix: \`sbox_ns_\`
- Secret key prefix: \`sbox_sk_\`

## Response Pattern
- Server returns HTTP 200 with \`{ ok: false, error: { code, message } }\` for rejections
- Non-200 status codes cause s&box Http API to throw and lose response body`,

  security: `# Security

## Key Types

| Key | Prefix | Ships with game? | Purpose |
|-----|--------|------------------|---------|
| Public Key | \`sbox_ns_\` | Yes | Runtime API calls (read data, call endpoints) |
| Secret Key | \`sbox_sk_\` | **NEVER** | Editor sync tool (manage collections/endpoints/workflows) |

## What Ships

- \`Assets/network-storage.credentials.json\` — Project ID + Public Key only
- \`Code/\` directory — runtime client code

## What Stays Local

- \`Editor/\` directory — never published by s&box
- \`Editor/Network Storage/config/.env\` — all keys (gitignored)
- Secret key is only used by the editor Sync Tool

## Rules

1. Never commit \`.env\` files
2. Never log or display secret keys
3. Never include secret keys in game code
4. All data validation must happen server-side via endpoints
5. Client-only checks can be bypassed — always validate on server`,
};

// ─── Example Content ──────────────────────────────────────────────────────────

interface Example {
  type: string;
  name: string;
  description: string;
  json: string;
}

const EXAMPLES: Record<string, Example[]> = {
  "player-data": [
    {
      type: "collection",
      name: "players",
      description: "Basic player data collection with currency, XP, and level",
      json: JSON.stringify({
        name: "players",
        description: "Player save data",
        collectionType: "per-steamid",
        accessMode: "public",
        maxRecords: 1,
        schema: {
          currency: { type: "number", default: 0 },
          xp: { type: "number", default: 0 },
          level: { type: "number", default: 1 },
          displayName: { type: "string", default: "" },
        },
      }, null, 2),
    },
    {
      type: "endpoint",
      name: "init-player",
      description: "Initialize a new player record with default values",
      json: JSON.stringify({
        slug: "init-player",
        name: "Initialize Player",
        method: "POST",
        description: "Create or load a player record",
        enabled: true,
        input: {
          displayName: { type: "string", required: true },
        },
        steps: [
          { type: "read", collection: "players", as: "player" },
          { type: "transform", field: "player.displayName", operation: "set", value: "{{input.displayName}}" },
          { type: "write", collection: "players" },
        ],
        response: { status: 200, body: { ok: true } },
      }, null, 2),
    },
  ],
  inventory: [
    {
      type: "collection",
      name: "players (with inventory)",
      description: "Player collection with inventory and backpack capacity",
      json: JSON.stringify({
        name: "players",
        description: "Player data with inventory system",
        collectionType: "per-steamid",
        accessMode: "public",
        maxRecords: 1,
        schema: {
          currency: { type: "number", default: 0 },
          backpackCapacity: { type: "number", default: 100 },
          currentOreKg: { type: "number", default: 0 },
          ores: { type: "object", default: {} },
          phaserTier: { type: "number", default: 1 },
        },
        constants: [
          { group: "mining", values: { base_capacity: 100, capacity_per_upgrade: 50 } },
        ],
        tables: [
          {
            name: "ore_types",
            columns: ["id", "name", "tier", "basePrice"],
            rows: [
              ["iron", "Iron Ore", 1, 10],
              ["copper", "Copper", 2, 25],
              ["gold", "Gold", 3, 50],
            ],
          },
        ],
      }, null, 2),
    },
    {
      type: "endpoint",
      name: "mine-ore",
      description: "Mine ore with backpack capacity check and tier validation",
      json: JSON.stringify({
        slug: "mine-ore",
        name: "Mine Ore",
        method: "POST",
        description: "Process an ore mining action",
        enabled: true,
        input: {
          oreType: { type: "string", required: true },
          amount: { type: "number", required: true },
        },
        steps: [
          { type: "read", collection: "players", as: "player" },
          { type: "lookup", source: "values", table: "ore_types", key: "id", value: "{{input.oreType}}", as: "ore" },
          { type: "condition", field: "player.phaserTier", operator: "gte", value: "{{ore.tier}}", onFail: "error", errorMessage: "Phaser tier too low for this ore" },
          { type: "condition", field: "player.currentOreKg", operator: "lt", value: "{{player.backpackCapacity}}", onFail: "error", errorCode: "BACKPACK_FULL", errorMessage: "Backpack is full" },
          { type: "transform", field: "player.ores.{{input.oreType}}", operation: "add", value: "{{input.amount}}" },
          { type: "transform", field: "player.currentOreKg", operation: "add", value: "{{input.amount}}" },
          { type: "write", collection: "players" },
        ],
        response: { status: 200, body: { ok: true } },
      }, null, 2),
    },
    {
      type: "workflow",
      name: "check-backpack-space",
      description: "Verify player has backpack space available",
      json: JSON.stringify({
        id: "check-backpack-space",
        name: "Check Backpack Space",
        description: "Verify player has room in their backpack",
        condition: {
          field: "player.currentOreKg",
          op: "lt",
          value: "{{player.backpackCapacity}}",
        },
        onFail: {
          reject: true,
          errorCode: "BACKPACK_FULL",
          errorMessage: "Not enough backpack space. Current: {{player.currentOreKg}}kg / {{player.backpackCapacity}}kg.",
        },
        steps: [
          {
            type: "condition",
            field: "player.currentOreKg",
            operator: "lt",
            value: "{{player.backpackCapacity}}",
            onFail: "error",
            errorMessage: "Backpack is full",
          },
        ],
      }, null, 2),
    },
  ],
  currency: [
    {
      type: "endpoint",
      name: "sell-ore",
      description: "Sell ore for currency with ore amount validation",
      json: JSON.stringify({
        slug: "sell-ore",
        name: "Sell Ore",
        method: "POST",
        description: "Sell ore from inventory for currency",
        enabled: true,
        input: {
          oreType: { type: "string", required: true },
          amount: { type: "number", required: true },
        },
        steps: [
          { type: "read", collection: "players", as: "player" },
          { type: "lookup", source: "values", table: "ore_types", key: "id", value: "{{input.oreType}}", as: "ore" },
          { type: "condition", field: "player.ores.{{input.oreType}}", operator: "gte", value: "{{input.amount}}", onFail: "error", errorCode: "NOT_ENOUGH_ORE", errorMessage: "Not enough ore to sell" },
          { type: "transform", field: "player.ores.{{input.oreType}}", operation: "subtract", value: "{{input.amount}}" },
          { type: "transform", field: "player.currentOreKg", operation: "subtract", value: "{{input.amount}}" },
          { type: "transform", field: "player.currency", operation: "add", value: "{{input.amount}}" },
          { type: "write", collection: "players" },
        ],
        response: { status: 200, body: { ok: true } },
      }, null, 2),
    },
    {
      type: "endpoint",
      name: "buy-upgrade",
      description: "Purchase an upgrade with currency check",
      json: JSON.stringify({
        slug: "buy-upgrade",
        name: "Buy Upgrade",
        method: "POST",
        description: "Purchase an upgrade using currency",
        enabled: true,
        input: {
          upgradeId: { type: "string", required: true },
          cost: { type: "number", required: true },
        },
        steps: [
          { type: "read", collection: "players", as: "player" },
          { type: "condition", field: "player.currency", operator: "gte", value: "{{input.cost}}", onFail: "error", errorCode: "NOT_ENOUGH_CURRENCY", errorMessage: "Not enough currency" },
          { type: "transform", field: "player.currency", operation: "subtract", value: "{{input.cost}}" },
          { type: "write", collection: "players" },
        ],
        response: { status: 200, body: { ok: true } },
      }, null, 2),
    },
    {
      type: "workflow",
      name: "check-currency",
      description: "Reusable currency check workflow",
      json: JSON.stringify({
        id: "check-currency",
        name: "Check Currency",
        description: "Verify player has enough currency",
        condition: {
          field: "player.currency",
          op: "gte",
          value: "{{input.cost}}",
        },
        onFail: {
          reject: true,
          errorCode: "NOT_ENOUGH_CURRENCY",
          errorMessage: "Not enough currency. Have: {{player.currency}}, need: {{input.cost}}",
        },
        steps: [
          {
            type: "condition",
            field: "player.currency",
            operator: "gte",
            value: "{{input.cost}}",
            onFail: "error",
            errorMessage: "Not enough currency",
          },
        ],
      }, null, 2),
    },
  ],
  leaderboard: [
    {
      type: "collection",
      name: "leaderboard",
      description: "Global leaderboard collection for top scores",
      json: JSON.stringify({
        name: "leaderboard",
        description: "Global leaderboard for high scores",
        collectionType: "global",
        accessMode: "public",
        maxRecords: 100,
        allowRecordDelete: false,
        schema: {
          playerName: { type: "string", default: "" },
          steamId: { type: "string", default: "" },
          score: { type: "number", default: 0 },
          submittedAt: { type: "string", default: "" },
        },
      }, null, 2),
    },
    {
      type: "endpoint",
      name: "submit-score",
      description: "Submit a score to the global leaderboard",
      json: JSON.stringify({
        slug: "submit-score",
        name: "Submit Score",
        method: "POST",
        description: "Submit a player score to the leaderboard",
        enabled: true,
        input: {
          playerName: { type: "string", required: true },
          score: { type: "number", required: true },
        },
        steps: [
          { type: "read", collection: "players", as: "player" },
          { type: "condition", field: "input.score", operator: "gt", value: 0, onFail: "error", errorMessage: "Score must be positive" },
          { type: "write", collection: "leaderboard" },
        ],
        response: { status: 200, body: { ok: true } },
      }, null, 2),
    },
  ],
  chat: [
    {
      type: "collection",
      name: "chat_messages",
      description: "Per-player chat message storage",
      json: JSON.stringify({
        name: "chat_messages",
        description: "Stores chat messages per player",
        collectionType: "per-steamid",
        accessMode: "public",
        maxRecords: 1,
        schema: {
          displayName: { type: "string", default: "" },
          messageCount: { type: "number", default: 0 },
        },
      }, null, 2),
    },
    {
      type: "endpoint",
      name: "send-message",
      description: "Send a chat message and increment counter",
      json: JSON.stringify({
        slug: "send-message",
        name: "Send Message",
        method: "POST",
        description: "Send a chat message",
        enabled: true,
        input: {
          text: { type: "string", required: true },
          displayName: { type: "string", required: true },
        },
        steps: [
          { type: "read", collection: "chat_messages", as: "chat" },
          { type: "transform", field: "chat.displayName", operation: "set", value: "{{input.displayName}}" },
          { type: "transform", field: "chat.messageCount", operation: "add", value: 1 },
          { type: "write", collection: "chat_messages" },
        ],
        response: { status: 200, body: { ok: true, messageCount: "{{chat.messageCount}}" } },
      }, null, 2),
    },
  ],
};

// ─── Error Patterns ───────────────────────────────────────────────────────────

interface ErrorPattern {
  patterns: (string | RegExp)[];
  errorCode: string;
  explanation: string;
  possibleCauses: string[];
  fixes: string[];
}

const ERROR_PATTERNS: ErrorPattern[] = [
  {
    patterns: ["UNAUTHORIZED", /401/i, /unauthorized/i],
    errorCode: "UNAUTHORIZED",
    explanation: "The API key is invalid, missing, or doesn't belong to the specified project.",
    possibleCauses: [
      "Wrong API key entered in Setup",
      "Key belongs to a different project",
      "Key was deleted or rotated on sbox.cool",
      "Missing x-api-key header in request",
    ],
    fixes: [
      "Re-check credentials in Editor → Network Storage → Setup",
      "Verify key prefixes: public = sbox_ns_, secret = sbox_sk_",
      "Regenerate keys on sbox.cool dashboard if needed",
      "Restart s&box fully (not hot-reload) after changing credentials",
    ],
  },
  {
    patterns: ["PROJECT_DISABLED", /project.*disabled/i],
    errorCode: "PROJECT_DISABLED",
    explanation: "The project exists but has been disabled on sbox.cool.",
    possibleCauses: ["Project was manually disabled", "Account issues"],
    fixes: ["Check project status on sbox.cool dashboard", "Re-enable the project"],
  },
  {
    patterns: ["QUOTA_EXCEEDED", /quota/i],
    errorCode: "QUOTA_EXCEEDED",
    explanation: "Monthly API usage limit has been exceeded.",
    possibleCauses: ["Too many API calls this month", "Unintended high traffic"],
    fixes: ["Check usage on sbox.cool dashboard", "Optimize call frequency", "Upgrade plan if available"],
  },
  {
    patterns: ["SCHEMA_VALIDATION_FAILED", /schema.*validation/i],
    errorCode: "SCHEMA_VALIDATION_FAILED",
    explanation: "The data being written doesn't match the collection's schema definition.",
    possibleCauses: [
      "Field type mismatch (e.g., sending string where number expected)",
      "Missing required fields",
      "Collection schema was updated but endpoint still sends old format",
    ],
    fixes: [
      "Compare endpoint output fields with collection schema",
      "Use validate_collection tool to check schema",
      "Push updated collection schema via Sync Tool",
    ],
  },
  {
    patterns: ["INVALID_JSON", /invalid.*json/i],
    errorCode: "INVALID_JSON",
    explanation: "The request body is not valid JSON.",
    possibleCauses: ["Malformed JSON in request", "Encoding issues", "Empty body on POST request"],
    fixes: ["Validate JSON syntax", "Ensure Content-Type is application/json", "Check that POST body is not empty"],
  },
  {
    patterns: ["INVALID_BODY", /invalid.*body/i],
    errorCode: "INVALID_BODY",
    explanation: "The request body structure doesn't match what the endpoint expects.",
    possibleCauses: ["Missing required input fields", "Extra unexpected fields", "Wrong field types"],
    fixes: ["Check endpoint's input definition", "Use validate_endpoint tool to verify", "Ensure all required fields are sent"],
  },
  {
    patterns: ["RATE_LIMIT", /rate.?limit/i],
    errorCode: "RATE_LIMIT",
    explanation: "The rate limit for this collection/endpoint has been exceeded.",
    possibleCauses: ["Too many writes in time window", "Rate limit rules too strict", "Client sending duplicate requests"],
    fixes: ["Adjust rateLimits in collection settings", "Implement client-side throttling", "Check for duplicate API calls"],
  },
  {
    patterns: ["ENDPOINT_NOT_FOUND", /endpoint.*not.*found/i],
    errorCode: "ENDPOINT_NOT_FOUND",
    explanation: "The endpoint slug doesn't exist on the server.",
    possibleCauses: [
      "Endpoint not pushed via Sync Tool",
      "Typo in endpoint slug",
      "Endpoint was deleted on sbox.cool",
    ],
    fixes: [
      "Open Sync Tool and push the endpoint",
      "Double-check the slug matches your JSON filename",
      "Verify endpoint exists on sbox.cool dashboard",
    ],
  },
  {
    patterns: ["ENDPOINT_DISABLED", /endpoint.*disabled/i],
    errorCode: "ENDPOINT_DISABLED",
    explanation: "The endpoint exists but has enabled: false.",
    possibleCauses: ["Endpoint was intentionally disabled", "Default enabled state wasn't set"],
    fixes: ["Set \"enabled\": true in the endpoint JSON file", "Push the updated endpoint via Sync Tool"],
  },
  {
    patterns: ["CONDITION_FAILED", /condition.*failed/i],
    errorCode: "CONDITION_FAILED",
    explanation: "A condition step in the endpoint pipeline failed. This is expected game logic (e.g., player tried an action they can't do).",
    possibleCauses: ["Player doesn't meet requirements", "Input values don't pass validation"],
    fixes: [
      "This is normal game behavior — handle it in client code",
      "Add custom errorCode to your condition for better identification",
      "Check the errorMessage for details",
    ],
  },
  {
    patterns: ["INTERNAL_ERROR", /internal.*error/i],
    errorCode: "INTERNAL_ERROR",
    explanation: "The server crashed while executing the endpoint. This indicates a misconfiguration.",
    possibleCauses: [
      "Endpoint references a collection that doesn't exist",
      "Step references an undefined alias",
      "Template syntax error in step values",
      "Server-side bug",
    ],
    fixes: [
      "Use validate_endpoint tool to check for issues",
      "Verify all referenced collections are pushed",
      "Check template syntax ({{input.x}}, {{alias.field}})",
      "Check server logs on sbox.cool dashboard",
    ],
  },
  {
    patterns: [/Response status code does not indicate success.*400/i, /status.*400/i],
    errorCode: "HTTP_400",
    explanation: "s&box's Http API threw an exception because the server returned HTTP 400. The response body (with error details) was lost.",
    possibleCauses: [
      "Endpoint or workflow has a non-200 status in onFail",
      "Invalid request format",
    ],
    fixes: [
      "Remove the \"status\" field from onFail or set it to 200",
      "s&box needs HTTP 200 to read the response body — use ok: false for errors instead",
    ],
  },
  {
    patterns: [/credentials.*not.*found/i, /no.*credentials/i, /network-storage\.credentials\.json/i],
    errorCode: "NO_CREDENTIALS",
    explanation: "The runtime credentials file (Assets/network-storage.credentials.json) was not found.",
    possibleCauses: [
      "Haven't run Setup yet",
      "credentials.json was deleted",
      "File is in wrong location",
    ],
    fixes: [
      "Open Editor → Network Storage → Setup and save credentials",
      "Check Assets/network-storage.credentials.json exists",
      "Restart s&box after saving",
    ],
  },
  {
    patterns: [/auth.*token.*empty/i, /empty.*auth/i, /steam.*auth/i],
    errorCode: "EMPTY_AUTH_TOKEN",
    explanation: "The Steam authentication token is empty. This usually means you're not in Play mode.",
    possibleCauses: [
      "Editor is not in Play mode",
      "Steam is not running",
      "Steam auth not available in current context",
    ],
    fixes: [
      "Press Play in the s&box editor",
      "Ensure Steam is running and you're logged in",
      "Steam tokens are only available during active Play sessions",
    ],
  },
  {
    patterns: [/not.*configured/i, /NetworkStorage.*not.*configured/i],
    errorCode: "NOT_CONFIGURED",
    explanation: "NetworkStorage hasn't been configured. Auto-config didn't find credentials and Configure() wasn't called.",
    possibleCauses: [
      "No credentials.json file",
      "credentials.json is incomplete",
      "Neither AutoConfigure() nor Configure() succeeded",
    ],
    fixes: [
      "Set up credentials via Editor → Network Storage → Setup",
      "Or call NetworkStorage.Configure(projectId, apiKey) manually",
      "Restart s&box fully after changing credentials",
    ],
  },
  {
    patterns: [/undefined.*read/i, /undefined read on/i],
    errorCode: "MISSING_COLLECTION",
    explanation: "An endpoint references a collection that doesn't exist on the server.",
    possibleCauses: [
      "Collection not pushed via Sync Tool",
      "Typo in collection name in endpoint step",
      "Collection was deleted from server",
    ],
    fixes: [
      "Push the collection via Sync Tool BEFORE pushing the endpoint",
      "Verify collection name matches exactly (case-sensitive, lowercase + underscores)",
      "Always push collections first, then endpoints",
    ],
  },
  {
    patterns: [/\{\{.*\}\}/i, /template.*text/i, /unresolved.*template/i],
    errorCode: "UNRESOLVED_TEMPLATE",
    explanation: "Template variables ({{...}}) weren't resolved by the server, appearing as raw text in the response.",
    possibleCauses: [
      "Template references a field that doesn't exist in the context",
      "Typo in template variable name",
      "Old server version that doesn't support template resolution in errorMessage",
    ],
    fixes: [
      "Verify template variable names match step aliases (e.g., {{player.x}} requires a read step with as: \"player\")",
      "Check for typos in the template path",
      "Ensure the server is running the latest version",
    ],
  },
];

// ─── Validation Helpers ───────────────────────────────────────────────────────

function tryParseJson(jsonStr: string): { ok: true; data: any } | { ok: false; error: string } {
  try {
    return { ok: true, data: JSON.parse(jsonStr) };
  } catch (e: any) {
    return { ok: false, error: `Invalid JSON: ${e.message}` };
  }
}

function validateTemplates(value: any, path: string, warnings: string[]): void {
  if (typeof value === "string") {
    const matches = value.matchAll(TEMPLATE_PATTERN);
    for (const match of matches) {
      const inner = match[1].trim();
      if (!inner) {
        warnings.push(`${path}: empty template \`{{}}\``);
      } else if (inner.startsWith("-")) {
        // Negation — check the rest
        const rest = inner.slice(1);
        if (!rest.includes(".") && rest !== "") {
          warnings.push(`${path}: negation template \`{{${inner}}}\` — did you mean \`{{-source.field}}\`?`);
        }
      } else if (!inner.includes(".")) {
        warnings.push(`${path}: template \`{{${inner}}}\` has no dot-path — expected format like \`{{input.fieldName}}\` or \`{{alias.field}}\``);
      }
    }
    // Check for single-brace mistakes
    if (/(?<!\{)\{(?!\{)[^}]+\}(?!\})/.test(value)) {
      warnings.push(`${path}: possible single-brace template detected — use double braces \`{{...}}\``);
    }
  } else if (typeof value === "object" && value !== null) {
    for (const [k, v] of Object.entries(value)) {
      validateTemplates(v, `${path}.${k}`, warnings);
    }
  }
}

function validateCollectionJson(data: any): { valid: boolean; errors: string[]; warnings: string[] } {
  const errors: string[] = [];
  const warnings: string[] = [];

  // Required: name
  if (!data.name) {
    errors.push("Missing required field: `name`");
  } else if (typeof data.name !== "string") {
    errors.push("`name` must be a string");
  } else if (!COLLECTION_NAME_PATTERN.test(data.name)) {
    errors.push(`\`name\` must match /^[a-z0-9_]+$/ — got "${data.name}". Use lowercase letters, digits, and underscores only.`);
  }

  // Required: schema
  if (!data.schema) {
    errors.push("Missing required field: `schema`");
  } else if (typeof data.schema !== "object" || Array.isArray(data.schema)) {
    errors.push("`schema` must be an object");
  } else {
    if (Object.keys(data.schema).length === 0) {
      warnings.push("`schema` is empty — consider adding field definitions");
    }
    for (const [fieldName, fieldDef] of Object.entries(data.schema)) {
      if (typeof fieldDef !== "object" || fieldDef === null) {
        errors.push(`schema.${fieldName}: must be an object with at least a \`type\` property`);
        continue;
      }
      const fd = fieldDef as any;
      if (!fd.type) {
        errors.push(`schema.${fieldName}: missing \`type\` property`);
      } else if (!VALID_FIELD_TYPES.includes(fd.type)) {
        errors.push(`schema.${fieldName}.type: must be one of: ${VALID_FIELD_TYPES.join(", ")} — got "${fd.type}"`);
      }
    }
  }

  // Optional enums
  if (data.collectionType && !VALID_COLLECTION_TYPES.includes(data.collectionType)) {
    errors.push(`\`collectionType\` must be one of: ${VALID_COLLECTION_TYPES.join(", ")} — got "${data.collectionType}"`);
  }
  if (data.accessMode && !VALID_ACCESS_MODES.includes(data.accessMode)) {
    errors.push(`\`accessMode\` must be one of: ${VALID_ACCESS_MODES.join(", ")} — got "${data.accessMode}"`);
  }

  // Type checks
  if (data.maxRecords !== undefined && (typeof data.maxRecords !== "number" || data.maxRecords < 1)) {
    errors.push("`maxRecords` must be a positive number");
  }
  if (data.allowRecordDelete !== undefined && typeof data.allowRecordDelete !== "boolean") {
    errors.push("`allowRecordDelete` must be a boolean");
  }
  if (data.requireSaveVersion !== undefined && typeof data.requireSaveVersion !== "boolean") {
    errors.push("`requireSaveVersion` must be a boolean");
  }
  if (data.webhookOnRateLimit !== undefined && typeof data.webhookOnRateLimit !== "boolean") {
    errors.push("`webhookOnRateLimit` must be a boolean");
  }

  // Constants validation
  if (data.constants) {
    if (!Array.isArray(data.constants)) {
      errors.push("`constants` must be an array");
    } else {
      data.constants.forEach((c: any, i: number) => {
        if (!c.group || typeof c.group !== "string") {
          errors.push(`constants[${i}]: missing or invalid \`group\` (must be a string)`);
        }
        if (!c.values || typeof c.values !== "object") {
          errors.push(`constants[${i}]: missing or invalid \`values\` (must be an object)`);
        }
      });
    }
  }

  // Tables validation
  if (data.tables) {
    if (!Array.isArray(data.tables)) {
      errors.push("`tables` must be an array");
    } else {
      data.tables.forEach((t: any, i: number) => {
        if (!t.name || typeof t.name !== "string") {
          errors.push(`tables[${i}]: missing or invalid \`name\``);
        }
        if (!Array.isArray(t.columns)) {
          errors.push(`tables[${i}]: \`columns\` must be an array of strings`);
        }
        if (!Array.isArray(t.rows)) {
          errors.push(`tables[${i}]: \`rows\` must be an array of arrays`);
        } else if (Array.isArray(t.columns)) {
          t.rows.forEach((row: any, ri: number) => {
            if (!Array.isArray(row)) {
              errors.push(`tables[${i}].rows[${ri}]: must be an array`);
            } else if (row.length !== t.columns.length) {
              errors.push(`tables[${i}].rows[${ri}]: has ${row.length} values but ${t.columns.length} columns defined`);
            }
          });
        }
      });
    }
  }

  // Warnings
  if (data.collectionType === "per-steamid" && data.maxRecords && data.maxRecords > 10) {
    warnings.push(`maxRecords is ${data.maxRecords} for a per-steamid collection — this is unusually high`);
  }

  // Unknown fields
  const knownFields = ["name", "description", "collectionType", "accessMode", "maxRecords",
    "allowRecordDelete", "requireSaveVersion", "rateLimits", "rateLimitAction",
    "webhookOnRateLimit", "schema", "constants", "tables", "id", "createdAt", "version"];
  for (const key of Object.keys(data)) {
    if (!knownFields.includes(key)) {
      warnings.push(`Unknown field: \`${key}\` — this will be ignored by the server`);
    }
  }

  return { valid: errors.length === 0, errors, warnings };
}

function validateEndpointJson(data: any, knownCollections?: string[], knownWorkflows?: string[]): { valid: boolean; errors: string[]; warnings: string[] } {
  const errors: string[] = [];
  const warnings: string[] = [];

  // Required: slug
  if (!data.slug) {
    errors.push("Missing required field: `slug`");
  } else if (typeof data.slug !== "string") {
    errors.push("`slug` must be a string");
  } else if (!ENDPOINT_SLUG_PATTERN.test(data.slug)) {
    errors.push(`\`slug\` must match /^[a-z0-9-]+$/ — got "${data.slug}". Use lowercase letters, digits, and hyphens only.`);
  }

  // Method
  if (data.method && !VALID_METHODS.includes(data.method)) {
    errors.push(`\`method\` must be one of: ${VALID_METHODS.join(", ")} — got "${data.method}"`);
  }

  // Enabled
  if (data.enabled !== undefined && typeof data.enabled !== "boolean") {
    errors.push("`enabled` must be a boolean");
  }

  // Input validation
  if (data.input && typeof data.input === "object") {
    for (const [fieldName, fieldDef] of Object.entries(data.input)) {
      if (typeof fieldDef === "object" && fieldDef !== null) {
        const fd = fieldDef as any;
        if (fd.type && !VALID_FIELD_TYPES.includes(fd.type)) {
          errors.push(`input.${fieldName}.type: must be one of: ${VALID_FIELD_TYPES.join(", ")} — got "${fd.type}"`);
        }
      }
    }
  }

  // Required: steps
  if (!data.steps) {
    errors.push("Missing required field: `steps`");
  } else if (!Array.isArray(data.steps)) {
    errors.push("`steps` must be an array");
  } else {
    if (data.steps.length === 0) {
      errors.push("`steps` array is empty — endpoints need at least one step");
    }
    if (data.steps.length > MAX_STEPS_PER_ENDPOINT) {
      errors.push(`Too many steps: ${data.steps.length} (max ${MAX_STEPS_PER_ENDPOINT})`);
    }

    // Count data-fetching steps
    const dataSteps = data.steps.filter((s: any) => ["read", "lookup", "filter"].includes(s.type));
    if (dataSteps.length > MAX_READ_LOOKUP_FILTER_STEPS) {
      errors.push(`Too many read/lookup/filter steps: ${dataSteps.length} (max ${MAX_READ_LOOKUP_FILTER_STEPS})`);
    }

    // Track defined aliases
    const definedAliases = new Set<string>();
    const readCollections = new Set<string>();

    data.steps.forEach((step: any, i: number) => {
      const prefix = `steps[${i}]`;

      if (!step.type) {
        errors.push(`${prefix}: missing \`type\` field`);
        return;
      }

      if (!VALID_STEP_TYPES.includes(step.type)) {
        errors.push(`${prefix}.type: must be one of: ${VALID_STEP_TYPES.join(", ")} — got "${step.type}"`);
        return;
      }

      // Check required fields for this step type
      const required = STEP_REQUIRED_FIELDS[step.type] || [];
      for (const field of required) {
        if (step[field] === undefined && step[field] !== 0 && step[field] !== false && step[field] !== "") {
          // Special case: condition with exists/not_exists doesn't need value
          if (field === "value" && step.type === "condition" && ["exists", "not_exists"].includes(step.operator)) {
            continue;
          }
          errors.push(`${prefix}: missing required field \`${field}\` for step type "${step.type}"`);
        }
      }

      // Type-specific validation
      if (step.type === "read" || step.type === "lookup" || step.type === "filter") {
        if (step.as) definedAliases.add(step.as);
      }

      if (step.type === "read") {
        if (step.collection) readCollections.add(step.collection);
        if (step.collection && knownCollections && !knownCollections.includes(step.collection)) {
          warnings.push(`${prefix}: collection "${step.collection}" not in known collections — make sure it's pushed to the server`);
        }
      }

      if (step.type === "write") {
        if (step.collection && !readCollections.has(step.collection)) {
          warnings.push(`${prefix}: writing to "${step.collection}" which was never read in a prior step — the write may fail`);
        }
      }

      if (step.type === "transform") {
        if (step.operation && !VALID_OPERATIONS.includes(step.operation)) {
          errors.push(`${prefix}.operation: must be one of: ${VALID_OPERATIONS.join(", ")} — got "${step.operation}"`);
        }
      }

      if (step.type === "condition") {
        if (step.operator && !VALID_OPERATORS.includes(step.operator)) {
          errors.push(`${prefix}.operator: must be one of: ${VALID_OPERATORS.join(", ")} — got "${step.operator}"`);
        }
        if (step.onFail && !VALID_ONFAIL.includes(step.onFail)) {
          errors.push(`${prefix}.onFail: must be one of: ${VALID_ONFAIL.join(", ")} — got "${step.onFail}"`);
        }
      }

      if (step.type === "workflow") {
        if (step.workflow && knownWorkflows && !knownWorkflows.includes(step.workflow)) {
          warnings.push(`${prefix}: workflow "${step.workflow}" not in known workflows — make sure it's pushed to the server`);
        }
      }

      // Template validation for all string values in this step
      validateTemplates(step, prefix, warnings);
    });
  }

  // Response validation
  if (data.response) {
    if (data.response.status && typeof data.response.status !== "number") {
      errors.push("`response.status` must be a number");
    }
    if (data.response.body) {
      validateTemplates(data.response.body, "response.body", warnings);
    }
  }

  // Method + input warning
  if (data.method === "GET" && data.input && Object.keys(data.input).length > 0) {
    warnings.push("GET endpoints typically don't have input fields — consider using POST if you need to send data");
  }

  return { valid: errors.length === 0, errors, warnings };
}

function validateWorkflowJson(data: any): { valid: boolean; errors: string[]; warnings: string[] } {
  const errors: string[] = [];
  const warnings: string[] = [];

  // Required: id
  if (!data.id) {
    errors.push("Missing required field: `id`");
  } else if (typeof data.id !== "string") {
    errors.push("`id` must be a string");
  } else if (!WORKFLOW_ID_PATTERN.test(data.id)) {
    errors.push(`\`id\` must match /^[a-z0-9-]+$/ — got "${data.id}". Use lowercase letters, digits, and hyphens only.`);
  }

  // Condition validation
  if (data.condition) {
    if (typeof data.condition === "object" && !Array.isArray(data.condition)) {
      if (data.condition.all || data.condition.any) {
        // Compound condition — validate each sub-condition
        const subs = data.condition.all || data.condition.any;
        if (!Array.isArray(subs)) {
          errors.push("`condition.all` / `condition.any` must be an array");
        }
      } else {
        // Simple condition
        if (!data.condition.field) errors.push("`condition.field` is required");
        if (!data.condition.op) errors.push("`condition.op` is required");
        if (data.condition.op && !VALID_OPERATORS.includes(data.condition.op)) {
          errors.push(`\`condition.op\` must be one of: ${VALID_OPERATORS.join(", ")} — got "${data.condition.op}"`);
        }
      }
      validateTemplates(data.condition, "condition", warnings);
    }
  }

  // onFail validation
  if (data.onFail && typeof data.onFail === "object") {
    const of = data.onFail;
    if (of.severity && !["warning", "critical"].includes(of.severity)) {
      errors.push(`onFail.severity: must be "warning" or "critical" — got "${of.severity}"`);
    }
    if (of.status && of.status !== 200) {
      warnings.push(`onFail.status is ${of.status} — non-200 status codes will cause s&box's Http API to throw and lose the error response body. Use 200 with ok: false instead.`);
    }
    if (of.errorMessage) {
      validateTemplates(of.errorMessage, "onFail.errorMessage", warnings);
    }
  }

  // Steps validation (reuse endpoint step validation)
  if (data.steps && Array.isArray(data.steps)) {
    data.steps.forEach((step: any, i: number) => {
      const prefix = `steps[${i}]`;
      if (!step.type) {
        errors.push(`${prefix}: missing \`type\` field`);
        return;
      }
      if (!VALID_STEP_TYPES.includes(step.type)) {
        errors.push(`${prefix}.type: must be one of: ${VALID_STEP_TYPES.join(", ")} — got "${step.type}"`);
        return;
      }
      const required = STEP_REQUIRED_FIELDS[step.type] || [];
      for (const field of required) {
        if (step[field] === undefined && step[field] !== 0 && step[field] !== false && step[field] !== "") {
          if (field === "value" && step.type === "condition" && ["exists", "not_exists"].includes(step.operator)) continue;
          errors.push(`${prefix}: missing required field \`${field}\` for step type "${step.type}"`);
        }
      }
      if (step.type === "condition") {
        if (step.operator && !VALID_OPERATORS.includes(step.operator)) {
          errors.push(`${prefix}.operator: must be one of: ${VALID_OPERATORS.join(", ")} — got "${step.operator}"`);
        }
        if (step.onFail && !VALID_ONFAIL.includes(step.onFail)) {
          errors.push(`${prefix}.onFail: must be one of: ${VALID_ONFAIL.join(", ")} — got "${step.onFail}"`);
        }
      }
      if (step.type === "transform" && step.operation && !VALID_OPERATIONS.includes(step.operation)) {
        errors.push(`${prefix}.operation: must be one of: ${VALID_OPERATIONS.join(", ")} — got "${step.operation}"`);
      }
      validateTemplates(step, prefix, warnings);
    });
  }

  return { valid: errors.length === 0, errors, warnings };
}

function validateEnvContent(content: string): { valid: boolean; errors: string[]; warnings: string[]; parsed: Record<string, string> } {
  const errors: string[] = [];
  const warnings: string[] = [];
  const parsed: Record<string, string> = {};

  const lines = content.split("\n");
  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) continue;

    const eqIdx = trimmed.indexOf("=");
    if (eqIdx === -1) {
      warnings.push(`Invalid line (no = sign): "${trimmed}"`);
      continue;
    }

    const key = trimmed.slice(0, eqIdx).trim();
    const value = trimmed.slice(eqIdx + 1).trim();
    parsed[key] = value;
  }

  // Required keys
  for (const key of REQUIRED_ENV_KEYS) {
    if (!parsed[key]) {
      errors.push(`Missing required key: ${key}`);
    }
  }

  // Prefix checks
  if (parsed.SBOXCOOL_PUBLIC_KEY && !parsed.SBOXCOOL_PUBLIC_KEY.startsWith("sbox_ns_")) {
    errors.push(`SBOXCOOL_PUBLIC_KEY must start with "sbox_ns_" — got "${parsed.SBOXCOOL_PUBLIC_KEY.slice(0, 20)}..."`);
  }
  if (parsed.SBOXCOOL_SECRET_KEY && !parsed.SBOXCOOL_SECRET_KEY.startsWith("sbox_sk_")) {
    errors.push(`SBOXCOOL_SECRET_KEY must start with "sbox_sk_" — got "${parsed.SBOXCOOL_SECRET_KEY.slice(0, 20)}..."`);
  }

  // Placeholder detection
  const placeholders = ["your-project-id", "your_project_id", "your_public_key", "your_secret_key",
    "sbox_ns_your_public_key", "sbox_sk_your_secret_key", "sbox_ns_your", "sbox_sk_your"];
  for (const [key, value] of Object.entries(parsed)) {
    if (placeholders.some(p => value.includes(p))) {
      warnings.push(`${key} appears to contain a placeholder value — replace with your actual key`);
    }
  }

  // Optional key validation
  if (parsed.SBOXCOOL_BASE_URL) {
    try {
      new URL(parsed.SBOXCOOL_BASE_URL);
    } catch {
      errors.push(`SBOXCOOL_BASE_URL is not a valid URL: "${parsed.SBOXCOOL_BASE_URL}"`);
    }
  }
  if (parsed.SBOXCOOL_API_VERSION && !/^v\d+$/.test(parsed.SBOXCOOL_API_VERSION)) {
    warnings.push(`SBOXCOOL_API_VERSION expected format like "v3" — got "${parsed.SBOXCOOL_API_VERSION}"`);
  }
  if (parsed.SBOXCOOL_DATA_SOURCE && !VALID_DATA_SOURCES.includes(parsed.SBOXCOOL_DATA_SOURCE as any)) {
    errors.push(`SBOXCOOL_DATA_SOURCE must be one of: ${VALID_DATA_SOURCES.join(", ")} — got "${parsed.SBOXCOOL_DATA_SOURCE}"`);
  }

  // Unknown keys
  const allKnown = [...REQUIRED_ENV_KEYS, ...OPTIONAL_ENV_KEYS];
  for (const key of Object.keys(parsed)) {
    if (!allKnown.includes(key as any)) {
      warnings.push(`Unknown environment variable: ${key}`);
    }
  }

  // Security warning
  if (parsed.SBOXCOOL_SECRET_KEY && parsed.SBOXCOOL_SECRET_KEY.startsWith("sbox_sk_") && parsed.SBOXCOOL_SECRET_KEY.length > 15) {
    warnings.push("This appears to contain a real secret key — make sure the .env file is gitignored and never committed");
  }

  return { valid: errors.length === 0, errors, warnings, parsed };
}

function diagnoseError(error: string, context: string): {
  errorCode: string;
  explanation: string;
  possibleCauses: string[];
  fixes: string[];
  consoleMessage: string;
} {
  // Normalize input
  const normalized = error.replace(/\[NetworkStorage\]\s*/g, "").replace(/^\d{4}-\d{2}-\d{2}.*?:\s*/g, "").trim();

  // Try each pattern
  for (const pattern of ERROR_PATTERNS) {
    for (const p of pattern.patterns) {
      const matches = typeof p === "string"
        ? normalized.includes(p) || normalized.toUpperCase().includes(p)
        : p.test(normalized);

      if (matches) {
        return {
          errorCode: pattern.errorCode,
          explanation: pattern.explanation,
          possibleCauses: pattern.possibleCauses,
          fixes: pattern.fixes,
          consoleMessage: `[Network Storage MCP] Error identified: ${pattern.errorCode}\n${pattern.explanation}\n\nPossible fixes:\n${pattern.fixes.map(f => `  - ${f}`).join("\n")}`,
        };
      }
    }
  }

  // No match — ask user for more info
  return {
    errorCode: "UNKNOWN",
    explanation: "Could not automatically identify this error.",
    possibleCauses: ["The error message doesn't match any known pattern"],
    fixes: [
      "Check the s&box console for [NetworkStorage] prefixed lines",
      "Look for the full error response JSON in the console output",
      "Try the validate_endpoint or validate_collection tools to check your JSON files",
      "Check the sbox.cool dashboard for server-side error logs",
    ],
    consoleMessage: `[Network Storage MCP] I couldn't identify this error automatically.\n\nPlease copy the FULL error output from your s&box console, including:\n  - Any lines starting with [NetworkStorage]\n  - The full JSON response if visible\n  - The HTTP status code if shown\n\nPaste the complete output here so I can help diagnose the issue.`,
  };
}

// ─── Read doc files helper ────────────────────────────────────────────────────

function readDocFile(filename: string): string {
  try {
    return readFileSync(resolve(REPO_ROOT, "docs", filename), "utf-8");
  } catch {
    return `Documentation file not found: docs/${filename}`;
  }
}

// ─── Server Setup ─────────────────────────────────────────────────────────────

const server = new McpServer({
  name: "sbox-network-storage",
  version: "1.0.0",
});

// ─── Tools ────────────────────────────────────────────────────────────────────

server.tool(
  "get_documentation",
  "Get documentation about Network Storage — collections, endpoints, workflows, setup, error handling, etc.",
  {
    topic: z.enum([
      "overview", "collections", "endpoints", "workflows", "steps", "templates",
      "env-config", "setup", "runtime-client", "error-handling", "sync-tool",
      "constraints", "security", "all",
    ]).describe("The documentation topic to retrieve"),
  },
  async ({ topic }) => {
    if (topic === "all") {
      const allDocs = Object.entries(DOCUMENTATION)
        .map(([, content]) => content)
        .join("\n\n---\n\n");
      return { content: [{ type: "text" as const, text: allDocs }] };
    }
    const doc = DOCUMENTATION[topic];
    if (!doc) {
      return { content: [{ type: "text" as const, text: `Unknown topic: ${topic}` }] };
    }
    return { content: [{ type: "text" as const, text: doc }] };
  }
);

server.tool(
  "validate_collection",
  "Validate a collection JSON file for correct syntax and structure",
  {
    json: z.string().describe("The collection JSON content to validate"),
  },
  async ({ json }) => {
    const parsed = tryParseJson(json);
    if (!parsed.ok) {
      return {
        content: [{ type: "text" as const, text: JSON.stringify({ valid: false, errors: [parsed.error], warnings: [] }, null, 2) }],
      };
    }
    const result = validateCollectionJson(parsed.data);
    return { content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }] };
  }
);

server.tool(
  "validate_endpoint",
  "Validate an endpoint JSON file for correct syntax, step types, template syntax, and constraints",
  {
    json: z.string().describe("The endpoint JSON content to validate"),
    collections: z.array(z.string()).optional().describe("Known collection names for cross-reference validation"),
    workflows: z.array(z.string()).optional().describe("Known workflow IDs for cross-reference validation"),
  },
  async ({ json, collections, workflows }) => {
    const parsed = tryParseJson(json);
    if (!parsed.ok) {
      return {
        content: [{ type: "text" as const, text: JSON.stringify({ valid: false, errors: [parsed.error], warnings: [] }, null, 2) }],
      };
    }
    const result = validateEndpointJson(parsed.data, collections, workflows);
    return { content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }] };
  }
);

server.tool(
  "validate_workflow",
  "Validate a workflow JSON file for correct syntax and structure",
  {
    json: z.string().describe("The workflow JSON content to validate"),
  },
  async ({ json }) => {
    const parsed = tryParseJson(json);
    if (!parsed.ok) {
      return {
        content: [{ type: "text" as const, text: JSON.stringify({ valid: false, errors: [parsed.error], warnings: [] }, null, 2) }],
      };
    }
    const result = validateWorkflowJson(parsed.data);
    return { content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }] };
  }
);

server.tool(
  "scaffold_collection",
  "Generate a collection JSON template with the correct structure and defaults",
  {
    name: z.string().describe("Collection name (lowercase alphanumeric + underscores)"),
    collectionType: z.enum(["per-steamid", "global"]).describe("per-steamid for player data, global for shared data"),
    description: z.string().optional().describe("Human-readable description"),
    fields: z.array(z.object({
      name: z.string(),
      type: z.enum(["string", "number", "boolean", "object", "array"]),
      default: z.any().optional(),
    })).describe("Schema fields to include"),
    accessMode: z.enum(["public", "private"]).optional().default("public"),
    constants: z.array(z.object({
      group: z.string(),
      values: z.record(z.any()),
    })).optional().describe("Optional constant groups"),
    tables: z.array(z.object({
      name: z.string(),
      columns: z.array(z.string()),
      rows: z.array(z.array(z.any())),
    })).optional().describe("Optional data tables"),
  },
  async ({ name, collectionType, description, fields, accessMode, constants, tables }) => {
    const schema: Record<string, any> = {};
    for (const field of fields) {
      const def: any = { type: field.type };
      if (field.default !== undefined) {
        def.default = field.default;
      } else {
        // Provide sensible defaults
        const defaults: Record<string, any> = { string: "", number: 0, boolean: false, object: {}, array: [] };
        def.default = defaults[field.type];
      }
      schema[field.name] = def;
    }

    const collection: any = {
      name,
      description: description || "",
      collectionType,
      accessMode: accessMode || "public",
      maxRecords: 1,
      allowRecordDelete: false,
      requireSaveVersion: false,
      rateLimits: { mode: "none" },
      rateLimitAction: "reject",
      webhookOnRateLimit: false,
      schema,
    };

    if (constants && constants.length > 0) collection.constants = constants;
    if (tables && tables.length > 0) collection.tables = tables;

    const jsonStr = JSON.stringify(collection, null, 2);
    const filePath = `Editor/Network Storage/collections/${name}.json`;

    return {
      content: [{ type: "text" as const, text: JSON.stringify({ json: jsonStr, filePath }, null, 2) }],
    };
  }
);

server.tool(
  "scaffold_endpoint",
  "Generate an endpoint JSON template with correct structure and step defaults",
  {
    slug: z.string().describe("Endpoint slug (lowercase alphanumeric + hyphens)"),
    name: z.string().optional().describe("Human-readable name"),
    method: z.enum(["GET", "POST"]).optional().default("POST"),
    description: z.string().optional(),
    input: z.record(z.object({
      type: z.enum(["string", "number", "boolean", "object", "array"]),
      required: z.boolean().optional(),
    })).optional().describe("Input field definitions"),
    steps: z.array(z.record(z.any())).describe("Pipeline steps — provide at minimum the type; defaults will be filled in"),
  },
  async ({ slug, name, method, description, input, steps }) => {
    const displayName = name || slug.replace(/-/g, " ").replace(/\b\w/g, c => c.toUpperCase());

    const processedSteps = steps.map((step) => {
      const processed = { ...step };
      // Fill in defaults based on type
      if (step.type === "read") {
        if (!processed.collection) processed.collection = "players";
        if (!processed.as) processed.as = processed.collection;
      } else if (step.type === "write") {
        if (!processed.collection) processed.collection = "players";
      }
      return processed;
    });

    const endpoint: any = {
      slug,
      name: displayName,
      method: method || "POST",
      description: description || "",
      enabled: true,
      input: input || {},
      steps: processedSteps,
      response: { status: 200, body: { ok: true } },
    };

    const jsonStr = JSON.stringify(endpoint, null, 2);
    const filePath = `Editor/Network Storage/endpoints/${slug}.json`;

    return {
      content: [{ type: "text" as const, text: JSON.stringify({ json: jsonStr, filePath }, null, 2) }],
    };
  }
);

server.tool(
  "scaffold_workflow",
  "Generate a workflow JSON template with correct structure",
  {
    id: z.string().describe("Workflow ID (lowercase alphanumeric + hyphens)"),
    name: z.string().optional().describe("Human-readable name"),
    description: z.string().optional(),
    conditionField: z.string().optional().describe("Field to check (e.g. 'player.currency')"),
    conditionOperator: z.enum(["eq", "neq", "gt", "gte", "lt", "lte", "contains", "exists", "not_exists"]).optional(),
    conditionValue: z.any().optional().describe("Value to compare against (supports {{template}} syntax)"),
    errorCode: z.string().optional().describe("Custom error code (e.g. 'NOT_ENOUGH_CURRENCY')"),
    errorMessage: z.string().optional().describe("Human-readable error message"),
  },
  async ({ id, name, description, conditionField, conditionOperator, conditionValue, errorCode, errorMessage }) => {
    const displayName = name || id.replace(/-/g, " ").replace(/\b\w/g, c => c.toUpperCase());

    const workflow: any = {
      id,
      name: displayName,
      description: description || "",
    };

    if (conditionField && conditionOperator) {
      workflow.condition = {
        field: conditionField,
        op: conditionOperator,
        ...(conditionValue !== undefined ? { value: conditionValue } : {}),
      };
    }

    workflow.onFail = {
      reject: true,
      errorCode: errorCode || "CONDITION_FAILED",
      errorMessage: errorMessage || "Condition check failed",
    };

    // Also generate steps array for compatibility
    if (conditionField && conditionOperator) {
      workflow.steps = [
        {
          type: "condition",
          field: conditionField,
          operator: conditionOperator,
          ...(conditionValue !== undefined ? { value: conditionValue } : {}),
          onFail: "error",
          ...(errorMessage ? { errorMessage } : {}),
        },
      ];
    }

    const jsonStr = JSON.stringify(workflow, null, 2);
    const filePath = `Editor/Network Storage/workflows/${id}.json`;

    return {
      content: [{ type: "text" as const, text: JSON.stringify({ json: jsonStr, filePath }, null, 2) }],
    };
  }
);

server.tool(
  "get_examples",
  "Get example JSON files for collections, endpoints, and workflows for common game scenarios",
  {
    type: z.enum(["collection", "endpoint", "workflow", "all"]).optional().default("all")
      .describe("Which type of example to return"),
    scenario: z.enum(["player-data", "inventory", "currency", "leaderboard", "chat", "all"]).optional().default("all")
      .describe("Specific game scenario to get examples for"),
  },
  async ({ type, scenario }) => {
    let examples: Example[] = [];

    const scenarios = scenario === "all"
      ? Object.keys(EXAMPLES)
      : [scenario || "all"];

    for (const s of scenarios) {
      const scenarioExamples = EXAMPLES[s];
      if (!scenarioExamples) continue;

      if (type === "all") {
        examples.push(...scenarioExamples);
      } else {
        examples.push(...scenarioExamples.filter(e => e.type === type));
      }
    }

    return {
      content: [{ type: "text" as const, text: JSON.stringify({ examples }, null, 2) }],
    };
  }
);

server.tool(
  "validate_env_config",
  "Validate a .env configuration file for correct keys, prefixes, and format",
  {
    content: z.string().describe("The .env file content to validate"),
  },
  async ({ content }) => {
    const result = validateEnvContent(content);
    return { content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }] };
  }
);

server.tool(
  "diagnose_error",
  "Diagnose an error message from the s&box console and suggest fixes. If the error can't be identified, provides a message asking the user to share the full console output.",
  {
    error: z.string().describe("The error message or code from the console output"),
    context: z.enum(["runtime", "sync-tool", "setup", "unknown"]).optional().default("unknown")
      .describe("Where the error occurred"),
  },
  async ({ error, context }) => {
    const result = diagnoseError(error, context || "unknown");
    return { content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }] };
  }
);

// ─── Resources ────────────────────────────────────────────────────────────────

const DOC_RESOURCES: { name: string; uri: string; file: string; description: string }[] = [
  { name: "Architecture Overview", uri: "docs://overview", file: "architecture.md", description: "System architecture and data flow" },
  { name: "Setup Guide", uri: "docs://setup", file: "setup-guide.md", description: "Installation and credential configuration" },
  { name: "Getting Started", uri: "docs://getting-started", file: "getting-started.md", description: "Complete setup walkthrough" },
  { name: "Runtime Client API", uri: "docs://runtime-client", file: "runtime-client.md", description: "Game code API reference" },
  { name: "File Reference", uri: "docs://file-reference", file: "file-reference.md", description: "JSON file format specifications" },
  { name: "Error Handling", uri: "docs://error-handling", file: "error-handling.md", description: "Error codes and rejection flow" },
  { name: "Sync Tool", uri: "docs://sync-tool", file: "sync-tool.md", description: "Editor sync tool usage" },
  { name: "Agent Instructions", uri: "docs://agent-instructions", file: "agent-instructions.md", description: "Guidelines for AI coding agents" },
];

for (const doc of DOC_RESOURCES) {
  server.resource(
    doc.name,
    doc.uri,
    { description: doc.description, mimeType: "text/markdown" },
    async () => ({
      contents: [{
        uri: doc.uri,
        mimeType: "text/markdown" as const,
        text: readDocFile(doc.file),
      }],
    })
  );
}

// ─── Start ────────────────────────────────────────────────────────────────────

const transport = new StdioServerTransport();
await server.connect(transport);
