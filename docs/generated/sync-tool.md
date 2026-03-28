# Sync Tool

> **Note:** For the most up-to-date documentation, visit https://sbox.cool/wiki/network-storage-v3 — these repo docs may be outdated.

Open via **Editor → Network Storage → Sync Tool**.

## What It Does

Compares local JSON files with sbox.cool server state, lets you push/pull changes.

## Status Indicators

| Icon | Meaning |
|------|---------|
| ✓ | Synced — local matches remote |
| ▲ | Local only — needs push |
| ▼ | Remote only — needs pull |
| ● | Changed — local and remote differ |

## Operations

- **Check Remote** — compare local vs remote
- **Push** (per item) — upload one resource
- **Push All** — upload all local resources
- **Pull** (per item) — download server version
- **Diff** — side-by-side comparison

## Important

- Always push **collections before endpoints** — endpoints reference collections
- New resources get auto-generated IDs on first push
- Changes are live immediately after push
