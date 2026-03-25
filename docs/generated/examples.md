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
  "accessMode": "endpoint",
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
    "type": "object",
    "properties": {
      "displayName": { "type": "string" }
    },
    "required": ["displayName"]
  },
  "steps": [
    {
      "id": "player",
      "type": "read",
      "collection": "players",
      "key": "{{steamId}}_default"
    },
    {
      "id": "save",
      "type": "write",
      "collection": "players",
      "key": "{{steamId}}_default",
      "ops": [
        { "op": "set", "path": "displayName", "value": "{{input.displayName}}" }
      ]
    }
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
  "accessMode": "endpoint",
  "maxRecords": 1,
  "schema": {
    "currency": { "type": "number", "default": 0 },
    "backpackCapacity": { "type": "number", "default": 100 },
    "currentOreKg": { "type": "number", "default": 0 },
    "ores": { "type": "object", "default": {} },
    "phaserTier": { "type": "number", "default": 1 }
  },
  "constants": [
    {
      "id": "mining",
      "name": "Mining",
      "entries": { "base_capacity": 100, "capacity_per_upgrade": 50 }
    }
  ],
  "tables": [
    {
      "id": "ore_types",
      "name": "Ore Types",
      "columns": [
        { "key": "id", "type": "string" },
        { "key": "name", "type": "string" },
        { "key": "tier", "type": "number" },
        { "key": "basePrice", "type": "number" }
      ],
      "rows": [
        { "id": "iron", "name": "Iron Ore", "tier": 1, "basePrice": 10 },
        { "id": "copper", "name": "Copper", "tier": 2, "basePrice": 25 },
        { "id": "gold", "name": "Gold", "tier": 3, "basePrice": 50 }
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
    "type": "object",
    "properties": {
      "oreType": { "type": "string" },
      "amount": { "type": "number" }
    },
    "required": ["oreType", "amount"]
  },
  "steps": [
    {
      "id": "player",
      "type": "read",
      "collection": "players",
      "key": "{{steamId}}_default"
    },
    {
      "id": "ore",
      "type": "lookup",
      "source": "values",
      "table": "ore_types",
      "where": {
        "field": "id",
        "op": "==",
        "value": "{{input.oreType}}"
      }
    },
    {
      "id": "tier_check",
      "type": "condition",
      "check": {
        "field": "{{player.phaserTier}}",
        "op": ">=",
        "value": "{{ore.tier}}"
      },
      "onFail": {
        "status": 403,
        "error": "TIER_TOO_LOW",
        "message": "Phaser tier too low for this ore"
      }
    },
    {
      "id": "space_check",
      "type": "condition",
      "check": {
        "field": "{{player.currentOreKg}}",
        "op": "<",
        "value": "{{player.backpackCapacity}}"
      },
      "onFail": {
        "status": 403,
        "error": "BACKPACK_FULL",
        "message": "Backpack is full"
      }
    },
    {
      "id": "new_ore_total",
      "type": "transform",
      "expression": "{{player.currentOreKg}} + {{input.amount}}"
    },
    {
      "id": "save",
      "type": "write",
      "collection": "players",
      "key": "{{steamId}}_default",
      "ops": [
        { "op": "inc", "path": "ores.{{input.oreType}}", "value": "{{input.amount}}" },
        { "op": "set", "path": "currentOreKg", "value": "{{new_ore_total}}" }
      ]
    }
  ],
  "response": {
    "status": 200,
    "body": {
      "ok": true,
      "currentOreKg": "{{new_ore_total}}"
    }
  }
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
    "field": "{{new_ore_total}}",
    "op": "<=",
    "value": "{{capacity}}"
  },
  "onFail": {
    "reject": true,
    "errorCode": "BACKPACK_FULL",
    "errorMessage": "Not enough backpack space. Current: {{current_kg}}kg / {{capacity}}kg."
  }
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
    "type": "object",
    "properties": {
      "oreType": { "type": "string" },
      "amount": { "type": "number" }
    },
    "required": ["oreType", "amount"]
  },
  "steps": [
    {
      "id": "player",
      "type": "read",
      "collection": "players",
      "key": "{{steamId}}_default"
    },
    {
      "id": "ore",
      "type": "lookup",
      "source": "values",
      "table": "ore_types",
      "where": {
        "field": "id",
        "op": "==",
        "value": "{{input.oreType}}"
      }
    },
    {
      "id": "ore_check",
      "type": "condition",
      "check": {
        "field": "{{player.ores.{{input.oreType}}}}",
        "op": ">=",
        "value": "{{input.amount}}"
      },
      "onFail": {
        "status": 403,
        "error": "NOT_ENOUGH_ORE",
        "message": "Not enough ore to sell"
      }
    },
    {
      "id": "sale_value",
      "type": "transform",
      "expression": "round({{ore.basePrice}} * {{input.amount}})"
    },
    {
      "id": "neg_amount",
      "type": "transform",
      "expression": "0 - {{input.amount}}"
    },
    {
      "id": "new_ore_total",
      "type": "transform",
      "expression": "max({{player.currentOreKg}} - {{input.amount}}, 0)"
    },
    {
      "id": "save",
      "type": "write",
      "collection": "players",
      "key": "{{steamId}}_default",
      "ops": [
        { "op": "inc", "path": "ores.{{input.oreType}}", "value": "{{neg_amount}}" },
        { "op": "inc", "path": "currency", "value": "{{sale_value}}", "source": "ore_sale", "reason": "Sold {{input.amount}} of {{ore.name}}" },
        { "op": "set", "path": "currentOreKg", "value": "{{new_ore_total}}" }
      ]
    }
  ],
  "response": {
    "status": 200,
    "body": {
      "ok": true,
      "earned": "{{sale_value}}",
      "currency": "{{player.currency}}"
    }
  }
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
    "type": "object",
    "properties": {
      "upgradeId": { "type": "string" }
    },
    "required": ["upgradeId"]
  },
  "steps": [
    {
      "id": "player",
      "type": "read",
      "collection": "players",
      "key": "{{steamId}}_default"
    },
    {
      "id": "upgrade",
      "type": "lookup",
      "source": "values",
      "table": "upgrades",
      "where": {
        "field": "upgrade_id",
        "op": "==",
        "value": "{{input.upgradeId}}"
      }
    },
    {
      "id": "upgrade_cost",
      "type": "transform",
      "expression": "{{upgrade.cost}} + 0"
    },
    {
      "id": "currency_check",
      "type": "condition",
      "check": {
        "field": "{{player.currency}}",
        "op": ">=",
        "value": "{{upgrade_cost}}"
      },
      "onFail": {
        "status": 403,
        "error": "NOT_ENOUGH_CURRENCY",
        "message": "Not enough currency. Need {{upgrade_cost}}, have {{player.currency}}."
      }
    },
    {
      "id": "neg_cost",
      "type": "transform",
      "expression": "0 - {{upgrade_cost}}"
    },
    {
      "id": "save",
      "type": "write",
      "collection": "players",
      "key": "{{steamId}}_default",
      "ops": [
        { "op": "inc", "path": "currency", "value": "{{neg_cost}}", "source": "upgrade_purchase", "reason": "Purchased {{upgrade.name}}" },
        { "op": "push", "path": "purchasedUpgrades", "value": "{{input.upgradeId}}" }
      ]
    }
  ],
  "response": {
    "status": 200,
    "body": {
      "ok": true,
      "purchased": "{{input.upgradeId}}"
    }
  }
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
    "field": "{{player.currency}}",
    "op": ">=",
    "value": "{{cost}}"
  },
  "onFail": {
    "reject": true,
    "errorCode": "NOT_ENOUGH_CURRENCY",
    "errorMessage": "Not enough currency. Have: {{player.currency}}, need: {{cost}}"
  }
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
  "accessMode": "endpoint",
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
    "type": "object",
    "properties": {
      "playerName": { "type": "string" },
      "score": { "type": "number" }
    },
    "required": ["playerName", "score"]
  },
  "steps": [
    {
      "id": "player",
      "type": "read",
      "collection": "players",
      "key": "{{steamId}}_default"
    },
    {
      "id": "score_check",
      "type": "condition",
      "check": {
        "field": "{{input.score}}",
        "op": ">",
        "value": 0
      },
      "onFail": {
        "status": 400,
        "error": "INVALID_SCORE",
        "message": "Score must be positive"
      }
    },
    {
      "id": "save",
      "type": "write",
      "collection": "leaderboard",
      "key": "{{steamId}}",
      "ops": [
        { "op": "set", "path": "playerName", "value": "{{input.playerName}}" },
        { "op": "set", "path": "score", "value": "{{input.score}}" },
        { "op": "set", "path": "steamId", "value": "{{steamId}}" }
      ]
    }
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
  "accessMode": "endpoint",
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
    "type": "object",
    "properties": {
      "text": { "type": "string" },
      "displayName": { "type": "string" }
    },
    "required": ["text", "displayName"]
  },
  "steps": [
    {
      "id": "chat",
      "type": "read",
      "collection": "chat_messages",
      "key": "{{steamId}}_default"
    },
    {
      "id": "save",
      "type": "write",
      "collection": "chat_messages",
      "key": "{{steamId}}_default",
      "ops": [
        { "op": "set", "path": "displayName", "value": "{{input.displayName}}" },
        { "op": "inc", "path": "messageCount", "value": 1 }
      ]
    }
  ],
  "response": { "status": 200, "body": { "ok": true, "messageCount": "{{chat.messageCount}}" } }
}
```
