# Network Storage v3 - Documentation

All documentation for this project is hosted externally. Use the **superdocs MCP** (configured in `.mcp.json`) for AI-assisted access, or browse the links below.

## Links

- **Wiki**: [sbox.cool/wiki/network-storage-v3](https://sbox.cool/wiki/network-storage-v3)
- **Tools Dashboard**: [superdocs.sbox.cool/tools](https://superdocs.sbox.cool/tools)
- **Full SuperDocs**: [superdocs.sbox.cool](https://superdocs.sbox.cool)
- **Endpoint Standards**: [superdocs.sbox.cool/tools/endpoint-standards](https://superdocs.sbox.cool/tools/endpoint-standards)

## Superdocs MCP Tools

The superdocs MCP server provides these tools relevant to Network Storage v3:

| Tool | Description |
|------|-------------|
| `get_network_storage_guide` | Complete NS v3 guide: setup, CallEndpoint/GetDocument/GetGameValues usage, endpoint pipeline (read/condition/transform/write/lookup/filter steps), collections, game values, workflows, error codes, and production C# patterns. |
| `get_endpoint_standards` | Best practices for designing endpoints. Covers anti-patterns (never trust client-supplied costs/rewards), correct patterns (lookup from Game Values), complete examples (shop, crafting, XP), and a quick reference. |
| `get_networking_patterns` | Networking patterns and multiplayer architecture for s&box. |
| `get_recipes` | Ready-made endpoint recipes and implementation templates. |
| `get_examples` | Code examples for common Network Storage scenarios. |
| `get_patterns` | General s&box design patterns. |
| `get_pitfalls` | Common mistakes and how to avoid them. |
| `search` | Full-text search across all s&box documentation. |
| `get_type` | Look up any s&box API type (class, struct, enum) with members and docs. |
| `get_type_members` | Get detailed member info for any s&box type. |
| `browse_api` | Browse the full s&box API by namespace. |

## Quick Start

### Installation

Install **Network Storage by sbox.cool** from the s&box Library Manager or visit [sbox.game/sboxcool/network-storage](https://sbox.game/sboxcool/network-storage).

Create a project at [sbox.cool/tools/network-storage](https://sbox.cool/tools/network-storage) to get your `ProjectId` and `ApiKey`.

```csharp
public static class NetworkStorageConfig
{
    public const string ProjectId = "YOUR_PROJECT_ID";
    public const string ApiKey = "sbox_ns_YOUR_KEY";

    public static void Initialize()
    {
        NetworkStorage.Configure( ProjectId, ApiKey );
    }
}
```

Call `NetworkStorageConfig.Initialize()` once at game startup (e.g., in `GameManager.OnStart`).

### Endpoint Standards - Quick Reference

**Client should only send**: Item IDs, Quest IDs, slot indices, target player IDs, option choices, bounded quantities (validated server-side).

**Server looks up**: Costs/prices, XP rewards, drop rates, cooldown timers, requirements, crafting recipes, damage values, any balancing constant.

**Critical rule**: Never use `input.cost`, `input.price`, `input.xpReward`, or any client-supplied economic value in endpoints. Always look up costs and rewards from Game Values tables.

**Endpoint step flow**: READ player data -> LOOKUP from Game Values -> CONDITION check requirements -> TRANSFORM compute derived values -> WRITE changes with audit trail -> RESPONSE

---

## Revision System

The revision system tracks which game version players are running and helps manage version mismatches during live play. When you publish a new game revision on s&box, players running older versions can be detected and notified.

### Two Enforcement Modes

Configure enforcement mode in **Project Settings > Revisions** on the sbox.cool dashboard:

| Mode | Behavior |
|------|----------|
| **Allow Continue** | Players see a notification but can keep playing indefinitely. Best for casual games or when interrupting sessions would be disruptive. |
| **Force Upgrade** | Players get a grace period countdown, then the server blocks saves or all requests. Best for competitive games or when version consistency is critical. |

### Server-Side Configuration (Dashboard)

In **Project Settings > Revisions**:

- **Enforcement Mode**: Choose between Allow Continue or Force Upgrade
- **Grace Period** (Force Upgrade only): How long players can keep playing after a new version is published (5 min to 24 hours, or disabled for immediate enforcement)
- **Post-Grace Action** (Force Upgrade only): What happens when grace expires
  - `block_writes`: Player can still read data but cannot save
  - `block_all`: All endpoint requests are blocked
- **Show New Version Banner** (Allow Continue only): Display a persistent banner about the update
- **Show Update Options**: Whether to show Create Session / Join Lobby buttons
- **Custom Notification Message**: Optional message shown to players

### Client-Side Configuration

All client-side features are **opt-in**. The built-in UI is disabled by default.

```csharp
// ═══════════════════════════════════════════════════════════════════
// OPTION 1: Enable the built-in popup UI
// ═══════════════════════════════════════════════════════════════════

// IMPORTANT: Set the root panel so the popup knows where to attach
// Do this once when your HUD is created (e.g., in your GameManager or Hud class)
NetworkStorageOutdatedUI.RootPanel = myHudPanel; // or Game.RootPanel, your Hud, etc.

// Enable the default revision warning popup
NetworkStorage.RevisionSettings.ShowDefaultMessage = true;

// Show popup only once per session (default: true)
NetworkStorage.RevisionSettings.ShowOnlyOnce = true;

// Auto-open popup when outdated is detected (default: true)  
NetworkStorage.RevisionSettings.AutoOpenOnOutdated = true;

// Allow SPACE key to close the popup (default: true)
NetworkStorage.RevisionSettings.AllowSpaceToClose = true;

// ═══════════════════════════════════════════════════════════════════
// OPTION 2: Use your own custom UI (recommended for polished games)
// ═══════════════════════════════════════════════════════════════════

// Keep the default UI disabled
NetworkStorage.RevisionSettings.ShowDefaultMessage = false;

// Subscribe to the event and show your own UI
NetworkStorage.OnRevisionOutdated += ( data ) =>
{
    Log.Info( $"Outdated: rev {data.CurrentRevisionId} -> {data.LatestRevisionId}" );
    
    // Check enforcement mode to decide UI style
    var mode = NetworkStoragePackageInfo.EnforcementMode;
    
    if ( mode == RevisionEnforcementMode.AllowContinue )
    {
        // Show a gentle notification - player can dismiss and keep playing
        MyUI.ShowInfoBanner( "A new version is available!" );
    }
    else
    {
        // Show a warning with countdown
        var remaining = NetworkStoragePackageInfo.GraceRemainingMinutes;
        MyUI.ShowWarning( $"Update required in {remaining} minutes" );
    }
};


// ═══════════════════════════════════════════════════════════════════
// OPTION 3: Just track the status, no UI at all
// ═══════════════════════════════════════════════════════════════════

// Query revision status anytime
if ( NetworkStoragePackageInfo.IsOutdatedRevision )
{
    var mode = NetworkStoragePackageInfo.EnforcementMode;
    var graceExpired = NetworkStoragePackageInfo.GraceExpired;
    var message = NetworkStoragePackageInfo.RevisionMessage;
    
    // Do whatever you want with this information
}
```

### Built-in UI Events

When using the built-in popup (`ShowDefaultMessage = true`), subscribe to these events to handle player actions:

```csharp
// Player clicked "Continue Playing" (Allow Continue mode only)
NetworkStorageOutdatedUI.OnContinuePlaying += () =>
{
    Log.Info( "Player chose to continue on old version" );
};

// Player clicked "Create New Session"
NetworkStorageOutdatedUI.OnCreateNewGame += () =>
{
    // Disconnect from current game and create a fresh session
    Game.Disconnect();
    CreateNewLobby();
};

// Player clicked "Join New Lobby"  
NetworkStorageOutdatedUI.OnJoinNewLobby += () =>
{
    // Show lobby browser filtered to current-revision lobbies
    ShowLobbyBrowser( filterToCurrentRevision: true );
};

// Player dismissed the popup (Force Upgrade mode only)
NetworkStorageOutdatedUI.OnDismiss += () =>
{
    Log.Info( "Player dismissed revision warning" );
};
```

### Programmatic UI Control

```csharp
// Open the built-in popup manually (requires parent panel)
NetworkStorageOutdatedUI.Open( myHudPanel );

// Close the popup
NetworkStorageOutdatedUI.Close();

// Check if popup is currently visible
if ( NetworkStorageOutdatedUI.IsOpen )
{
    // ...
}
```

### Available Status Properties

Query `NetworkStoragePackageInfo` for current revision state:

| Property | Type | Description |
|----------|------|-------------|
| `IsOutdatedRevision` | bool | True when running an older revision |
| `EnforcementMode` | RevisionEnforcementMode | `ForceUpgrade` or `AllowContinue` |
| `GraceRemainingMinutes` | int? | Minutes left in grace period |
| `GraceExpired` | bool | True when grace period has ended |
| `RevisionAction` | string | Current action: `"warn"`, `"block_writes"`, `"block_all"` |
| `RevisionMessage` | string | Server-provided message for players |
| `CurrentRevisionId` | long? | The revision this game is running |
| `ServerCurrentRevision` | long? | The latest revision on the server |
| `PolicyShowUpdateOptions` | bool | Whether server wants update buttons shown |
| `PolicyShowPopupOnce` | bool | Whether popup should only show once |

### Event Data

The `OnRevisionOutdated` event provides a `RevisionOutdatedData` struct:

| Property | Type | Description |
|----------|------|-------------|
| `CurrentRevisionId` | long | The client's running revision |
| `LatestRevisionId` | long | The latest revision on server |
| `RevisionOutdated` | bool | True when outdated |
| `EnforcementMode` | RevisionEnforcementMode | Server's enforcement mode |
| `GraceExpired` | bool | True when grace has expired |
| `GraceRemainingMinutes` | int? | Minutes remaining |
| `Message` | string | Custom message from server |
| `Action` | string | Current enforcement action |
| `ShowUpdateOptions` | bool | Show Create/Join buttons |
| `ShowPopupOnce` | bool | Only show popup once |
| `TimeRemaining` | int | Computed seconds until grace expires |
| `IsGraceExpired` | bool | Computed from timestamps |
| `Source` | string | Detection source |
| `Reason` | RevisionOutdatedReason | Why event fired |

### Testing in the Editor

Test your revision UI without publishing a new version:

```csharp
// Show the full UI + fire events (bypasses ShowOnlyOnce)
NetworkStorage.TestShowRevisionOutdatedMessage( parentPanel );

// Fire the event only (test custom hooks without UI)
NetworkStorage.TestFireRevisionOutdated();
```

### Lobby Metadata

Stamp revision tracking metadata when creating lobbies:

```csharp
var meta = NetworkStorage.BuildLobbyMetadata(
    migrationRevision: null,
    sourceLobbyId: null
);
lobby.SetMetadata( meta );

// Check lobby freshness
bool isFresh = NetworkStorage.IsLobbyOnCurrentRevision( metadata );
bool isStale = NetworkStorage.IsLobbyStale( metadata );
```

### Full Integration Example

```csharp
public partial class MyGame : GameManager
{
    private Panel _hud;

    public override void ClientJoined( IClient cl )
    {
        base.ClientJoined( cl );
        
        if ( cl.IsHost )
        {
            InitializeNetworkStorage();
        }
    }

    private void InitializeNetworkStorage()
    {
        // Configure Network Storage
        NetworkStorage.Configure( "my-project-id", "sbox_ns_mykey" );
        
        // IMPORTANT: Set the root panel for the popup UI
        // This must be done before any revision events fire
        _hud = new MyHudPanel(); // Your game's HUD
        NetworkStorageOutdatedUI.RootPanel = _hud;
        
        // Enable built-in UI
        NetworkStorage.RevisionSettings.ShowDefaultMessage = true;
        
        // Handle player actions from the popup
        NetworkStorageOutdatedUI.OnCreateNewGame += HandleCreateNewGame;
        NetworkStorageOutdatedUI.OnContinuePlaying += HandleContinuePlaying;
        
        // Initialize revision handler (subscribes to status changes)
        NetworkStorageRevisionHandler.Initialize();
    }

    private void HandleCreateNewGame()
    {
        // Save any unsaved progress first
        SaveProgress();
        
        // Disconnect and restart
        Game.Disconnect();
        
        // The player will rejoin on the new revision after game update
    }

    private void HandleContinuePlaying()
    {
        // Player chose to keep playing on old version
        // Maybe show a small reminder in the corner
        ShowPersistentUpdateReminder();
    }
}
```

### Security Notes

- **Do NOT trust client-reported revision status.** The server always computes the authoritative status.
- Revision enforcement is server-side. Even modified clients cannot bypass `block_writes` or `block_all` — the server rejects the requests.
- The `_revisionStatus` block in endpoint responses is the source of truth.

### Warnings

- Test thoroughly before enabling Force Upgrade mode in production
- `block_all` can lock players out entirely — use `block_writes` if you want graceful degradation
- The sync tool must push package info before revision detection works
- All client-side features are opt-in; nothing is enabled by default
