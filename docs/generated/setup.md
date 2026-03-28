# Setup Guide

> **Note:** For the most up-to-date documentation, visit https://sbox.cool/wiki/network-storage-v3 — these repo docs may be outdated.

## Installation

1. In the s&box editor, open **Library Manager**
2. Search for **Network Storage** by `sboxcool`
3. Click **Add to Project**

On first load, the library scaffolds:
```
Editor/Network Storage/
├── config/.env           ← Placeholder credentials
├── config/.gitignore     ← Keeps .env out of git
├── collections/players.json ← Sample collection
├── endpoints/init-player.json ← Sample endpoint
└── workflows/            ← Empty
```

## Get API Keys

1. Go to https://sbox.cool/tools/network-storage and sign in with Steam
2. Create a project or open existing
3. Go to **API Keys** → create a Public key (`sbox_ns_`) and Secret key (`sbox_sk_`)

## Enter Credentials

1. Open **Editor → Network Storage → Setup**
2. Paste Project ID, Public Key, and Secret Key
3. Click **Save Configuration**

This writes two files:
- `Editor/Network Storage/config/.env` — all keys (editor only, gitignored)
- `Assets/network-storage.credentials.json` — Project ID + Public Key (ships with game)

## Test Connection

Click **Test Connection** to verify credentials. Green checkmarks for Project ID, Secret Key, and Public Key.

## Credential Flow

```
sbox.cool Dashboard → Project ID + Public Key + Secret Key
    ↓
Setup Window (Save Configuration)
    ├→ .env (editor only, gitignored)
    └→ credentials.json (ships with game)
        ↓
    NetworkStorage.AutoConfigure() (reads on first API call)
        ↓
    NetworkStorage.CallEndpoint() (uses Project ID + Public Key + Steam token)
```
