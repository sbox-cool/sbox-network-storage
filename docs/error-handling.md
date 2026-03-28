# Network Storage — Error Handling & Rejection Guide

> **Note:** For the most up-to-date documentation, visit https://sbox.cool/wiki/network-storage-v3 — these repo docs may be outdated.

How endpoint rejections, workflow validation failures, and network errors flow through the system.

## How Rejections Work

When a player action violates a server-side rule (backpack full, not enough currency, wrong tier), the flow is:

```
Client calls endpoint
    ↓
Server runs endpoint steps
    ↓
Condition/workflow check fails
    ↓
Server returns HTTP 200 with { ok: false, error: { code, message } }
    ↓
Client ParseResponse detects ok: false → returns null
    ↓
Calling code sees null → reverts optimistic update
    ↓
OnEndpointRejection event fires → UI shows error banner
```

**Why HTTP 200?** s&box's `Http.RequestStringAsync` throws exceptions on HTTP 4xx/5xx, which loses the response body. By returning HTTP 200 with `ok: false`, the client can read the full error JSON including the error code and human-readable message. Users can still set `"status": 403` in their workflow `onFail` if they explicitly want a non-200 status code.

## Server Response Formats

### Success
```json
{
  "ok": true,
  "success": true,
  "xp": 240,
  "currency": 500,
  "currentOreKg": 50,
  "backpackCapacity": 100
}
```

### Condition/Workflow Rejection
```json
{
  "ok": false,
  "error": {
    "code": "BACKPACK_FULL",
    "message": "Not enough backpack space. Current: 95kg / 100kg."
  },
  "severity": "warning"
}
```

### System Error (rare — endpoint misconfiguration, crashes)
```json
{
  "ok": false,
  "error": {
    "code": "INTERNAL_ERROR",
    "message": "An internal error occurred while executing this endpoint."
  }
}
```

## Client-Side Error Detection

`NetworkStorage.CallEndpoint` returns `JsonElement?` — `null` means any kind of failure.

The client's `ParseResponse` detects errors via multiple patterns (in order):
1. `ok: false` in response body (primary — all condition/workflow rejections)
2. `error` object without `ok: true` (fallback for edge cases)
3. `error` as a string (simple error format)
4. `status >= 400` in response body (forwarded HTTP status)
5. Non-JSON responses (HTML error pages from proxies/CDN)

All detected errors are logged to the console:
```
[NetworkStorage] mine-ore: BACKPACK_FULL — Not enough backpack space. Current: 95kg / 100kg.
```

## Optimistic Updates with Revert

The recommended pattern for game actions:

```csharp
public async Task ReportMining( string oreId, float kg )
{
    if ( kg <= 0f ) return;

    // 1. Optimistic local update (UI responds instantly)
    Ores[oreId] = Ores.GetValueOrDefault( oreId, 0f ) + kg;
    CurrentOreKg += kg;

    // 2. Server validates and stores authoritatively
    var result = await TrackedEndpointCall( "mine-ore", new { ore_id = oreId, kg } );

    if ( result.HasValue )
    {
        // 3a. Success — apply server-authoritative values
        Apply( result.Value );
    }
    else
    {
        // 3b. Rejected — revert the optimistic update
        Ores[oreId] = Ores.GetValueOrDefault( oreId, 0f ) - kg;
        if ( Ores[oreId] <= 0.01f ) Ores.Remove( oreId );
        CurrentOreKg = MathF.Max( 0f, CurrentOreKg - kg );
    }
}
```

The `SaveStateTracker.CallAndApply` helper wraps this pattern:

```csharp
await tracker.CallAndApply( "mine-ore", input,
    applyOptimistic: () => { CurrentOreKg += kg; },
    applyServer: (response) => { Apply( response ); },
    revert: () => { CurrentOreKg -= kg; }
);
```

## Surfacing Errors to Players

### The OnEndpointRejection Event

`PlayerDataManager.OnEndpointRejection` fires whenever an endpoint call is rejected. Subscribe to show UI feedback:

```csharp
// In your UI component
protected override void OnStart()
{
    PlayerDataManager.OnEndpointRejection += OnRejection;
}

protected override void OnDestroy()
{
    PlayerDataManager.OnEndpointRejection -= OnRejection;
}

private void OnRejection( string message )
{
    // message is "ERROR_CODE: Human readable message"
    // Strip the code prefix for display:
    var colonIdx = message.IndexOf( ": " );
    var display = colonIdx > 0 ? message[( colonIdx + 2 )..] : message;

    ShowErrorBanner( display );
    Sound.Play( "generic_error" );
}
```

### Error Message Templates

Workflow `errorMessage` fields support `{{template}}` syntax. The server resolves them against the bound context before sending:

```json
{
  "onFail": {
    "reject": true,
    "errorCode": "BACKPACK_FULL",
    "errorMessage": "Not enough space. Current: {{current_kg}}kg / {{capacity}}kg."
  }
}
```

The client receives the resolved message: `"Not enough space. Current: 95kg / 100kg."`

## Workflow onFail Options

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `reject` | bool | `true` | Short-circuit and return error to client |
| `errorCode` | string | `"CONDITION_FAILED"` | Error code in response (e.g. `"BACKPACK_FULL"`) |
| `errorMessage` | string | `"Condition check failed."` | Human-readable message (supports `{{templates}}`) |
| `severity` | string | `null` | `"warning"` or `"critical"` — logged for monitoring |
| `status` | number | `200` | HTTP status code (default 200 so s&box client can read body) |
| `flag` | bool | `false` | Log a player flag for anti-cheat review |
| `webhook` | bool | `false` | Send Discord webhook notification on failure |
| `clamp` | bool | `false` | Cap the input value instead of rejecting |

### Webhook Behavior

By default, condition/workflow rejections do **NOT** fire Discord webhooks. These are expected game logic (player tried to mine with full backpack, tried to buy without enough currency, etc.).

To opt-in for specific workflows (e.g., anti-cheat):
```json
{
  "onFail": {
    "reject": true,
    "webhook": true,
    "flag": true,
    "severity": "critical",
    "errorCode": "SUSPICIOUS_VALUE",
    "errorMessage": "Value out of expected range"
  }
}
```

Unexpected errors (crashes, endpoint misconfiguration, 5xx) always fire webhooks.

## Common Error Codes

| Code | Meaning | Typical Cause |
|------|---------|---------------|
| `BACKPACK_FULL` | Inventory at capacity | Player mined ore with full backpack |
| `NOT_ENOUGH_ORE` | Insufficient ore for action | Player tried to sell/drop more than they have |
| `NOT_ENOUGH_CURRENCY` | Insufficient funds | Player tried to buy something they can't afford |
| `PHASERDEX_REQUIRED` | Missing required upgrade | Player tried to mine ore above their tier |
| `CONDITION_FAILED` | Generic condition failure | Catch-all for conditions without a custom code |
| `INTERNAL_ERROR` | Server crash | Endpoint misconfiguration or bug — check server logs |

## Debugging Endpoint Issues

### Console Logging

Every endpoint call logs the full raw JSON response:
```
[NetworkStorage] mine-ore → {"ok":false,"error":{"code":"BACKPACK_FULL","message":"Not enough space..."}}
```

### NetLog

The structured `NetLog` captures all requests, responses, and errors:
```csharp
foreach ( var entry in NetLog.Entries )
    Log.Info( $"[{entry.Kind}] {entry.Tag}: {entry.Message}" );
```

### Common Issues

**"Response status code does not indicate success: 400"**
The server returned HTTP 400, and `Http.RequestStringAsync` threw instead of returning the body. This means the endpoint or workflow has an explicit `"status": 400` (or higher) in its `onFail`. Remove the `status` field or set it to `200` so the client can read the error JSON.

**Condition always passes (no rejection)**
Check that workflow condition fields use `{{template}}` syntax (`"field": "{{new_ore_total}}"`) when the values come from bindings. Without `{{}}`, the field is treated as a dot-path context lookup, which may resolve to a different value.

**Error banner shows raw template text like `{{current_kg}}`**
The server needs to resolve `{{templates}}` in `errorMessage` before sending. This was fixed in the endpoint runner — ensure you're running the latest version.

**Optimistic update not reverting**
Make sure the endpoint call returns `null` on rejection. Check `ParseResponse` is detecting the error (look for `[NetworkStorage] slug: CODE — message` in the console). If you see the raw JSON with `ok: false` but no error log, the detection logic may need updating.

## Management API Errors (Sync Tool)

The Sync Tool uses `System.Net.Http.HttpClient` (allowed in editor context) to call the Management API. Unlike the runtime API, management endpoints return standard HTTP status codes with structured error bodies.

Authentication requires two headers on every request:

| Header | Value |
|--------|-------|
| `x-api-key` | `sbox_sk_...` (128-character secret key) |
| `x-public-key` | `sbox_ns_...` (public API key) |

When auth fails, the server returns HTTP 401 with a specific error code:

| Code | Meaning |
|------|---------|
| `MISSING_SECRET_KEY` | `x-api-key` header missing or wrong prefix |
| `MISSING_PUBLIC_KEY` | `x-public-key` header missing or wrong prefix |
| `INVALID_SECRET_KEY` | Secret key not recognized |
| `INVALID_PUBLIC_KEY` | Public key not recognized |
| `PROJECT_MISMATCH` | Key belongs to a different project |
| `KEY_DISABLED` | Key is disabled |
| `KEY_UPGRADE_REQUIRED` | Old key format -- regenerate from dashboard |
| `FORBIDDEN` | Key lacks required permission scope |

All 401 responses include an `expected` object showing what the server needs:

```json
{
  "ok": false,
  "error": "MISSING_SECRET_KEY",
  "message": "Missing or invalid x-api-key header. Must be a secret key (sbox_sk_*).",
  "expected": {
    "headers": {
      "x-api-key": "sbox_sk_... (128-character secret key)",
      "x-public-key": "sbox_ns_... (public API key)"
    }
  }
}
```

The `SyncToolApi` class reads `error` and `message` from the response and surfaces them in the editor console via `LastErrorCode` / `LastErrorMessage`.

## s&box HTTP Constraints

- **Only `Http.RequestStringAsync` is whitelisted** -- `System.Net.Http.HttpClient` is blocked by s&box's sandbox
- `Http.RequestStringAsync` **throws on HTTP 4xx/5xx** -- the response body is lost
- This is why condition rejections default to **HTTP 200** with `ok: false` -- so the client can read the error details
- If you explicitly set `"status": 403` in a workflow's `onFail`, the client will only see the exception message, not the error JSON
- `System.Net.Http.StringContent` IS allowed (needed for POST request bodies)
