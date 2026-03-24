# Network Storage — Setup Guide

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

1. Go to [sbox.cool](https://sbox.cool) and sign in
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

Click **Save Configuration** to write to `Editor/Network Storage/config/.env`.

### 3. Test Connection

Click **Test Connection** to verify all credentials against the server. You'll see per-credential results:
- **Project ID** — checks the project exists
- **Secret Key** — checks the key is valid and has management permissions
- **Public Key** — checks the key is valid for runtime use

### 4. Data Source Preference

Choose how the runtime client fetches data:

| Mode | Behavior |
|------|----------|
| **API + Fallback** | Try API first, fall back to local JSON files if API fails |
| **API Only** | Always use API, fail if unreachable |
| **JSON Only** | Use local JSON files only (offline mode) |

## Credential Security

- `.env` is stored in `Editor/Network Storage/config/` which is inside `Editor/`
- s&box **never publishes** anything in `Editor/` — secrets are safe
- A `.gitignore` is created automatically to prevent committing `.env`
- The **secret key** (`sbox_sk_`) is only used by editor tools, never by the game client
- The **public key** (`sbox_ns_`) is safe to ship — it can only read public data and call endpoints

## Migration

The library auto-detects and migrates from older configurations:

| Old Location | New Location |
|---|---|
| `Editor/Network Storage/.env` (root) | `Editor/Network Storage/config/.env` |
| `Editor/SyncTools/.env` (legacy) | `Editor/Network Storage/config/.env` |

Migration happens automatically on next save — no manual steps needed.
