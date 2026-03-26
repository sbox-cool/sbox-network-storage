# Network Storage — Instructions for AI Agents

This document provides instructions for AI coding agents (Claude, Copilot, etc.) working on projects that use the Network Storage library.

## Critical Rules

### 1. NEVER Modify the Library In-Place
The folder `Libraries/sboxcool.network-storage/` is **managed by s&box**. Local edits will be overwritten on next sync. If you need to change the library:

1. Make changes in the library's own git repo (typically at `C:\Users\user\Documents\repos\sbox-network-storage`)
2. Copy the changed files to the local `Libraries/sboxcool.network-storage/` for testing
3. Once confirmed working, commit and push in the library repo
4. Publish via s&box Library Manager

### 2. NEVER Commit or Display the Secret Key
- The `.env` file in `Editor/Network Storage/config/` is gitignored
- Secret keys use prefix `sbox_sk_` — never log, display, or commit them
- Public keys (`sbox_ns_`) are safe to reference in code

### 3. Backend-First Design
All player data validation, rewards, inventory checks, and game logic **MUST** be implemented server-side via endpoints — never as client-side-only checks.

When a feature touches player data:
1. Design the endpoint (JSON file) first
2. Add conditions for validation (e.g., "has enough currency")
3. Then write the client code that calls it
4. Client handles server rejection gracefully

### 4. NEVER Add as Git Submodule
Always use the published s&box library. Never add the Network Storage repo as a git submodule to a game project.

### 5. Contributing Changes Back to the Library

If you need to modify the Network Storage library (bug fix, new feature, improvement), **do not** just edit the local copy and move on. The goal is to keep the main repo at [github.com/sbox-cool/sbox-network-storage](https://github.com/sbox-cool/sbox-network-storage) improving over time.

**Option A — Fork and Pull Request (preferred for external contributors)**
1. Fork the repo on GitHub
2. Create a feature branch on your fork
3. Make your changes and test them locally (copy to `Libraries/sboxcool.network-storage/` to verify in s&box)
4. Open a Pull Request against the main repo
5. Describe what you changed and why — include before/after if relevant
6. Once merged, the published library will be updated

**Option B — Direct commit (for repo maintainers / authorized agents)**
1. Clone or pull the repo at `C:\Users\user\Documents\repos\sbox-network-storage`
2. Create a branch for your changes
3. Make changes in the repo AND copy to `Libraries/sboxcool.network-storage/` for testing
4. Once confirmed working in s&box, commit and push
5. Publish the updated library via s&box Library Manager

**Why this matters:** If agents only edit the local `Libraries/` copy, improvements are lost on the next s&box sync. By contributing upstream (fork + PR or direct push), every project using the library benefits from the fix.

## File Locations

### In the Game Project
```
Editor/Network Storage/           ← Game data (source of truth)
  config/.env                     ← Credentials (gitignored)
  collections/*.json              ← Collection schemas
  endpoints/*.json                ← Endpoint pipelines
  workflows/*.json                ← Reusable validation workflows
Libraries/sboxcool.network-storage/ ← Library code (s&box managed)
```

### In the Library Repo
```
Code/                             ← Runtime client (ships with game)
Editor/                           ← Editor tools (never ships)
Examples/                         ← Usage examples
UnitTests/                        ← Tests
docs/                             ← This documentation
```

## Common Tasks

### Adding a New Endpoint

1. Create `Editor/Network Storage/endpoints/{slug}.json`
2. Define the pipeline steps (read, condition, transform, write)
3. Open the Sync Tool and push the new endpoint
4. Write client code to call it:
```csharp
var result = await NetworkStorage.Post( "endpoint-slug", inputData );
```

### Adding a New Collection

1. Create `Editor/Network Storage/collections/{name}.json`
2. Define the schema with field types and defaults
3. Push via Sync Tool
4. Reference it in endpoint steps as `"collection": "name"`
5. If the collection has constants/tables, they're available in endpoints as `{{values.key}}`

### Adding a New Workflow

1. Create `Editor/Network Storage/workflows/{id}.json`
2. Define either a simple condition-only workflow or a multi-step workflow:
   - **Condition-only:** Uses `condition` and `onFail` fields directly
   - **Multi-step:** Uses `params` for typed inputs, `steps` for a pipeline (up to 10 steps), and `returns` to pass values back
3. Push via Sync Tool
4. Reference from endpoints:
   - Condition-only: `{ "type": "condition", "workflow": "id", "bindings": {...} }`
   - Multi-step: `{ "type": "workflow", "workflow": "id", "params": {...} }`

### Deleting Player Records

Use the `delete` step type to remove records. Deletes are deferred until all conditions pass:
1. Ensure the collection has `allowRecordDelete: true` in its schema
2. Add a `delete` step: `{ "id": "wipe", "type": "delete", "collection": "players", "key": "{{steamId}}_default" }`
3. Combine with `read` + `condition` steps for validation before deletion

### Modifying Game Data

After editing any JSON file in `Editor/Network Storage/`:
1. Open the Sync Tool (Editor → Network Storage → Sync Tool)
2. Click **Check Remote** to see what changed
3. Click **Push** (per item) or **Push All** to upload changes
4. Changes are live immediately

### Adding a New Ore Type (Example Pattern)

1. Add the ore to `collections/game_values.json` in `tables.ore_types.rows`
2. Update the player data collection schema if needed (e.g., add ore storage field)
3. Push both collections via Sync Tool
4. Update any fallback data in game code to match

## Understanding the Sync Tool Code

### `SyncToolConfig.cs` — The Hub
This is the central configuration class. It:
- Loads credentials from `.env`
- Provides all file paths (`EnvFilePath`, `CollectionsPath`, `EndpointsPath`, `WorkflowsPath`)
- Loads/saves data files (collections, endpoints, workflows)
- Scaffolds new projects on first install
- Handles migration from older config locations

Key paths:
```
SyncToolsPath  → {project}/Editor/{DataFolder}/
ConfigPath     → {SyncToolsPath}/config/
EnvFilePath    → {ConfigPath}/.env
CollectionsPath → {SyncToolsPath}/collections/
EndpointsPath  → {SyncToolsPath}/endpoints/
WorkflowsPath  → {SyncToolsPath}/workflows/
```

### `SyncToolApi.cs` — HTTP Client
Makes authenticated requests to the sbox.cool management API:
- `Request(method, path, body)` — generic authenticated request
- `GetEndpoints()` / `PushEndpoints(data)` — endpoint CRUD
- `GetCollections()` / `PushCollections(data)` — collection CRUD
- `GetWorkflows()` / `PushWorkflows(data)` — workflow CRUD
- `Validate(publicKey)` — test credentials

All requests use the secret key via `x-api-key` header.

### `SyncToolTransforms.cs` — Format Conversion
Converts between local JSON file format and server API format:
- `EndpointsToServer()` / `ServerEndpointToLocal()` — endpoint transforms
- `CollectionsToServer()` / `ServerCollectionToLocal()` / `ServerToCollections()` — collection transforms
- `WorkflowsToServer()` / `ServerWorkflowToLocal()` / `ServerToWorkflows()` — workflow transforms

Key behavior: preserves server-assigned IDs by matching on `slug` (endpoints), `name` (collections), or `id` (workflows).

### `SyncToolWindow.cs` — Main UI
The DockWindow that renders the sync tool using `OnPaint()`. Uses a paint-based approach (not widget layout) for compatibility with s&box's DockWindow system. Features:
- Per-item status indicators (synced, local only, differs, remote only)
- Push/Pull buttons per item
- Push All button
- Diff viewer integration
- After successful push, items transition from LocalOnly/Differs → InSync

### `SetupWindow.cs` — Credentials UI
Paint-based DockWindow with custom input fields. Each field has Paste and Clear buttons. Clipboard access uses `powershell.exe Get-Clipboard` to bypass s&box editor sandbox limitations.

## API Endpoints

The management API base URL is `https://api.sboxcool.com/v3/manage/{projectId}/`.

| Method | Path | Description |
|--------|------|-------------|
| GET | `endpoints` | Fetch all endpoints |
| PUT | `endpoints` | Push endpoint definitions |
| GET | `collections` | Fetch all collections |
| PUT | `collections` | Push collection schemas |
| GET | `workflows` | Fetch all workflows |
| PUT | `workflows` | Push workflow definitions |
| GET | `validate` | Validate credentials |

All management requests require `x-api-key: {secret_key}` header.

## Gotchas

1. **DockWindow uses OnPaint, not Layout** — s&box DockWindows render via `OnPaint()` with `Paint.*` drawing calls. Widget-based layouts (Label, Button, LineEdit) don't render properly in DockWindows. The Sync Tool and Setup windows use click-region tracking for interactivity.

2. **No clipboard API in s&box editor** — `Editor.Application.Clipboard` doesn't exist. The library uses `powershell.exe Get-Clipboard` via `System.Diagnostics.Process`. P/Invoke (`DllImport`) is blocked by the s&box sandbox.

3. **Editor/ never ships** — Everything in `Editor/` is excluded from s&box publishing. This is why it's safe to store secrets there.

4. **JSON normalization for diff** — When comparing local vs remote, both sides are normalized (sorted keys, consistent formatting) to avoid false diffs from key ordering.

5. **ID preservation on push** — The Sync Tool fetches existing remote data before pushing, so it can preserve server-assigned IDs. New items get auto-generated IDs.
