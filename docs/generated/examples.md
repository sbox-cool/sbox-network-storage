# Examples — Common Game Scenarios

Pre-built JSON examples for common game patterns. These are the same examples available via the MCP's `get_examples` tool.

## Player Data

### Collection: players

Basic player data collection with currency, XP, and level.

```json
{
  "name": "players",
  "description": "Player save data",
  "collectionType": "per-steamid",
  "accessMode": "public",
  "maxRecords": 1,
  "schema": {
    "currency": { "type": "number", "default": 0 },
    "xp": { "type": "number", "default": 0 },
    "level": { "type": "number", "default": 1 },
    "displayName": { "type": "string", "default": "" }
  }
}
```

### Endpoint: init-player

Initialize a new player record with default values.

```json
{
  "slug": "init-player",
  "name": "Initialize Player",
  "method": "POST",
  "description": "Create or load a player record",
  "enabled": true,
  "input": {
    "displayName": { "type": "string", "required": true }
  },
  "steps": [
    { "type": "read", "collection": "players", "as": "player" },
    { "type": "transform", "field": "player.displayName", "operation": "set", "value": "{{input.displayName}}" },
    { "type": "write", "collection": "players" }
  ],
  "response": { "status": 200, "body": { "ok": true } }
}
```

---

## Inventory System

### Collection: players (with inventory)

Player collection with inventory and backpack capacity.

```json
{
  "name": "players",
  "description": "Player data with inventory system",
  "collectionType": "per-steamid",
  "accessMode": "public",
  "maxRecords": 1,
  "schema": {
    "currency": { "type": "number", "default": 0 },
    "backpackCapacity": { "type": "number", "default": 100 },
    "currentOreKg": { "type": "number", "default": 0 },
    "ores": { "type": "object", "default": {} },
    "phaserTier": { "type": "number", "default": 1 }
  },
  "constants": [
    { "group": "mining", "values": { "base_capacity": 100, "capacity_per_upgrade": 50 } }
  ],
  "tables": [
    {
      "name": "ore_types",
      "columns": ["id", "name", "tier", "basePrice"],
      "rows": [
        ["iron", "Iron Ore", 1, 10],
        ["copper", "Copper", 2, 25],
        ["gold", "Gold", 3, 50]
      ]
    }
  ]
}
```

### Endpoint: mine-ore

Mine ore with backpack capacity check and tier validation.

```json
{
  "slug": "mine-ore",
  "name": "Mine Ore",
  "method": "POST",
  "description": "Process an ore mining action",
  "enabled": true,
  "input": {
    "oreType": { "type": "string", "required": true },
    "amount": { "type": "number", "required": true }
  },
  "steps": [
    { "type": "read", "collection": "players", "as": "player" },
    { "type": "lookup", "source": "values", "table": "ore_types", "key": "id", "value": "{{input.oreType}}", "as": "ore" },
    { "type": "condition", "field": "player.phaserTier", "operator": "gte", "value": "{{ore.tier}}", "onFail": "error", "errorMessage": "Phaser tier too low for this ore" },
    { "type": "condition", "field": "player.currentOreKg", "operator": "lt", "value": "{{player.backpackCapacity}}", "onFail": "error", "errorCode": "BACKPACK_FULL", "errorMessage": "Backpack is full" },
    { "type": "transform", "field": "player.ores.{{input.oreType}}", "operation": "add", "value": "{{input.amount}}" },
    { "type": "transform", "field": "player.currentOreKg", "operation": "add", "value": "{{input.amount}}" },
    { "type": "write", "collection": "players" }
  ],
  "response": { "status": 200, "body": { "ok": true } }
}
```

### Workflow: check-backpack-space

Verify player has backpack space available.

```json
{
  "id": "check-backpack-space",
  "name": "Check Backpack Space",
  "description": "Verify player has room in their backpack",
  "condition": {
    "field": "player.currentOreKg",
    "op": "lt",
    "value": "{{player.backpackCapacity}}"
  },
  "onFail": {
    "reject": true,
    "errorCode": "BACKPACK_FULL",
    "errorMessage": "Not enough backpack space. Current: {{player.currentOreKg}}kg / {{player.backpackCapacity}}kg."
  },
  "steps": [
    {
      "type": "condition",
      "field": "player.currentOreKg",
      "operator": "lt",
      "value": "{{player.backpackCapacity}}",
      "onFail": "error",
      "errorMessage": "Backpack is full"
    }
  ]
}
```

---

## Currency System

### Endpoint: sell-ore

Sell ore for currency with ore amount validation.

```json
{
  "slug": "sell-ore",
  "name": "Sell Ore",
  "method": "POST",
  "description": "Sell ore from inventory for currency",
  "enabled": true,
  "input": {
    "oreType": { "type": "string", "required": true },
    "amount": { "type": "number", "required": true }
  },
  "steps": [
    { "type": "read", "collection": "players", "as": "player" },
    { "type": "lookup", "source": "values", "table": "ore_types", "key": "id", "value": "{{input.oreType}}", "as": "ore" },
    { "type": "condition", "field": "player.ores.{{input.oreType}}", "operator": "gte", "value": "{{input.amount}}", "onFail": "error", "errorCode": "NOT_ENOUGH_ORE", "errorMessage": "Not enough ore to sell" },
    { "type": "transform", "field": "player.ores.{{input.oreType}}", "operation": "subtract", "value": "{{input.amount}}" },
    { "type": "transform", "field": "player.currentOreKg", "operation": "subtract", "value": "{{input.amount}}" },
    { "type": "transform", "field": "player.currency", "operation": "add", "value": "{{input.amount}}" },
    { "type": "write", "collection": "players" }
  ],
  "response": { "status": 200, "body": { "ok": true } }
}
```

### Endpoint: buy-upgrade

Purchase an upgrade with currency check.

```json
{
  "slug": "buy-upgrade",
  "name": "Buy Upgrade",
  "method": "POST",
  "description": "Purchase an upgrade using currency",
  "enabled": true,
  "input": {
    "upgradeId": { "type": "string", "required": true },
    "cost": { "type": "number", "required": true }
  },
  "steps": [
    { "type": "read", "collection": "players", "as": "player" },
    { "type": "condition", "field": "player.currency", "operator": "gte", "value": "{{input.cost}}", "onFail": "error", "errorCode": "NOT_ENOUGH_CURRENCY", "errorMessage": "Not enough currency" },
    { "type": "transform", "field": "player.currency", "operation": "subtract", "value": "{{input.cost}}" },
    { "type": "write", "collection": "players" }
  ],
  "response": { "status": 200, "body": { "ok": true } }
}
```

### Workflow: check-currency

Reusable currency check workflow.

```json
{
  "id": "check-currency",
  "name": "Check Currency",
  "description": "Verify player has enough currency",
  "condition": {
    "field": "player.currency",
    "op": "gte",
    "value": "{{input.cost}}"
  },
  "onFail": {
    "reject": true,
    "errorCode": "NOT_ENOUGH_CURRENCY",
    "errorMessage": "Not enough currency. Have: {{player.currency}}, need: {{input.cost}}"
  },
  "steps": [
    {
      "type": "condition",
      "field": "player.currency",
      "operator": "gte",
      "value": "{{input.cost}}",
      "onFail": "error",
      "errorMessage": "Not enough currency"
    }
  ]
}
```

---

## Leaderboard

### Collection: leaderboard

Global leaderboard collection for top scores.

```json
{
  "name": "leaderboard",
  "description": "Global leaderboard for high scores",
  "collectionType": "global",
  "accessMode": "public",
  "maxRecords": 100,
  "allowRecordDelete": false,
  "schema": {
    "playerName": { "type": "string", "default": "" },
    "steamId": { "type": "string", "default": "" },
    "score": { "type": "number", "default": 0 },
    "submittedAt": { "type": "string", "default": "" }
  }
}
```

### Endpoint: submit-score

Submit a score to the global leaderboard.

```json
{
  "slug": "submit-score",
  "name": "Submit Score",
  "method": "POST",
  "description": "Submit a player score to the leaderboard",
  "enabled": true,
  "input": {
    "playerName": { "type": "string", "required": true },
    "score": { "type": "number", "required": true }
  },
  "steps": [
    { "type": "read", "collection": "players", "as": "player" },
    { "type": "condition", "field": "input.score", "operator": "gt", "value": 0, "onFail": "error", "errorMessage": "Score must be positive" },
    { "type": "write", "collection": "leaderboard" }
  ],
  "response": { "status": 200, "body": { "ok": true } }
}
```

---

## Chat Messages

### Collection: chat_messages

Per-player chat message storage.

```json
{
  "name": "chat_messages",
  "description": "Stores chat messages per player",
  "collectionType": "per-steamid",
  "accessMode": "public",
  "maxRecords": 1,
  "schema": {
    "displayName": { "type": "string", "default": "" },
    "messageCount": { "type": "number", "default": 0 }
  }
}
```

### Endpoint: send-message

Send a chat message and increment counter.

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
    { "type": "read", "collection": "chat_messages", "as": "chat" },
    { "type": "transform", "field": "chat.displayName", "operation": "set", "value": "{{input.displayName}}" },
    { "type": "transform", "field": "chat.messageCount", "operation": "add", "value": 1 },
    { "type": "write", "collection": "chat_messages" }
  ],
  "response": { "status": 200, "body": { "ok": true, "messageCount": "{{chat.messageCount}}" } }
}
```
