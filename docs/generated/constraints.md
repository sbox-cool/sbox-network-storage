# Constraints & Limits

## Naming Rules

| Entity | Pattern | Description |
|--------|---------|-------------|
| Collection names | `/^[a-z0-9_]+$/` | Lowercase letters, digits, underscores |
| Endpoint slugs | `/^[a-z0-9-]+$/` | Lowercase letters, digits, hyphens |
| Workflow IDs | `/^[a-z0-9-]+$/` | Lowercase letters, digits, hyphens |

## Maximums

| Limit | Value |
|-------|-------|
| Steps per endpoint | 20 |
| Read/lookup/filter steps per endpoint | 10 |
| Endpoints per project | 20 |
| Collections per project | 50 |
| Records scanned in filter | 500 |

## Valid Enums

### Step Types
`read`, `write`, `transform`, `condition`, `lookup`, `filter`, `workflow`

### Transform Operations
`add`, `subtract`, `set`, `multiply`, `divide`, `append`, `remove`

### Condition Operators
`eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `contains`, `exists`, `not_exists`

### onFail Values
`error`, `skip`

### Collection Types
`per-steamid`, `global`

### Access Modes
`public`, `private`

### HTTP Methods
`GET`, `POST`

### Data Sources
`api_then_json`, `api_only`, `json_only`

## API Keys

| Key Type | Prefix | Ships with game? |
|----------|--------|------------------|
| Public Key | `sbox_ns_` | Yes |
| Secret Key | `sbox_sk_` | **Never** |

## Required Fields per Step Type

| Step Type | Required Fields |
|-----------|----------------|
| `read` | `collection`, `as` |
| `write` | `collection` |
| `transform` | `field`, `operation`, `value` |
| `condition` | `field`, `operator`, `value`*, `onFail` |
| `lookup` | `source`, `table`, `key`, `value`, `as` |
| `filter` | `source`, `field`, `operator`, `value`, `as` |
| `workflow` | `workflow` |

*`value` not required for `exists`/`not_exists` operators.

## Response Pattern

- Server returns HTTP 200 with `{ ok: false, error: { code, message } }` for rejections
- Non-200 status codes cause s&box Http API to throw and lose response body
