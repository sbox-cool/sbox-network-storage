# Network Storage — Runtime Client

The runtime client (`NetworkStorageClient`) is what your game code uses to interact with the sbox.cool backend at runtime. It ships with your game (unlike the editor tools).

## Setup

In your game, create a configuration class that provides credentials:

```csharp
using Sandbox;

public class NetworkStorageConfig : Component, Component.INetworkListener
{
    public static NetworkStorageConfig Instance { get; private set; }

    // Public key only — secret key is NEVER used at runtime
    private const string PublicKey = "sbox_ns_your_public_key_here";
    private const string ProjectId = "your-project-id";

    protected override void OnStart()
    {
        Instance = this;
    }
}
```

## Making Requests

### Call an Endpoint

```csharp
// POST to an endpoint
var input = new Dictionary<string, object>
{
    ["oreType"] = "iron",
    ["amount"] = 50
};

var response = await NetworkStorage.Post( "sell-ore", input );
if ( response.HasValue )
{
    // Success — parse response
    var data = response.Value;
}
```

### Read a Collection

```csharp
// GET player data
var playerData = await NetworkStorage.Get( "players" );
```

## Classes

### `NetworkStorageClient`
The main HTTP client. Handles:
- Authenticated requests to the sbox.cool API using the public key
- JSON serialization/deserialization
- Error handling and logging

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
| **API + Fallback** | Calls the API; if it fails, reads from local JSON files in `Editor/Network Storage/` |
| **API Only** | Always calls the API; returns null on failure |
| **JSON Only** | Reads from local JSON files only; never makes HTTP calls |

JSON Only mode is useful for offline development and testing without hitting the server.
