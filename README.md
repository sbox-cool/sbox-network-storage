# Server-side endpoints & database for s&box
- Install via s&box editor -> https://sbox.game/sboxcool/network-storage
- Self host -> https://github.com/sbox-cool/sbox-network-storage-server


Persistent cloud storage, server-side endpoints, and an editor sync tool for s&box games — [sboxns.com](https://sboxns.com) by sbox.cool.

## Useful Links
- Network Storage: https://sbox.cool/tools/network-storage
- Documentation & Tutorials: http://sbox.cool/wiki/network-storage-v3
- s&box editor Library: [https://sbox.game/sboxcool/network-storage](https://sbox.game/sboxcool/network-storage)
   - Search `Network Storage by sbox.cool` with author name: `sboxcool`
- sbox.cool and sboxcool.com are both operated the same group. `SboxCool.com` is our LTS url, e.g. `api.sboxcool.com`.

## Features

- **Runtime Client** — Call server endpoints, fetch game values, read/write documents from your game code
- **Editor Sync Tool** — Push and pull collections and endpoints between local YAML source files and the sboxcool.com dashboard
- **Setup Wizard** — Editor window for entering and validating your API credentials
- **Diff Viewer** — Side-by-side comparison of local vs remote data before syncing
- **Network Logger** — Ring buffer that captures all API traffic for in-game debug panels
- **JSON Helpers** — Extension methods for reading API response payloads

## Installation

### From s&box (recommended)

Install directly from the s&box asset browser:

**https://sbox.game/sboxcool/network-storage**

Or find it in the editor:

1. Open the s&box editor
2. Go to **Editor > Library Manager**
3. Search for **"Network Storage"**
4. Look for the one published by **sboxcool** — do not install from other authors
5. Click **Add to Project**

The library appears in your project's `Libraries/` folder automatically.

### Manual (git clone)

Clone this repo into your project's `Libraries/` directory:

```
cd "YourProject/Libraries"
git clone https://github.com/sbox-cool/sbox-network-storage "Network Storage by sboxcool"
```

Or add it as a git submodule:

```
git submodule add https://github.com/sbox-cool/sbox-network-storage "Libraries/Network Storage by sboxcool"
```

### Updating

Use **Library Manager** to update; it replaces the library folder. For a manual install, `git pull` inside the library folder, or delete the old `Libraries/<library folder>` completely and install again. **Do not copy new files over an old install**: files moved between versions (for example `JsonHelpers.cs`, `NetLog.cs`, `NetworkStorageClient.cs` now live under `Code/Core/`) would exist twice and the project fails to compile with duplicate-type errors (CS0121).

### Requirements

Library versions before `1.0.309133` do not start on s&box editor 26.10 or later (`Constructor on type 'SyncToolWindow' not found`). Update the library.

### Library identity and release process

`NetworkStorage.LibraryVersion` is the library SemVer (`1.1.0`), not the
s&box package revision in `.version` or a game's published revision.
`NetworkStorage.MinServerVersion` is `0.4.0`, the self-hosted baseline;
it is metadata, not a runtime rejection or a claim of editor certification.
The existing public `PackageVersion` property reports the same SemVer.

Runtime, Sync Tool and Python sync requests send `x-ns-library-version`.
Sync Tool and Python additionally send `x-ns-client-type: editor` and
`x-ns-library-revision` when the installed package's `.version` contains a
positive 64-bit revision (`1.0.<revision>`). A missing/unreadable/malformed
revision is omitted. The editor reads the standard
`Libraries/sboxcool.network-storage/.version` path; a differently named manual
checkout omits that optional header. Python reads its own library directory.
Restart the editor after replacing a library, because its package metadata is
cached for the session.

Runtime does **not** read the library checkout's `.version`: game filesystem
mounts expose assets, not a reliable library source-directory path. Its
availability in published bundles has not been verified, so runtime omits the
package-revision header rather than guessing or reporting the game's revision.
No credential, authentication, publish-target or game-revision behavior changes.
Servers without compatibility metadata continue to work; these headers are
self-reported diagnostics, not authentication.

Maintainer release sequence:

1. Bump `LibraryVersion` in `Code/Core/NetworkStorageClient.cs`; Python reads
   that constant, so there is no second SemVer to update.
2. Record exact-source editor/game evidence against both the current server and
   `0.4.0`, including public/authenticated requests and Sync Tool connect/push.
   Follow [compatibility certification](docs/compatibility-certification.md).
3. Tag the tested source `vX.Y.Z`, then publish on sbox.game.
4. Create GitHub Release `vX.Y.Z` recording the assigned s&box package revision,
   `MinServerVersion`, included PRs/issues and tested server/engine/provider
   pairings with evidence links.
5. Submit the published-library entry and tested evidence to the server's
   [`compat/library-matrix.json`](https://github.com/sbox-cool/sbox-network-storage-server/blob/main/compat/library-matrix.json)
   ([server #59](https://github.com/sbox-cool/sbox-network-storage-server/issues/59)).

Before releasing `1.1.0`, retain the last legacy source `0532c00`
(package `1.0.309133`) with a maintainer-created `v1.0.309133` tag. No tag or
package publication is implied by this source change. For rollback, replace the
**whole library folder** with a supported tagged release, then republish the game.

Tracking: [L1 #7](https://github.com/sbox-cool/sbox-network-storage/issues/7)
↔ [server #58](https://github.com/sbox-cool/sbox-network-storage-server/issues/58).
`pending-sbox-editor-test`: interactive Play mode and Sync Tool against the
version-aware server and released `0.4.0`; record library source commit/SemVer,
assigned package revision, server commit/version, engine build, database and
evidence URLs. Headless-engine/dedicated-server smoke checks alone do not certify
this pairing or close that gate.


## Setup

### 1. Create a project on sboxcool.com

1. Go to [sboxcool.com](https://sboxcool.com) and create a new project
2. Note your **Project ID** from the dashboard
3. Create API keys:
   - **Public key** (`sbox_ns_` prefix) — used by the game client at runtime
   - **Secret key** (`sbox_sk_` prefix) — used by the editor sync tool only, never ships with your game

### 2. Configure in the s&box editor

1. Open **Editor > Network Storage > Setup**
2. Enter your **Project ID**, **Public API Key**, and **Secret Key**
3. Click **Save Configuration**
4. Click **Test Connection** to verify

Your credentials are saved to `Editor/Network Storage/.env` — this file is in the `Editor/` directory which s&box excludes from publishing. Your secret key never ships with your game.

To use a self-hosted server instead of `api.sboxcool.com`, set `baseUrl` in `Editor/Network Storage/config/public/projectConfig.json` (or `NetworkStorage.Configure( projectId, publicKey, "https://your-server" )` in game code). The Sync Tool window shows the active server (highlighted when it is not `api.sboxcool.com`) and the last request it sent.

### Generated code

After a sync or pull the Sync Tool writes into **your project**: `Code/Data/NetworkStorage/` (`Readme.txt` plus `autoGenerated_Config.cs`, `_Collections.cs`, `_Endpoints.cs`, `_Workflows.cs`) and `Assets/network-storage.credentials.json` (project ID, **public** key, base URL, API version, auth/encryption flags; never the secret key). The `autoGenerated_*.cs` files are deleted and regenerated every time: do not edit them. Because `credentials.json` ships with the game, point `baseUrl` at the server you want players to use before publishing.

### Dedicated server endpoint secret (optional)

Dedicated servers can supply a runtime endpoint secret without putting it in the published bundle:

```bash
sbox-server.exe +game your.org.game your.map +hostname "My Server" +network_storage_secret_key sbox_sk_your_secret_key
```

The primary dedicated launch key is `+network_storage_secret_key`. The library also accepts aliases (`+network-storage-secret-key`, `+sboxcool_secret_key`, `+networkStorageSecretKey`, `+sboxcoolSecretKey`, `+nsSecretKey`, `+ns_secret_key`) on `Application.IsDedicatedServer` hosts, and validates the supplied key at game startup. Generic names such as `+secret-key`, `+secret_key`, and `+secretKey` are intentionally not supported. When the dedicated key is used, the library does not request or send s&box auth tokens. Endpoint requests use an internal backend flag while the actual launch secret travels in the `x-secret-key` HTTPS header. Storage document row reads/writes/deletes use the secret as the HTTPS `x-api-key` header without a URL secret flag so secret keys with `collections:execute` can access endpoint-only collections. Do not put `sbox_sk_` values in code, public credentials, or client-callable URLs. Collection schema deletion remains website-only.

## Quick Start

Create a config class in your game project:

```csharp
namespace Sandbox;

public static class MyNetStorageConfig
{
    public const string ProjectId = "your_project_id";
    public const string ApiKey = "sbox_ns_your_public_key";

    public static void Initialize()
    {
        NetworkStorage.Configure( ProjectId, ApiKey );
    }
}
```

Then call endpoints from your game code:

```csharp
// Configure once at startup
if ( !NetworkStorage.IsConfigured )
    MyNetStorageConfig.Initialize();

// Call a server endpoint (GET)
var player = await NetworkStorage.CallEndpoint( "load-player" );

// Call with input (POST)
var result = await NetworkStorage.CallEndpoint( "mine-ore", new
{
    ore_id = "iron",
    kg = 5.0f
} );

// Endpoint URLs from the dashboard are also accepted; the slug is extracted.
var sameResult = await NetworkStorage.CallEndpoint(
    "https://api.sboxcool.com/v3/endpoints/your_project_id/mine-ore?apiKey=sbox_ns_your_public_key",
    new { ore_id = "iron", kg = 5.0f } );

// Update collection documents directly (dedicated servers attach +network_storage_secret_key automatically)
await NetworkStorage.SaveDocument( "player-data", Game.SteamId.ToString(), new { level = 2, xp = 100 } );
await NetworkStorage.UpdateDocument( "player-data", Game.SteamId.ToString(),
    NetworkStorageOperation.Increment( "xp", 50, source: "server", reason: "quest" ),
    NetworkStorageOperation.Set( "lastSeen", DateTimeOffset.UtcNow.ToUnixTimeSeconds() ) );
await NetworkStorage.DeleteDocument( "player-data", Game.SteamId.ToString() );

// Read the response
if ( result.HasValue )
{
    var currency = JsonHelpers.GetInt( result.Value, "currency", 0 );
    var level = JsonHelpers.GetInt( result.Value, "level", 1 );
}
```

See the [Examples/](Examples/) folder for complete working patterns.

## Runtime API Reference

### NetworkStorage (static)

| Method | Description |
| --- | --- |
| `Configure(projectId, apiKey)` | Set credentials. Call once at startup. |
| `CallEndpoint(slugOrUrl, input?)` | Call a server endpoint by slug or endpoint URL. Returns `JsonElement?`. |
| `GetGameValues()` | Fetch all game values (constants + tables). Returns `JsonElement?`. |
| `GetDocument(collectionId, documentId?)` | Read a document from a collection. Defaults to current player's Steam ID. |
| `SaveDocument(collectionId, documentId, data)` | Save/replace a collection document. Dedicated servers attach the configured secret key automatically. |
| `UpdateDocument(collectionId, documentId, ops)` | Apply server-side operations (`set`, `inc`, `push`, `pull`, `remove`) to a document. |
| `DeleteDocument(collectionId, documentId?)` | Delete one collection document/row. Public calls require record deletes to be enabled; dedicated secret keys with collection execute permission can delete rows for diagnostics/backoffice cleanup. Does not delete collection schemas. |
| `ListRecords/CreateRecord/RenameRecord/DeleteRecord` | Manage multi-record save slots. |
| `NetworkStorageAnalytics.TrackEvent(eventType, payload?)` | Report an allowlisted custom Player Analytics event. |
| `NetworkStorageAnalytics.Warning(code, message?, context?)` | Report a recoverable warning/pain point to the player timeline. |
| `NetworkStorageAnalytics.Error(exception, code?, context?)` | Report an exception/error to the player timeline without breaking gameplay. |
| `NetworkStorageAnalytics.SessionStart/SessionEnd` | Report managed session boundaries when analytics is enabled. |
| `IsConfigured` | `true` after `Configure()` has been called. |
| `ApiRoot` | The full versioned API URL (e.g. `https://api.sboxcool.com/v3`). |

See [analytics.md](analytics.md) for Player Analytics setup, custom events, warnings, and error reporting examples.

### JsonHelpers (static)

Safe extraction from API `JsonElement` payloads with caller-provided defaults. Handles missing keys and string-to-number coercion.

```csharp
var name = JsonHelpers.GetString( data, "playerName", "Unknown" );
var level = JsonHelpers.GetInt( data, "level", 1 );
var speed = JsonHelpers.GetFloat( data, "speed", 1.0f );
var active = JsonHelpers.GetBool( data, "active", true );
```

### Extension Methods (on JsonElement)

Shorthand wrappers around JsonHelpers, plus collection parsers:

```csharp
// Shorthand
var name = data.Str( "playerName", "Unknown" );
var level = data.Int( "level", 1 );
var speed = data.Float( "speed", 1.0f );

// Parse arrays
var upgrades = data.ReadStringList( "purchasedUpgrades" );

// Parse objects
var ores = data.ReadDictionary( "ores", v => (float)v.GetDouble() );

// Parse table rows
var items = data.ReadList( "rows", row => new ItemInfo(
    row.Str( "id" ), row.Str( "name" ), row.Int( "tier" )
) );
```

### SaveStateTracker

Wraps endpoint calls with automatic state management for HUD feedback:

```csharp
var tracker = new SaveStateTracker();

// Simple tracked call
var result = await tracker.Call( "mine-ore", new { ore_id = "iron", kg = 5 } );
// tracker.State is now Saved or Error
// tracker.IsBusy is true while any call is in flight

// Optimistic update with auto-revert
await tracker.CallAndApply( "sell-ore", input,
    applyOptimistic: () => { /* update local state */ },
    applyServer: (data) => { /* apply authoritative response */ },
    revert: () => { /* undo on failure */ }
);
```

### NetLog

Static ring buffer capturing all Network Storage events. Use it for debug panels:

```csharp
// Entries are added automatically by NetworkStorage
foreach ( var entry in NetLog.Entries )
{
    // entry.Time, entry.Kind (Request/Response/Error/Info),
    // entry.Tag, entry.Message
}

// Add custom entries
NetLog.Info( "my-system", "Custom log message" );

// Track changes (for UI refresh)
var version = NetLog.Version; // increments on every add/clear
```

## Editor Sync Tool

The Sync Tool lets you manage your sboxcool.com project data as local YAML source files, then push/pull changes through the API.
YAML is the preferred authoring format. Existing legacy JSON resources remain readable and pushable for compatibility; newly created and pulled resources are written as YAML.

### Open the Sync Tool

**Editor > Network Storage > Sync Tool**

### Workflow

1. **Define new collections, endpoints, workflows, tests, and libraries** as YAML source files in `Editor/Network Storage/`
2. Existing JSON resources can still be pushed; migrate them to YAML when convenient to use the current authoring format
3. Click **Check for Updates** to compare local files against the remote server
4. **Push** sends your local changes to sboxcool.com
5. **Pull** downloads the latest from sboxcool.com to your local files
6. **View Diff** shows a side-by-side comparison before overwriting

### Source Authoring

YAML source files use kind-specific names:

```text
collections/<id>.collection.yml
endpoints/<slug>.endpoint.yml
workflows/<id>.workflow.yml
tests/<id>.test.yml
libraries/<id>.library.yml
```

`.yaml` is also accepted, but project documentation and generated examples should prefer `.yml`. Legacy JSON files are unsupported and are not automatically reverse-converted into YAML.

Each source file starts with:

```yaml
sourceVersion: 1
kind: endpoint
id: mine-ore
definition:
  method: POST
  steps: []
```

See `source-authoring.md`, `source-authoring.schema.json`, and `Examples/SourceAuthoring/` for the current source model and examples.

Validate and preview source files locally:

```powershell
python Libraries/sboxcool.network-storage/Editor/source_compiler.py --project-root .
python Libraries/sboxcool.network-storage/Editor/sync.py --project-root . --sources --dry-run
```

### Status Indicators

| Icon | Meaning |
| --- | --- |
| ✓ | In sync — local matches remote |
| ▲ | Local only — exists locally but not on server |
| ▼ | Remote only — exists on server but not locally |
| ● | Differs — local and remote have different content |

### Data Folder Structure

```
Editor/
  Network Storage/              # Configurable in Setup
    .env                        # Credentials (gitignored, never published)
    collections/                # YAML source collections
      player_data.collection.yml
      game_values.collection.yml
    endpoints/                  # YAML source endpoints
      load-player.endpoint.yml
      mine-ore.endpoint.yml
      sell-ore.endpoint.yml
    workflows/
      check-currency.workflow.yml
    libraries/
      economy.library.yml
```

## Security

- **Secret key** (`sbox_sk_`) is stored in `Editor/Network Storage/.env` — the `Editor/` directory is excluded from s&box publishing, so your secret key never ships with your game
- **Public key** (`sbox_ns_`) is safe to include in your game code — it can only be used with Steam authentication
- The `.env` file is gitignored by default — never commit it to version control
- See `.env.example` for the expected format

## Data Source Mode

Network Storage runs in **API Only** mode. Runtime reads and editor sync operations use the API; local JSON files and API fallback modes are unsupported.

## MCP Server (for AI Agents)

This repo includes an MCP (Model Context Protocol) server that gives AI coding agents like Claude deep knowledge of the Network Storage system — collections, endpoints, workflows, template syntax, error diagnosis, and more.

### What It Provides

- **10 tools** — validate source files, scaffold new collections/endpoints/workflows, get documentation, diagnose errors
- **8 resources** — all documentation files exposed for contextual reading

### Setup

The MCP server requires [Bun](https://bun.sh) to be installed.

1. The `.mcp.json` file at the repo root auto-configures Claude Code to use the MCP server
2. Dependencies are installed automatically on first launch (`bun install`)
3. Open Claude Code in this repo and the MCP will be available

To verify it's working, run `/mcp` in Claude Code — you should see `sbox-network-storage` listed.

### Manual Setup (other MCP clients)

If your MCP client doesn't auto-discover `.mcp.json`, add this to your MCP configuration:

```json
{
  "mcpServers": {
    "sbox-network-storage": {
      "command": "bash",
      "args": ["-c", "cd mcp && bun install —silent && cd .. && bun run mcp/index.ts"]
    }
  }
}
```

Set the working directory to this repository root.

### Available Tools

| Tool | Description |
| --- | --- |
| `get_documentation` | Retrieve docs by topic (collections, endpoints, workflows, setup, errors, etc.) |
| `validate_collection` | Validate a collection definition for correct schema and naming |
| `validate_endpoint` | Validate an endpoint definition — steps, operators, templates, constraints |
| `validate_workflow` | Validate a workflow definition — conditions, onFail config |
| `scaffold_collection` | Generate a collection YAML source template |
| `scaffold_endpoint` | Generate an endpoint YAML source template |
| `scaffold_workflow` | Generate a workflow YAML source template |
| `get_examples` | Get examples for common game scenarios (inventory, currency, leaderboard, etc.) |
| `validate_env_config` | Validate `.env` credential file format |
| `diagnose_error` | Diagnose s&box console errors and suggest fixes |


## Compatibility (LTS)

The library and server release independently and both are LTS. Certify each
library/server/engine/provider pairing once with real evidence; a fundamental
wire/semantic change invalidates affected certifications and must be re-tested
before release. Public methods and endpoints are LTS surface: no silent
rename, removal or narrowing. See
[docs/compatibility-certification.md](docs/compatibility-certification.md).

## License

MIT — see [LICENSE](LICENSE).
