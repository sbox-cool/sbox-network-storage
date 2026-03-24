# Network Storage — Runtime Client

The runtime client (`NetworkStorageClient`) is what your game code uses to interact with the sbox.cool backend at runtime. It ships with your game (unlike the editor tools).

## Auto-Configuration (Recommended)

The client **automatically reads credentials from `Assets/network-storage.credentials.json`** on first API use. No setup code needed.

Just call any API method and it works:

```csharp
// No Configure() call needed — auto-configures from credentials file
var player = await NetworkStorage.CallEndpoint( "load-player" );
var values = await NetworkStorage.GetGameValues();
```

The credentials file is created by the Setup window (**Editor → Network Storage → Setup**) and contains only the Project ID and Public Key (no secrets). It looks like:

```json
{
  "projectId": "your_project_id",
  "publicKey": "sbox_ns_your_public_key",
  "baseUrl": "https://api.sboxcool.com",
  "apiVersion": "v3"
}
```

Auto-config searches these paths (first match wins):
1. `network-storage.credentials.json`
2. `/network-storage.credentials.json`
3. `Assets/network-storage.credentials.json`
4. `/Assets/network-storage.credentials.json`

The secret key is **never** loaded at runtime — only the public key ships with your game.

> **Important:** Auto-configuration uses a static flag that persists across hot-reloads.
> If you change credentials, do a **full restart** of s&box to pick up the new values.

## Manual Configuration (Optional Override)

If you need to override credentials (e.g., for testing against a different project), call `Configure()` before any API use:

```csharp
NetworkStorage.Configure( "your-project-id", "sbox_ns_your_key" );
```

Once `Configure()` is called, it takes precedence over auto-config. This also sets the static flag, so `AutoConfigure()` will not run.

See [Getting Started](getting-started.md) for a complete setup walkthrough including credential testing.

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
