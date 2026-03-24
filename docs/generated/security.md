# Security

## Key Types

| Key | Prefix | Ships with game? | Purpose |
|-----|--------|------------------|---------|
| Public Key | `sbox_ns_` | Yes | Runtime API calls (read data, call endpoints) |
| Secret Key | `sbox_sk_` | **NEVER** | Editor sync tool (manage collections/endpoints/workflows) |

## What Ships

- `Assets/network-storage.credentials.json` — Project ID + Public Key only
- `Code/` directory — runtime client code

## What Stays Local

- `Editor/` directory — never published by s&box
- `Editor/Network Storage/config/.env` — all keys (gitignored)
- Secret key is only used by the editor Sync Tool

## Rules

1. Never commit `.env` files
2. Never log or display secret keys
3. Never include secret keys in game code
4. All data validation must happen server-side via endpoints
5. Client-only checks can be bypassed — always validate on server
