# Network Storage — Overview

Network Storage is an s&box editor library providing a complete backend-as-a-service for s&box games. It connects your game to the sbox.cool cloud platform for persistent player data, game configuration, and server-side logic — all managed through JSON files.

## Two Halves

1. **Runtime Client** (`Code/`) — ships with your game, makes API calls to read/write data and call endpoints
2. **Editor Sync Tool** (`Editor/`) — developer-only, manages collections/endpoints/workflows via GUI that syncs with sbox.cool

## Data Flow

- Developers edit JSON files in `Editor/Network Storage/`
- Sync Tool pushes changes to sbox.cool (live immediately)
- Game code uses `NetworkStorageClient` to call endpoints
- Server executes pipelines atomically (read → validate → transform → write/delete → respond)

## Key Principles

- **Editor/ never ships** — secrets and management tools stay on dev machines
- **JSON as source of truth** — version-controllable, human-readable, PR-reviewable
- **Backend-first design** — all validation/logic runs server-side via endpoints
- **HTTP 200 for rejections** — s&box's Http API throws on 4xx/5xx, so errors return 200 with `ok: false`
