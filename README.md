# Network Storage for s&box
Persistent cloud storage, server-side endpoints, and an editor sync tool for s&box games — powered by the [sboxcool.com](https://sboxcool.com) API.
<img width="488" height="647" alt="Screenshot 2026-03-24 153432" src="https://github.com/user-attachments/assets/cdd10f39-e757-45da-858a-dbefd14b206e" />
<img width="489" height="540" alt="Screenshot 2026-03-24 153341" src="https://github.com/user-attachments/assets/24ce6892-8df6-4b68-857f-b1e2d43703e9" />

## Useful Links
- Network Storage: https://sbox.cool/tools/network-storage
- Documentation & Tutorials: http://sbox.cool/wiki/network-storage-v3
- s&box editor Library: https://sbox.game/sboxcool/network-storage
   - Search `Network Storage by sbox.cool` with author name: `sboxcool`
- sbox.cool and sboxcool.com are both operated the same group. `SboxCool.com` is our LTS url, e.g. `api.sboxcool.com`.

## Features

- **Runtime Client** — Call server endpoints, fetch game values, read/write documents from your game code
- **Editor Sync Tool** — Push and pull collections and endpoints between local JSON files and the sboxcool.com dashboard
- **Setup Wizard** — Editor window for entering and validating your API credentials
- **Diff Viewer** — Side-by-side comparison of local vs remote data before syncing
- **Network Logger** — Ring buffer that captures all API traffic for in-game debug panels
- **JSON Helpers** — Extension methods for safe deserialization with fallbacks

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
|--------|-------------|
| `Configure(projectId, apiKey)` | Set credentials. Call once at startup. |
| `CallEndpoint(slug, input?)` | Call a server endpoint by slug. Returns `JsonElement?`. |
| `GetGameValues()` | Fetch all game values (constants + tables). Returns `JsonElement?`. |
| `GetDocument(collectionId, documentId?)` | Read a document from a collection. Defaults to current player's Steam ID. |
| `IsConfigured` | `true` after `Configure()` has been called. |
| `ApiRoot` | The full versioned API URL (e.g. `https://api.sboxcool.com/v3`). |

### JsonHelpers (static)

Safe extraction from `JsonElement` with fallback defaults. Handles missing keys and string-to-number coercion.

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

The Sync Tool lets you manage your sboxcool.com project data as local JSON files, then push/pull changes.

### Open the Sync Tool

**Editor > Network Storage > Sync Tool**

### Workflow

1. **Define collections and endpoints** as JSON files in `Editor/Network Storage/`
2. Click **Check for Updates** to compare local files against the remote server
3. **Push** sends your local changes to sboxcool.com
4. **Pull** downloads the latest from sboxcool.com to your local files
5. **View Diff** shows a side-by-side comparison before overwriting

### Status Indicators

| Icon | Meaning |
|------|---------|
| ✓ | In sync — local matches remote |
| ▲ | Local only — exists locally but not on server |
| ▼ | Remote only — exists on server but not locally |
| ● | Differs — local and remote have different content |

### Data Folder Structure

```
Editor/
  Network Storage/              # Configurable in Setup
    .env                        # Credentials (gitignored, never published)
    collections/                # One JSON file per collection
      player_data.json
      game_values.json
    endpoints/                  # One JSON file per endpoint
      load-player.json
      mine-ore.json
      sell-ore.json
```

## Security

- **Secret key** (`sbox_sk_`) is stored in `Editor/Network Storage/.env` — the `Editor/` directory is excluded from s&box publishing, so your secret key never ships with your game
- **Public key** (`sbox_ns_`) is safe to include in your game code — it can only be used with Steam authentication
- The `.env` file is gitignored by default — never commit it to version control
- See `.env.example` for the expected format

## Data Source Modes

Configure in **Editor > Network Storage > Setup** under "Data Source":

| Mode | Behavior |
|------|----------|
| **API + Fallback** (default) | Try API first, fall back to local JSON files if unavailable |
| **API Only** | Always fetch from API, no fallback |
| **JSON Only** | Read from local JSON files only, no API calls |

## License

MIT — see [LICENSE](LICENSE).
