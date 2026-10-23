# <img src="Assets/Branding/icon-512.png" width="48" height="48" alt="Network Storage icon: shaded lime cube with a smug face and storage port" /> s&box Network Storage

**Client library and editor tools for an s&box game backend.** Call server endpoints, load game
values and sync your backend's rules from the editor. Works with the open-source
[self-hosted server](https://github.com/sbox-cool/sbox-network-storage-server) and the managed
[sbox.cool](https://sbox.cool/tools/network-storage) service.

[Install](https://sbox.game/sboxcool/network-storage) · [Website](https://sboxns.com) ·
[Docs](https://sboxns.com/docs/) · [Discord](https://sboxns.com/discord-invite) ·
[Managed service wiki](https://sbox.cool/wiki/network-storage-v3)

![s&box Network Storage: game values, server-side checks and versioned builds](https://sboxns.com/images/marketing/card-hero.png)

## Quick start

1. **Install** from **Editor > Library Manager**: search for *Network Storage* by **sboxcool**.
2. **Configure** in **Editor > Network Storage > Setup** with your project ID, public key, secret
   key and (for a self-hosted server) base URL. The secret key stays in `Editor/` and never ships.
3. **Call your server** from game code:

```csharp
NetworkStorage.Configure( "your_project_id", "sbox_ns_your_public_key", "https://my-game.sboxns.com" );

var player = await NetworkStorage.CallEndpoint( "load-player" );
var result = await NetworkStorage.CallEndpoint( "mine-ore", new { ore_id = "iron", kg = 5.0f } );
if ( result.HasValue )
    Log.Info( $"Currency: {result.Value.Int( "currency", 0 )}" );

var values = (await NetworkStorage.GetGameValues()).Value; // every group and table, one request
```

No server yet? `sbox-ns quickstart "My Game"` on a self-hosted server prints the IDs, keys and the
`Configure(...)` line: [sboxns.com/install](https://sboxns.com/install/).

## What's in it

| | |
| --- | --- |
| **Runtime client** | Endpoints, game values, documents, save slots, analytics, retries and encrypted requests. [Runtime API](docs/runtime-api.md) |
| **Sync Tool** | Collections, endpoints and workflows as YAML in your project, with diff, push and pull; generates typed code for them. [Sync Tool](docs/sync-tool.md) |
| **Setup window** | Enter and test credentials and the server URL. [Getting started](docs/getting-started.md) |
| **Debugging** | `NetLog` ring buffer for in-game debug panels, `SaveStateTracker` for HUD save state. |
| **Agent tools** | `ns_*` tools for coding agents through s&box's `sbox-cli` MCP bridge. [Agent tools](docs/agent-tools.md) |

Copyable patterns are in [Examples/](Examples/).

## Docs

- [Getting started](docs/getting-started.md): install, update, configure, API root, generated code,
  dedicated servers, security model.
- [Runtime API](docs/runtime-api.md): calls, game values, encrypted requests and key pinning,
  server compatibility status, helpers.
- [Sync Tool](docs/sync-tool.md): YAML source files and the push/pull workflow;
  [source-authoring.md](source-authoring.md) for the full model.
- [Agent tools](docs/agent-tools.md): the in-engine `network_storage` toolset.
- Maintainers: [releasing](docs/releasing.md), [compat gate](docs/compat-gate.md),
  [compatibility certification](docs/compatibility-certification.md).

## Compatibility

The library and server release independently and are both long-term supported. Public methods
and endpoints are never silently renamed, removed or narrowed, and servers keep accepting older
libraries by default. Each library, server, engine and database pairing is certified with real
evidence: [compatibility certification](docs/compatibility-certification.md).

## License

MIT, see [LICENSE](LICENSE). sbox.cool and sboxcool.com are run by the same group;
`api.sboxcool.com` is the long-term managed API host.
