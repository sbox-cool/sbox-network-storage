# Environment Configuration (.env)

The `.env` file stores API credentials for the editor Sync Tool. Located at `Editor/Network Storage/config/.env` (gitignored).

## Format

```env
# Project identifier from sboxcool.com dashboard
SBOXCOOL_PROJECT_ID=your-project-id

# Public API key (sbox_ns_ prefix) — used by game client at runtime
SBOXCOOL_PUBLIC_KEY=sbox_ns_your_public_key

# Secret key (sbox_sk_ prefix) — editor sync tool only, NEVER ships
SBOXCOOL_SECRET_KEY=sbox_sk_your_secret_key

# Base URL (default: https://api.sboxcool.com)
SBOXCOOL_BASE_URL=https://api.sboxcool.com

# API version (default: v3)
SBOXCOOL_API_VERSION=v3

# Editor subfolder for sync data (default: Network Storage)
SBOXCOOL_DATA_FOLDER=Network Storage

# Data source: api_then_json, api_only, json_only
SBOXCOOL_DATA_SOURCE=api_then_json
```

## Required Keys

| Key | Prefix | Description |
|-----|--------|-------------|
| `SBOXCOOL_PROJECT_ID` | — | Your project ID from the sbox.cool dashboard |
| `SBOXCOOL_PUBLIC_KEY` | `sbox_ns_` | Public key for game client (safe to ship) |
| `SBOXCOOL_SECRET_KEY` | `sbox_sk_` | Secret key for editor only (NEVER ship) |

## Optional Keys

| Key | Default | Description |
|-----|---------|-------------|
| `SBOXCOOL_BASE_URL` | `https://api.sboxcool.com` | API base URL |
| `SBOXCOOL_API_VERSION` | `v3` | API version |
| `SBOXCOOL_DATA_FOLDER` | `Network Storage` | Editor subfolder name |
| `SBOXCOOL_DATA_SOURCE` | `api_then_json` | Data source mode: `api_then_json`, `api_only`, `json_only` |

## Security

- The `.env` file is gitignored — never commit it
- The secret key (`sbox_sk_`) must NEVER ship with your game
- The public key (`sbox_ns_`) is safe to include in published builds
- `Editor/` directory is never published by s&box

## Validation Rules

The MCP validates `.env` files against these rules:

- All three required keys must be present: `SBOXCOOL_PROJECT_ID`, `SBOXCOOL_PUBLIC_KEY`, `SBOXCOOL_SECRET_KEY`
- `SBOXCOOL_PUBLIC_KEY` must start with `sbox_ns_`
- `SBOXCOOL_SECRET_KEY` must start with `sbox_sk_`
- `SBOXCOOL_BASE_URL` must be a valid URL (if present)
- `SBOXCOOL_API_VERSION` must match format like `v3` (if present)
- `SBOXCOOL_DATA_SOURCE` must be one of: `api_then_json`, `api_only`, `json_only` (if present)
- Placeholder values (e.g. `your-project-id`, `sbox_ns_your_public_key`) are flagged as warnings
- Unknown environment variable names are flagged
- Real secret keys trigger a reminder to keep the file gitignored
