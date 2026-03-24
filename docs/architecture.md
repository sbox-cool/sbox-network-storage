# Network Storage — Architecture

## Overview

Network Storage is an s&box editor library that provides a complete backend-as-a-service for s&box games. It connects your game to the sbox.cool cloud platform for persistent player data, game configuration, and server-side logic — all managed through JSON files in your editor.

The library has two halves:

1. **Runtime Client** (`Code/`) — used by your game at runtime to read/write player data and call endpoints
2. **Editor Sync Tool** (`Editor/`) — used by developers to manage collections, endpoints, and workflows via a GUI that syncs with the sbox.cool API

## Project Structure

```
sboxcool.network-storage/
├── Code/                          # Runtime library (ships with your game)
│   ├── NetworkStorageClient.cs    # Main client — GET/POST to the API
│   ├── NetworkStorageExtensions.cs# Helper extensions for common patterns
│   ├── JsonHelpers.cs             # JSON utilities (merge, diff, etc.)
│   ├── NetLog.cs                  # Logging utilities
│   └── SaveStateTracker.cs        # Tracks dirty state for auto-save
│
├── Editor/                        # Editor tools (NEVER ships with game)
│   ├── SetupWindow.cs             # Credentials setup UI (Editor → Network Storage → Setup)
│   ├── SyncToolWindow.cs          # Main sync tool UI (Editor → Network Storage → Sync Tool)
│   ├── SyncToolConfig.cs          # Config loader/saver — reads .env, manages paths
│   ├── SyncToolApi.cs             # HTTP client for the management API
│   ├── SyncToolTransforms.cs      # Transforms between local JSON ↔ server format
│   ├── DiffViewWindow.cs          # Side-by-side diff viewer for comparing local vs remote
│   └── ConfirmDialog.cs           # Modal confirmation dialog
│
├── Examples/                      # Example scripts showing usage patterns
│   ├── BasicSetup.cs
│   ├── GameValuesExample.cs
│   ├── PlayerDataExample.cs
│   └── SaveStateTrackerExample.cs
│
├── UnitTests/                     # Unit tests
│   ├── UnitTest.cs
│   └── LibraryTest.cs
│
└── network-storage.sbproj         # s&box project file (Org: sboxcool, Ident: network-storage)
```

## Data Flow

```
┌─────────────────────────────────────────────────────────┐
│                    Your s&box Project                    │
│                                                         │
│  Editor/Network Storage/                                │
│  ├── config/.env          ← API keys (gitignored)       │
│  ├── collections/*.json   ← Collection schemas          │
│  ├── endpoints/*.json     ← Endpoint pipelines          │
│  └── workflows/*.json     ← Reusable workflow steps     │
│                                                         │
│  ┌──────────────┐    ┌───────────────┐                  │
│  │ Sync Tool UI │───→│ SyncToolApi   │──── HTTPS ──→ sbox.cool API
│  │ (Editor)     │←───│ SyncToolConfig│←─── JSON ───     │
│  └──────────────┘    └───────────────┘                  │
│                                                         │
│  ┌──────────────────────────┐                           │
│  │ NetworkStorageClient     │──── HTTPS ──→ sbox.cool API
│  │ (Runtime, in your game)  │←─── JSON ───              │
│  └──────────────────────────┘                           │
└─────────────────────────────────────────────────────────┘
```

### Editor Flow (Development Time)
1. Developer edits JSON files in `Editor/Network Storage/`
2. Opens the Sync Tool (Editor → Network Storage → Sync Tool)
3. Sync Tool compares local files with remote server state
4. Developer clicks Push to upload changes, or Pull to download remote changes
5. Changes are live immediately — no deploy step

### Runtime Flow (Game Running)
1. Game code uses `NetworkStorageClient` to call endpoints or read collections
2. Client authenticates with the **public** API key (safe to ship)
3. Server executes endpoint pipelines (read → validate → write → respond)
4. Client receives JSON response

## Key Design Decisions

### Editor/ is Never Published
Everything in `Editor/` is excluded from s&box publishing. Secret keys, sync tools, and management API calls never ship with your game. The runtime client uses only the public key.

### JSON Files as Source of Truth
Collections, endpoints, and workflows are stored as individual JSON files. This makes them:
- Version-controllable (git diff, blame, merge)
- Human-readable and hand-editable
- Easy to review in PRs

### First-Install Scaffolding
When the library is added to a new project, `SyncToolConfig.Load()` detects no `.env` file and automatically creates the full folder structure with sample files (a player collection and init-player endpoint).

### Clipboard via PowerShell
The Setup window's input fields use `powershell.exe Get-Clipboard` for paste support, since s&box's editor sandbox doesn't expose a clipboard API. This is Windows-only but s&box only runs on Windows.
