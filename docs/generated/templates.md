# Template Syntax — `{{template}}` Reference

Templates let you reference dynamic values in endpoint steps. They are resolved at execution time by the server.

## Syntax

`{{source.path}}` — reference a value from the execution context.

## Sources

| Source | Description | Example |
|--------|------------|---------|
| `input` | Request input fields | `{{input.oreType}}`, `{{input.amount}}` |
| `steamId` | The player's Steam ID | `{{steamId}}` |
| `{stepId}` | Data from a `read` or `lookup` step | `{{player.currency}}`, `{{ore.tier}}` |
| `{transformId}` | Result of a `transform` step | `{{sale_value}}`, `{{neg_cost}}` |
| `values` | Game values constants | `{{values.progression.xp_per_level}}` |

## Path Traversal

Use dots to access nested fields:
- `{{player.inventory.items}}` — nested object access
- `{{player.ores.iron}}` — dynamic key access

## Dynamic Keys

Templates can be used inside field paths:
- `{{player.ores.{{input.oreType}}}}` — resolves the ore type from input first

## Negation

Prefix with `-` for numeric negation:
- `{{-input.amount}}` — negates the value

## Where Templates Work

Templates are resolved in these step fields:

- `read.key` — the record key
- `write.key` — the record key
- `write.ops[].path` — dot-path to field (e.g. `ores.{{input.ore_id}}`)
- `write.ops[].value` — the value to apply
- `write.ops[].reason` — audit trail reason
- `transform.expression` — the math expression
- `condition.check.field` (or `condition.check.left`) — left-hand value to check
- `condition.check.value` (or `condition.check.right`) — right-hand value to compare
- `condition.onFail.message` — error message
- `lookup.where.value` — the lookup match value
- `filter.where.value` — the filter comparison value
- `delete.key` — the record key to delete
- `workflow.params.*` — parameter values passed to a multi-step workflow
- `response.body` — values in the response body

## Common Patterns

```json
// Reference input
"value": "{{input.amount}}"

// Reference read data
"field": "{{player.currency}}"

// Reference transform result (no .result suffix needed)
"value": "{{sale_value}}"

// Dynamic field path in write ops
{ "op": "inc", "path": "ores.{{input.oreType}}", "value": "{{input.amount}}" }

// Negation (for decrementing)
"expression": "0 - {{upgrade_cost}}"

// Game values constants
"expression": "max({{player.xp}}, {{values.progression.xp_per_level}})"
```

## Validation Rules

The MCP validates template syntax against these rules:

- Templates must use double braces: `{{...}}` (single braces `{...}` are flagged as possible mistakes)
- Empty templates `{{}}` are flagged
- Templates without a dot-path (e.g. `{{foo}}` instead of `{{input.foo}}`) are warned — except for transform step references which are accessed as `{{stepId}}` directly
- Negation templates must follow `{{-source.field}}` format
