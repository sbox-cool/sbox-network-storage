# Network Storage — Runtime Client

The runtime client (`NetworkStorageClient`) is what your game code uses to interact with the sbox.cool backend at runtime. It ships with your game (unlike the editor tools).

## Auto-Configuration (Recommended)

The client **automatically reads credentials from your `.env` file** on first use. No setup code needed.

Just call any API method and it works:

```csharp
// No Configure() call needed — reads from Editor/Network Storage/config/.env automatically
var player = await NetworkStorage.CallEndpoint( "load-player" );
var values = await NetworkStorage.GetGameValues();
```

The auto-config searches for `.env` in these locations (first match wins):
1. `Editor/Network Storage/config/.env` (current)
2. `Editor/Network Storage/.env` (legacy)
3. `Editor/SyncTools/.env` (legacy)

It reads **only** the public key and project ID — the secret key is **never** loaded at runtime.

## Manual Configuration (Optional Override)

If you need to override the `.env` values (e.g., for testing against a different project), call `Configure()` before any API use:

```csharp
NetworkStorage.Configure( "your-project-id", "sbox_ns_your_key" );
```

Once `Configure()` is called, it takes precedence over auto-config.

## Making Requests

### Call an Endpoint

```csharp
// POST to an endpoint with input
var input = new Dictionary<string, object>
{
    ["oreType"] = "iron",
    ["amount"] = 50
};

var response = await NetworkStorage.CallEndpoint( "sell-ore", input );
if ( response.HasValue )
{
    // Success — response.Value is the parsed JSON body
}

// GET endpoint (no input)
var leaderboard = await NetworkStorage.CallEndpoint( "get-leaderboard" );
```

### Get Game Values

```csharp
// Fetch all game config (constants + tables) from the server
var values = await NetworkStorage.GetGameValues();
```

### Read a Document

```csharp
// Read the current player's document (uses their Steam ID)
var playerData = await NetworkStorage.GetDocument( "players" );

// Read a specific document by ID
var doc = await NetworkStorage.GetDocument( "players", "76561198012345678" );
```

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsConfigured` | bool | True after auto-config or manual `Configure()` loads valid credentials |
| `ProjectId` | string | The active project ID |
| `ApiKey` | string | The active public API key |
| `BaseUrl` | string | API base URL (default: `https://api.sboxcool.com`) |
| `ApiVersion` | string | API version (default: `v3`) |
| `ApiRoot` | string | Full versioned root, e.g. `https://api.sboxcool.com/v3` |

## Helper Classes

### `NetworkStorageExtensions`
Helper extension methods for common patterns like extracting values from JSON responses.

### `JsonHelpers`
Utilities for JSON operations: merging objects, computing diffs, pretty-printing.

### `SaveStateTracker`
Tracks whether local data has changed since last save. Useful for implementing auto-save or dirty indicators.

### `NetLog`
Scoped logging utility with `[NetworkStorage]` prefix for easy filtering in the s&box console.

## Data Source Modes

The client respects the data source preference set in the Setup window:

| Mode | Runtime Behavior |
|------|-----------------|
| **API + Fallback** | Calls the API; if it fails, reads from local JSON files |
| **API Only** | Always calls the API; returns null on failure |
| **JSON Only** | Reads from local JSON files only; never makes HTTP calls |

## Error Handling

All methods return `JsonElement?` — `null` on failure, a parsed JSON element on success.

The client automatically:
- Logs errors to the s&box console with `[NetworkStorage]` prefix
- Parses server error responses (`{ ok: false, error: "...", message: "..." }`)
- Returns `null` on network failures, parse errors, or server rejections

```csharp
var result = await NetworkStorage.CallEndpoint( "buy-upgrade", input );
if ( result == null )
{
    // Server rejected or network failed — check console for details
    return;
}
// Success — use result.Value
```
