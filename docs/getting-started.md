# Network Storage — Getting Started

> **Note:** For the most up-to-date documentation, visit https://sbox.cool/wiki/network-storage-v3 — these repo docs may be outdated.

A complete walkthrough: configure your credentials, verify they work, create collections/endpoints/workflows, and make your first API call — all in under 10 minutes.

## 1. Create a Project on sbox.cool

1. Go to [https://sbox.cool/tools/network-storage](https://sbox.cool/tools/network-storage) and sign in with Steam
2. Click **Create Project**
3. Give it a name (e.g. "My Game")
4. Copy your **Project ID** from the dashboard — you'll need it next

## 2. Generate API Keys

From your project dashboard on [sbox.cool](https://sbox.cool):

1. Go to **API Keys**
2. Create a **Public Key** — starts with `sbox_ns_`. This is used by the game client at runtime. Safe to ship.
3. Create a **Secret Key** — starts with `sbox_sk_`. This is used by the editor Sync Tool only. **Never ships with your game.**

You now have three values: **Project ID**, **Public Key**, and **Secret Key**.

## 3. Enter Credentials in the Editor

1. Open the s&box editor
2. Go to **Editor → Network Storage → Setup**
3. Paste your **Project ID**, **Public API Key** (`sbox_ns_...`), and **Secret Key** (`sbox_sk_...`)
4. Click **Save Configuration**

This writes two files:
- `Editor/Network Storage/config/.env` — stores all keys for the editor Sync Tool (gitignored, never published)
- `Assets/network-storage.credentials.json` — stores only the Project ID and Public Key for the game client at runtime

## 4. Test Your Credentials

Still in the Setup window:

1. Click **Test Connection**
2. You should see green checkmarks for:
   - **Project ID** — the project exists on sbox.cool
   - **Secret Key** — valid and has management permissions
   - **Public Key** — valid for runtime API calls

If any test fails:
- Double-check you copied the full key (including the `sbox_ns_` or `sbox_sk_` prefix)
- Make sure the keys belong to the same project
- Check that the project is active on [sbox.cool](https://sbox.cool)

### Quick Runtime Test

To verify the game client can reach the API, add this to any component:

```csharp
protected override void OnStart()
{
    _ = TestConnection();
}

private async Task TestConnection()
{
    // Auto-configures from Assets/network-storage.credentials.json
    var values = await NetworkStorage.GetGameValues();
    if ( values.HasValue )
        Log.Info( "[Test] Connected to Network Storage!" );
    else
        Log.Warning( "[Test] Connection failed — check credentials" );
}
```

If you see "Connected" in the console, everything is working. If you see 401 Unauthorized, make sure:
- `Assets/network-storage.credentials.json` exists and has the correct Project ID and Public Key
- You did a **full restart** of s&box after saving credentials (static config state persists across hot-reloads)
- You are running in Play mode (Steam auth tokens are only available during active play)

## 5. Create a Collection

Collections define where your data is stored. You can create them in two ways:

### Option A: On the Website (Recommended for Beginners)

1. Go to your project on [https://sbox.cool/tools/network-storage](https://sbox.cool/tools/network-storage)
2. Click **Collections → Create Collection**
3. Set the name, type (`per-steamid` for player data, `global` for shared data), and schema
4. In the s&box editor, open **Editor → Network Storage → Sync Tool**
5. Click **Check Remote** — your new collection appears as "Remote only" (▼)
6. Click **Pull** to download it as a local JSON file

### Option B: As a Local JSON File (Recommended for Version Control)

Create a JSON file in `Editor/Network Storage/collections/`:

```json
{
  "name": "chat_messages",
  "description": "Stores chat messages per player",
  "collectionType": "per-steamid",
  "accessMode": "public",
  "maxRecords": 1,
  "schema": {
    "displayName": { "type": "string", "default": "" },
    "messages": { "type": "array", "default": [] },
    "messageCount": { "type": "number", "default": 0 }
  }
}
```

Then push it:
1. Open **Editor → Network Storage → Sync Tool**
2. Your collection shows as "Local, not pushed" (▲)
3. Click **Push** to upload it to sbox.cool

## 6. Create an Endpoint

Endpoints are server-side pipelines that your game calls. They read, validate, transform, and write data atomically.

### Option A: On the Website

1. Go to your project on [https://sbox.cool/tools/network-storage](https://sbox.cool/tools/network-storage)
2. Click **Endpoints → Create Endpoint**
3. Define the slug, method, input schema, and pipeline steps
4. Pull it down via the Sync Tool

### Option B: As a Local JSON File

Create a JSON file in `Editor/Network Storage/endpoints/`:

```json
{
  "slug": "send-message",
  "name": "Send Message",
  "method": "POST",
  "description": "Send a chat message",
  "enabled": true,
  "input": {
    "text": { "type": "string", "required": true },
    "displayName": { "type": "string", "required": true }
  },
  "steps": [
    {
      "type": "read",
      "collection": "chat_messages",
      "as": "chat"
    },
    {
      "type": "transform",
      "field": "chat.displayName",
      "operation": "set",
      "value": "{{input.displayName}}"
    },
    {
      "type": "transform",
      "field": "chat.messageCount",
      "operation": "add",
      "value": 1
    },
    {
      "type": "write",
      "collection": "chat_messages"
    }
  ],
  "response": {
    "status": 200,
    "body": { "ok": true, "messageCount": "{{chat.messageCount}}" }
  }
}
```

Push it via the Sync Tool just like collections.

### Endpoint Step Types

| Type | What it does |
|------|-------------|
| `read` | Load a record from a collection into a variable |
| `write` | Save a record back to a collection |
| `transform` | Modify a field (add, subtract, set, multiply) |
| `condition` | Check a condition; reject the request if it fails |
| `lookup` | Look up a row in a values table |
| `filter` | Filter an array field |
| `workflow` | Run a reusable workflow by ID |

## 7. Create a Workflow

Workflows are reusable validation/logic blocks that endpoints can reference. Useful for shared checks like "does the player have enough currency?" or "is the input valid?".

### Option A: On the Website

1. Go to [https://sbox.cool/tools/network-storage](https://sbox.cool/tools/network-storage)
2. Click **Workflows → Create Workflow**
3. Pull via the Sync Tool

### Option B: As a Local JSON File

Create a JSON file in `Editor/Network Storage/workflows/`:

```json
{
  "id": "validate-message",
  "name": "Validate Message",
  "description": "Check that a chat message is not empty",
  "condition": {
    "field": "input.text",
    "op": "neq",
    "value": ""
  },
  "onFail": "error",
  "errorMessage": "Message cannot be empty",
  "steps": [
    {
      "type": "condition",
      "field": "input.text",
      "op": "neq",
      "value": "",
      "onFail": "error",
      "errorMessage": "Message cannot be empty"
    }
  ]
}
```

Reference it from an endpoint step:
```json
{
  "type": "workflow",
  "workflow": "validate-message"
}
```

Push via the Sync Tool.

## 8. Call Your Endpoint from Game Code

```csharp
// No manual configuration needed — auto-configures from credentials file.

// GET endpoint (no input)
var data = await NetworkStorage.CallEndpoint( "load-messages" );
if ( data.HasValue )
{
    var count = JsonHelpers.GetInt( data.Value, "messageCount", 0 );
    Log.Info( $"You have {count} messages" );
}

// POST endpoint (with input)
var result = await NetworkStorage.CallEndpoint( "send-message", new
{
    text = "Hello world!",
    displayName = "Player1"
} );

if ( result.HasValue )
    Log.Info( "Message sent!" );
else
    Log.Warning( "Failed to send — check console for details" );
```

## 9. Sync Tool Workflow Summary

The Sync Tool (**Editor → Network Storage → Sync Tool**) is your main interface for managing backend data:

| Action | What it does |
|--------|-------------|
| **Check Remote** | Compares local JSON files with the sbox.cool server |
| **Push** | Uploads local files to the server |
| **Pull** | Downloads server state to local files |
| **Diff** | Side-by-side comparison before overwriting |
| **Push All** | Pushes all local resources at once |

Status indicators:
| Icon | Meaning |
|------|---------|
| ✓ | Synced — local matches remote |
| ▲ | Local only — needs to be pushed |
| ▼ | Remote only — needs to be pulled |
| ● | Changed — local and remote differ |

**Always push collections before endpoints** — endpoints reference collections, so the collection must exist on the server first.

## Troubleshooting

| Error | Cause | Fix |
|-------|-------|-----|
| 401 Unauthorized | Invalid or missing credentials | Re-check credentials in Setup, restart s&box |
| 400 Bad Request | Endpoint or collection not pushed | Push via Sync Tool |
| `undefined read on X` | Endpoint references a collection that doesn't exist on server | Push the collection first |
| Empty auth token | Not running in Play mode | Press Play in the editor |
| Credentials not loading | Static config stuck after hot-reload | Full restart of s&box |

## Useful Links

- **Dashboard & Project Management:** [https://sbox.cool/tools/network-storage](https://sbox.cool/tools/network-storage)
- **Documentation & Tutorials:** [https://sbox.cool/wiki/network-storage-v3](https://sbox.cool/wiki/network-storage-v3)
- **s&box Library Page:** [https://sbox.game/sboxcool/networkstoragebysboxcool](https://sbox.game/sboxcool/networkstoragebysboxcool)
