# Generated Documentation — MCP Reference

Documentation generated from the Network Storage MCP server logic. These docs mirror the exact content, validation rules, examples, and error patterns embedded in the MCP tools.

## Core Concepts

| Document | Description |
|----------|-------------|
| [Overview](overview.md) | Architecture, data flow, key principles |
| [Setup](setup.md) | Installation, credential configuration, testing |
| [Security](security.md) | Key types, what ships, what stays local |

## JSON File Formats

| Document | Description |
|----------|-------------|
| [Collections](collections.md) | Collection JSON schema, fields, validation rules |
| [Endpoints](endpoints.md) | Endpoint JSON schema, slug rules, validation rules |
| [Workflows](workflows.md) | Workflow JSON schema, onFail options, compound conditions |
| [Step Types](steps.md) | Complete reference for all 7 step types with required fields |
| [Templates](templates.md) | `{{template}}` syntax, sources, patterns, validation |

## Configuration

| Document | Description |
|----------|-------------|
| [Environment Config](env-config.md) | `.env` file format, required/optional keys, validation |
| [Constraints](constraints.md) | All naming rules, limits, valid enums, required fields |

## Runtime & Tools

| Document | Description |
|----------|-------------|
| [Runtime Client](runtime-client.md) | C# API reference for game code |
| [Sync Tool](sync-tool.md) | Editor sync tool operations and workflow |

## Error Reference

| Document | Description |
|----------|-------------|
| [Error Handling](error-handling.md) | Rejection flow, response formats, optimistic updates |
| [Error Patterns](error-patterns.md) | All 17 diagnosable error patterns with causes and fixes |

## Examples

| Document | Description |
|----------|-------------|
| [Examples](examples.md) | Complete JSON examples for 5 game scenarios |

### Scenarios Covered

- **Player Data** — basic player collection + init endpoint
- **Inventory** — backpack capacity, ore mining, tier validation
- **Currency** — selling items, buying upgrades, currency checks
- **Leaderboard** — global collection, score submission
- **Chat** — per-player message storage, message counting
