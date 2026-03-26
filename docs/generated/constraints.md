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
| Steps per workflow | 20 |
| Workflow nesting depth | 8 |
| Read/lookup/filter steps per execution | 10 |

## Valid Enums

### Step Types
`read`, `write`, `transform`, `condition`, `lookup`, `filter`, `workflow`, `delete`

### Write Operations (`ops`)
`set`, `inc`, `push`, `pull`, `remove`

### Transform Operations
`add`, `subtract`, `set`, `multiply`, `divide`, `append`, `remove`

### Condition Operators

| Operator | Aliases | Description |
|----------|---------|-------------|
| `==` | `eq` | Equals |
| `!=` | `neq`, `ne` | Not equals |
| `>` | `gt` | Greater than |
| `>=` | `gte`, `ge` | Greater than or equal |
| `<` | `lt` | Less than |
| `<=` | `lte`, `le` | Less than or equal |
| `contains` | `includes`, `has` | Array/string contains |
| `not_contains` | `not_includes`, `not_has`, `notcontains` | Array/string does not contain |
| `exists` | | Field exists and is not null |
| `not_exists` | `not_exist`, `notexists` | Field does not exist or is null |

### Condition Field Aliases
`left` is accepted as an alias for `field`, and `right` as an alias for `value` in condition checks.

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
| `condition` | `field`/`left`, `operator`, `value`/`right`*, `onFail` |
| `lookup` | `source`, `table`, `key`, `value`, `as` |
| `filter` | `source`, `field`, `operator`, `value`, `as` |
| `workflow` | `workflow` |
| `delete` | `collection`, `key` |

*`value` not required for `exists`/`not_exists`/`not_contains` (when checking existence) operators.

## Response Pattern

- Server returns HTTP 200 with `{ ok: false, error: { code, message } }` for rejections
- Non-200 status codes cause s&box Http API to throw and lose response body
