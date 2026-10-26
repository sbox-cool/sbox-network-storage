# Runtime API

Everything game code calls. The generated per-member reference is at
[sboxns.com/docs/library/api](https://sboxns.com/docs/library/api/); copyable patterns are in
[Examples/](../Examples/).

## Calling the server

```csharp
if ( !NetworkStorage.IsConfigured )
    NetworkStorage.Configure( "your_project_id", "sbox_ns_your_public_key" );

// Endpoint without input (GET; POST when encrypted requests are required)
var player = await NetworkStorage.CallEndpoint( "load-player" );

// Endpoint with input (POST)
var result = await NetworkStorage.CallEndpoint( "mine-ore", new { ore_id = "iron", kg = 5.0f } );

// Endpoint URLs copied from the dashboard work too; the slug is extracted.
await NetworkStorage.CallEndpoint(
    "https://api.sboxcool.com/v3/endpoints/your_project_id/mine-ore?apiKey=sbox_ns_your_public_key",
    new { ore_id = "iron", kg = 5.0f } );

// Documents (dedicated servers attach +network_storage_secret_key automatically)
await NetworkStorage.SaveDocument( "player-data", Game.SteamId.ToString(), new { level = 2, xp = 100 } );
await NetworkStorage.UpdateDocument( "player-data", Game.SteamId.ToString(),
    NetworkStorageOperation.Increment( "xp", 50, source: "server", reason: "quest" ),
    NetworkStorageOperation.Set( "lastSeen", DateTimeOffset.UtcNow.ToUnixTimeSeconds() ) );
await NetworkStorage.DeleteDocument( "player-data", Game.SteamId.ToString() );

if ( result.HasValue )
{
    var currency = result.Value.Int( "currency", 0 );
}
```

| Member | Description |
| --- | --- |
| `Configure(projectId, apiKey, ...)` | Set credentials once at startup. See [getting started](getting-started.md#configure). |
| `IsConfigured` / `ApiRoot` | Whether `Configure` ran; the full API URL (`.../v3`, or `.../api` with `apiVersion` `api`). |
| `CallEndpoint(slugOrUrl, input?)` | Call an endpoint by slug or dashboard URL. Returns `JsonElement?`. |
| `TryGetLastEndpointError(...)` | The last error code and message for a call. Keys: an endpoint slug, `storage`, `storage-save`, `storage-update`, `storage-delete`, `storage-records`, `storage-record-create`, `storage-record-delete`, `storage-record-rename`, `query:{id}`, `game-values`, `CAPABILITY_UNAVAILABLE`, `INVALID_QUERY`, `INVALID_ENDPOINT`. |
| `GetGameValues()` | All game value groups and tables in one request. |
| `GetDocument(collectionId, documentId?)` | Read a document; defaults to the current player's Steam ID. |
| `SaveDocument` / `UpdateDocument` / `DeleteDocument` | Replace a document, apply `set`/`inc`/`push`/`pull`/`remove` operations, or delete one row. Public deletes need record deletes enabled on the server. |
| `ListRecords` / `CreateRecord` / `RenameRecord` / `DeleteRecord` | Multi-record save slots. |
| `TrackAnalyticsEvent(eventType, payload?)` | Custom Player Analytics event; the name must be listed in the project's analytics settings. |
| `NetworkStorageAnalytics.Warning/Error/SessionStart/SessionEnd` | Warnings, caught exceptions and session boundaries on the player timeline. |
| `LibraryVersion` / `MinServerVersion` | Library SemVer (`1.2.0`) and the oldest supported self-hosted server (`0.4.0`, metadata only). |
| `ServerVersion` / `LibraryStatus` / `OnLibraryStatusChanged` / `HasCapability(name)` | What the server reported; see [server compatibility](#server-compatibility-status). |
| `SendAsync(NetworkStorageRequest)` | Send any `/v3` (or `api.root`) request and get a `NetworkStorageResult` back. Never throws; see [generic requests](#generic-requests). |

### Retries and errors

Rate-limited calls (HTTP 429) are retried at most twice, with the total wait capped at 5 seconds,
using the server's `Retry-After` header, then `retryAfterSeconds`, then `retryAfterSec` in the body. GET requests
always retry; other methods only when the server marks the response `retrySafe`. A GET that gets a 503 with
`Retry-After` is retried the same way. A call that still fails is reported as `RATE_LIMITED` through `TryGetLastEndpointError`.

Failed replies keep the server's error code. A JSON error code (`{"ok":false,"error":{"code":...}}`, or a bare
`"error":"CODE"` string when the value is code-shaped) is reported as-is, with the server's message. A non-2xx
reply without a JSON code is reported with the call's fallback code (`HTTP_ERROR` for endpoints, `REQUEST_FAILED`
for storage, queries and game values; `NOT_FOUND` for a missing document), and the HTTP status appears in the
message (for example `HTTP 413 RequestEntityTooLarge`). 429 without a code is `RATE_LIMITED`. Transport failures are `REQUEST_FAILED`.

A document read returns the stored JSON exactly as saved, including top-level arrays and keys named `ok`, `body`
or `error`. An endpoint reply is unwrapped to its `body` only when it has `ok: true` and a `timing` object.

A security refusal (`SECURITY_*`) is retried once after a config refresh only when the server marks it `retrySafe: true`.

Storage writes send `x-ns-write-mode` (`replace` for `SaveDocument`, `ops` for `UpdateDocument`) so the server does not
infer the mode from the body. Every non-GET storage or `SendAsync` request sends an `Idempotency-Key` that is reused
across retries of the same logical call; every request sends an `X-Correlation-ID`.

A listener on a library event that throws is logged; the throw does not reach the request that raised the event. Later
listeners on the same event are skipped, so keep listeners small.

### Generic requests

`NetworkStorage.SendAsync(new NetworkStorageRequest { ... })` sends one request to the storage API and returns a
`NetworkStorageResult` (`Ok`, `Status`, `Code`, `Message`, `Body`, `RetryAfterSeconds`, `RequestId`). It never throws.

- `Method`: `GET` (default), `POST`, `PUT`, `PATCH` or `DELETE`. `Path` starts with `/`, for example `/storage/{project}/{collection}/{key}`.
- `Query`: extra query parameters. `apiKey`, `revisionTarget`, `includeStaged` and `secret-key` are reserved.
- `Body`: JSON-serialised body for non-GET requests.
- `Auth`: `Player` (default; the signed-in player's token), `Public` (project key only) or `DedicatedSecret` (dedicated server secret).
- `IdempotencyKey`: optional, 1 to 128 visible characters; generated when omitted.
- `UseServerRoot`: send to the server's `api.root` (path only, from server info) instead of the `ApiRoot` URL.

Refusals before a request is sent use codes `INVALID_REQUEST`, `INVALID_METHOD`, `INVALID_PATH`, `NOT_CONFIGURED`,
`API_ROOT_UNAVAILABLE`, `DEDICATED_SECRET_REQUIRED` and `DEDICATED_SECRET_UNAVAILABLE`. `/endpoints/...` is refused
with `USE_CALL_ENDPOINT`; use `CallEndpoint`. `/manage/...` is refused with `UNSUPPORTED_PATH`.
These checks also apply to percent-encoded route segments. Dot segments and encoded
separators, queries, fragments or control characters are refused with `INVALID_PATH`.

Calls through the host (proxy mode) wait at most 30 seconds for the host's reply. A timeout is reported as `PROXY_TIMEOUT`; any other proxy failure, including the host changing or disconnecting while the call is in flight, is reported as `PROXY_FAILED`.

### Analytics batching

When the server advertises `analytics-batch`, events are queued and sent together (up to 50 events
or 256 KiB per request; the first event waits at most 2 seconds). Servers without the capability
get one request per event. Rejected events, events over 32 KiB and events beyond the 500-event
queue are counted in a `[NetworkStorage] Dropped N analytics event(s)` warning, at most once every
30 seconds. An abrupt process exit can lose in-flight events.

## Game values

Shared numbers the game and server both read: tuning constants, named groups and typed tables.
Author them in the server's owner console or over its API; see the server's
[game values guide](https://github.com/sbox-cool/sbox-network-storage-server/blob/main/docs/game-values.md)
for revisions, snapshots and a worked anti-exploit example.

```csharp
var gv = (await NetworkStorage.GetGameValues()).Value;
var combat = gv.GetProperty( "groups" ).GetProperty( "combat" ).GetProperty( "values" );
int zombieHp = combat.Int( "zombie_hp_per_level" ) * zombieLevel;
var weapons = gv.GetProperty( "tables" ).GetProperty( "weapons" )
    .ReadList( "rows", r => (Id: r.Str( "weapon_id" ), Damage: r.Int( "damage" ), Model: r.Str( "model" )) );
```

`GetGameValues()` is one `GET {ApiRoot}/values/{projectId}`. The library keeps no offline copy:
load once at startup and look up locally afterwards. Game code cannot write game values. Endpoints
read the same numbers with `{{values.combat.zombie_hp_per_level}}` and `lookup` steps with
`source: values`.

A build keeps its published snapshot only when the project has an explicit revision policy and
its revision is retained inside the support window; otherwise it reads live values.

`GetGameValues` is a public, unsigned GET outside the endpoint path: neither the encryption pin
nor a refused config covers it, and a proxy can read or change it. Do not put secrets in game
values.

## Encrypted requests

The server owner picks the mode (`off`, `v1`, `v2-preferred`, `v2-required`) and the game follows
the server's signed security config. v2 seals each request to a server-generated key that can
rotate without a game update.

- With encryption required, a call without input is still sent as an encrypted POST. The host's
  `CallEndpointAs` path builds no envelope, so a no-input call on behalf of another player is
  refused locally with `ENCRYPTION_REQUIRES_POST`.
- **Pin the signing key** so a proxy or swapped config cannot substitute its own: pass
  `securityKeyFingerprint` as the seventh `Configure` argument, or enter it in the Setup window.
  Copy it from `sbox-ns security status` ("Security signing key"); spaces, colons and case are
  ignored. With a pin, endpoint calls are refused with `ENCRYPTION_KEY_UNVERIFIED` until a config
  signed by that key loads.
- **Without a pin**, the first signing key seen per API root is trusted and saved in the game's
  data folder (`network-storage-signing-key-trust.json`). A later change warns and is refused with
  `ENCRYPTION_SIGNING_KEY_CHANGED`; the last trusted config is kept. Verify the server out of band
  before setting a new pin or resetting the trust store. Unpinned games that never receive a
  trusted config stay on v1.
  The trust store checks each saved entry on its own: an unusable entry is logged and skipped, and the others keep their trust. A trusted signing key may have any RSA modulus of at least 128 bytes (RSA-1024 and larger). Encryption v2 still requires RSA-2048 keys.
- A config that fails verification refuses every endpoint call, plain or encrypted. Calling
  `Configure` with a different pin, base URL, API version or project drops the held config and
  verifies again.
- **Clock skew.** Encrypted requests carry a unix time that the server accepts only inside its replay
  window (30 to 600 seconds, default 120). The library learns the server's clock from the `Date` header of
  its responses (a response with an `Age` header is cached, so it is ignored) and from the `serverTime`
  in an `ENCRYPTED_REQUEST_STALE` refusal, and builds request ids from that clock. A stale refusal is
  retried once with a fresh id, so a game whose client clock is wrong still makes its calls. The learned
  offset is clamped to one day, and differences under 5 seconds are treated as zero.

## Server compatibility status

The library reads `x-ns-server-version` and `x-ns-library-status` from responses and
`capabilities` from `/v3/server-info`:

| Member | Meaning |
| --- | --- |
| `ServerVersion` | Last version the server reported, or `null`. |
| `LibraryStatus` | `supported`, `outdated`, `deprecated`, `unsupported`, `blocked` or `unknown`. |
| `OnLibraryStatusChanged` | Raised when the status changes. No player-facing UI is shown. |
| `HasCapability(name)` | `true` unless the server lists capabilities and the name is missing. |

A `426 LIBRARY_UNSUPPORTED` or `LIBRARY_BLOCKED` response is surfaced through
`TryGetLastEndpointError` and logged once. In the editor, Setup and the Sync Tool show a banner for
`outdated`/`unknown` (or a newer published revision), a once-per-session dialog for `deprecated`,
and refuse **Push All** for `unsupported`/`blocked`; Pull and Test Live keep working. Nothing here
runs in play mode or published games.

Runtime, Sync Tool and Python sync requests send `x-ns-library-version`. Editor tools also send
`x-ns-client-type: editor` and, when the installed `.version` has a positive revision,
`x-ns-library-revision`. Runtime never reads `.version`. These headers are self-reported
diagnostics, not authentication.

## Helpers

### JSON

```csharp
var name   = data.Str( "playerName", "Unknown" );   // JsonHelpers.GetString( data, ... )
var level  = data.Int( "level", 1 );                // missing keys and "5" strings handled
var speed  = data.Float( "speed", 1.0f );
var active = JsonHelpers.GetBool( data, "active", true );
var upgrades = data.ReadStringList( "purchasedUpgrades" );
var ores     = data.ReadDictionary( "ores", v => (float)v.GetDouble() );
var items    = data.ReadList( "rows", row => new ItemInfo( row.Str( "id" ), row.Str( "name" ), row.Int( "tier" ) ) );
```

### SaveStateTracker

Wraps endpoint calls with state for HUD feedback:

```csharp
var tracker = new SaveStateTracker();
var result = await tracker.Call( "mine-ore", new { ore_id = "iron", kg = 5 } ); // State: Saved or Error; IsBusy while in flight

await tracker.CallAndApply( "sell-ore", input,
    applyOptimistic: () => { /* update local state */ },
    applyServer: data => { /* apply authoritative response */ },
    revert: () => { /* undo on failure */ } );
```

`Call` sets `State` to `Error` and rethrows when the endpoint throws; `CallAndApply` runs `revert` before rethrowing. A call that completes after `Reset()` no longer changes `State`.

### NetLog

Ring buffer of every Network Storage request, response and error, for debug panels:

```csharp
foreach ( var entry in NetLog.Entries ) { /* entry.Time, entry.Kind, entry.Tag, entry.Message */ }
NetLog.Info( "my-system", "Custom log message" );
var version = NetLog.Version; // increments on every add or clear
```
