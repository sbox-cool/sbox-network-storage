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
|———|——————-|
| `get_network_storage_guide` | Complete NS v3 guide: setup, CallEndpoint/GetDocument/GetGameValues usage, endpoint JSON pipeline (read/condition/transform/write/lookup/filter steps), collections, game values, workflows, error codes, and production C# patterns. |
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

## Endpoint Standards - Quick Reference

**Client should only send**: Item IDs, Quest IDs, slot indices, target player IDs, option choices, bounded quantities (validated server-side).

**Server looks up**: Costs/prices, XP rewards, drop rates, cooldown timers, requirements, crafting recipes, damage values, any balancing constant.

**Critical rule**: Never use `input.cost`, `input.price`, `input.xpReward`, or any client-supplied economic value in endpoints. Always look up costs and rewards from Game Values tables.

**Endpoint step flow**: READ player data -> LOOKUP from Game Values -> CONDITION check requirements -> TRANSFORM compute derived values -> WRITE changes with audit trail -> RESPONSE

## Installation

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
