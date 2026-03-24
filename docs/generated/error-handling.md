# Error Handling & Rejection Guide

## How Rejections Work

```
Client calls endpoint → Server runs steps → Condition fails
    → HTTP 200 with { ok: false, error: { code, message } }
    → Client returns null → Optimistic update reverts → UI shows error
```

**Why HTTP 200?** s&box's `Http.RequestStringAsync` throws on 4xx/5xx, losing the response body. HTTP 200 with `ok: false` lets the client read the full error.

## Response Formats

### Success
```json
{ "ok": true, "success": true, "xp": 240, "currency": 500 }
```

### Rejection
```json
{ "ok": false, "error": { "code": "BACKPACK_FULL", "message": "Not enough space." }, "severity": "warning" }
```

### System Error
```json
{ "ok": false, "error": { "code": "INTERNAL_ERROR", "message": "An internal error occurred." } }
```

## Common Error Codes

| Code | Meaning |
|------|---------|
| `UNAUTHORIZED` | Invalid/missing API key |
| `PROJECT_DISABLED` | Project disabled on sbox.cool |
| `QUOTA_EXCEEDED` | Monthly usage limit hit |
| `SCHEMA_VALIDATION_FAILED` | Data doesn't match schema |
| `ENDPOINT_NOT_FOUND` | Endpoint slug doesn't exist on server |
| `ENDPOINT_DISABLED` | Endpoint exists but `enabled: false` |
| `CONDITION_FAILED` | Generic condition failure |
| `INTERNAL_ERROR` | Server crash — check logs |
| `RATE_LIMIT_DAILY` | Rate limit exceeded |
| `INVALID_JSON` | Request body not valid JSON |
| `INVALID_BODY` | Request body shape wrong |

## Optimistic Update Pattern

```csharp
// 1. Apply optimistic update
CurrentOreKg += kg;
// 2. Call server
var result = await NetworkStorage.CallEndpoint("mine-ore", input);
if (result.HasValue)
    Apply(result.Value);  // 3a. Success
else
    CurrentOreKg -= kg;    // 3b. Revert
```
