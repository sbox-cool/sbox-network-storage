# Network Storage — Setup Guide

> **Note:** For the most up-to-date documentation, visit https://sbox.cool/wiki/network-storage-v3 — these repo docs may be outdated.

> **New here?** Start with the [Getting Started](getting-started.md) guide for a complete walkthrough including creating collections, endpoints, and workflows.

## Installation

1. In the s&box editor, open **Library Manager**
2. Search for **Network Storage** by `sboxcool`
3. Click **Add to Project**
4. s&box imports the library into `Libraries/sboxcool.network-storage/`

On first load, the library automatically scaffolds:

```
Editor/Network Storage/
├── config/
│   ├── .env           ← Placeholder credentials (edit these)
│   └── .gitignore     ← Keeps .env out of version control
├── collections/
│   └── players.json   ← Sample player collection
├── endpoints/
│   └── init-player.json ← Sample endpoint
└── workflows/         ← Empty, ready for your workflows
```

## Configuration

### 1. Get API Keys

1. Go to [sbox.cool/tools/network-storage](https://sbox.cool/tools/network-storage) and sign in with Steam
2. Create a project (or open an existing one)
3. Go to **API Keys** in the dashboard
4. Create a **Public** key (prefix `sbox_ns_`) — used by the game client
5. Create a **Secret** key (prefix `sbox_sk_`) — used by the editor sync tool only

### 2. Enter Credentials

Open **Editor → Network Storage → Setup** in the s&box editor.

Fill in:
| Field | Value | Notes |
|-------|-------|-------|
| **Project ID** | Your project ID from the dashboard | Required |
| **Public API Key** | `sbox_ns_...` | Used by game client at runtime |
| **Secret Key** | `sbox_sk_...` | Editor-only, NEVER ships with game |
| **Base URL** | `https://api.sboxcool.com` | Default, rarely changed |
| **Editor Data Folder** | `Network Storage` | Subfolder name under `Editor/` |

Each field has a **Paste** button (reads from system clipboard) and a **Clear** button.

Click **Save Configuration** to write credentials. This creates:
- `Editor/Network Storage/config/.env` — all keys for the editor (gitignored)
- `Assets/network-storage.credentials.json` — Project ID + Public Key for the game client

### 3. Test Connection

Click **Test Connection** to verify all credentials against the server. You'll see per-credential results:
- **Project ID** — checks the project exists
- **Secret Key** — checks the key is valid and has management permissions
- **Public Key** — checks the key is valid for runtime use

#### Runtime Connection Test

To verify the game client can reach the API, add this temporary test to any component:

```csharp
protected override void OnStart()
{
    _ = TestConnection();
}

private async Task TestConnection()
{
    // Auto-configures from Assets/network-storage.credentials.json
    Log.Info( $"[Test] ProjectId={NetworkStorage.ProjectId}" );
    Log.Info( $"[Test] ApiKey={NetworkStorage.ApiKey}" );

    var values = await NetworkStorage.GetGameValues();
    Log.Info( values.HasValue
        ? "[Test] Connected to Network Storage!"
        : "[Test] Connection failed — check console for error details" );
}
```

**Common issues:**
| Symptom | Cause | Fix |
|---------|-------|-----|
| `ProjectId=your_project_id` | Credentials file not found or old config cached | Restart s&box (full, not hot-reload) |
| 401 Unauthorized | Wrong API key or auth token empty | Verify keys in Setup, ensure you're in Play mode |
| 400 Bad Request | Endpoint/collection not pushed to server | Use Sync Tool to push |
| `undefined read on X` | Endpoint references a missing collection | Push the collection first |

### 4. Data Source Preference

Choose how the runtime client fetches data:

| Mode | Behavior |
|------|----------|
| **API + Fallback** | Try API first, fall back to local JSON files if API fails |
| **API Only** | Always use API, fail if unreachable |
| **JSON Only** | Use local JSON files only (offline mode) |

## How Credentials Flow

```
sbox.cool Dashboard
    │
    ├─→ Project ID + Public Key + Secret Key
    │
    ▼
Editor → Network Storage → Setup (Save Configuration)
    │
    ├─→ Editor/Network Storage/config/.env     (all keys — editor only, gitignored)
    │
    └─→ Assets/network-storage.credentials.json (Project ID + Public Key — ships with game)
         │
         ▼
    NetworkStorage.AutoConfigure()   (reads credentials file on first API call)
         │
         ▼
    NetworkStorage.CallEndpoint()    (uses Project ID + Public Key + Steam auth token)
```

## Credential Security

- `.env` is stored in `Editor/Network Storage/config/` which is inside `Editor/`
- s&box **never publishes** anything in `Editor/` — secrets are safe
- A `.gitignore` is created automatically to prevent committing `.env`
- The **secret key** (`sbox_sk_`) is only used by editor tools, never by the game client
- The **public key** (`sbox_ns_`) is safe to ship — it can only read public data and call endpoints with Steam authentication

## Migration

The library auto-detects and migrates from older configurations:

| Old Location | New Location |
|---|---|
| `Editor/Network Storage/.env` (root) | `Editor/Network Storage/config/.env` |
| `Editor/SyncTools/.env` (legacy) | `Editor/Network Storage/config/.env` |

Migration happens automatically on next save — no manual steps needed.

## Next Steps

- [Getting Started](getting-started.md) — Full walkthrough: create collections, endpoints, workflows, and make API calls
- [Runtime Client](runtime-client.md) — API reference for game code
- [Sync Tool](sync-tool.md) — Managing your backend data from the editor
- [File Reference](file-reference.md) — JSON file formats for collections, endpoints, and workflows
