# Runtime Client API

> **Note:** For the most up-to-date documentation, visit https://sbox.cool/wiki/network-storage-v3 — these repo docs may be outdated.

The runtime client (`NetworkStorageClient`) is what your game code uses at runtime. It ships with your game.

## Auto-Configuration

The client **automatically reads** from `Assets/network-storage.credentials.json` on first API use:

```csharp
// No Configure() needed — auto-configures
var player = await NetworkStorage.CallEndpoint("load-player");
var values = await NetworkStorage.GetGameValues();
```

## Manual Configuration

```csharp
NetworkStorage.Configure("your-project-id", "sbox_ns_your_key");
```

## API Methods

### CallEndpoint
```csharp
// POST with input
var result = await NetworkStorage.CallEndpoint("sell-ore", new { oreType = "iron", amount = 50 });

// GET (no input)
var data = await NetworkStorage.CallEndpoint("get-leaderboard");
```

### GetGameValues
```csharp
var values = await NetworkStorage.GetGameValues();
```

### GetDocument
```csharp
var playerData = await NetworkStorage.GetDocument("players");
var doc = await NetworkStorage.GetDocument("players", "76561198012345678");
```

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsConfigured` | bool | True after config loads |
| `ProjectId` | string | Active project ID |
| `ApiKey` | string | Active public API key |
| `BaseUrl` | string | API base URL |
| `ApiVersion` | string | API version |
| `ApiRoot` | string | Full versioned root |

## Error Handling

All methods return `JsonElement?` — `null` on failure. Errors are logged to console with `[NetworkStorage]` prefix.

```csharp
var result = await NetworkStorage.CallEndpoint("buy-upgrade", input);
if (result == null)
{
    // Server rejected or network failed — check console for details
    return;
}
```
