# Template Syntax — `{{template}}` Reference

Templates let you reference dynamic values in endpoint steps. They are resolved at execution time by the server.

## Syntax

`{{source.path}}` — reference a value from the execution context.

## Sources

| Source | Description | Example |
|--------|------------|---------|
| `input` | Request input fields | `{{input.oreType}}`, `{{input.amount}}` |
| `{alias}` | Data from a `read` or `lookup` step | `{{player.currency}}`, `{{ore.tier}}` |
| `values` | Game values / tables | `{{values.ore_types}}` |

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
- `transform.field` — the target field path
- `transform.value` — the value to apply
- `condition.field` — the field to check
- `condition.value` — the comparison value
- `lookup.value` — the lookup key value
- `filter.value` — the filter comparison value
- `response.body` — values in the response body
- `onFail.errorMessage` — workflow error messages

## Common Patterns

```json
// Reference input
"value": "{{input.amount}}"

// Reference read data
"field": "player.currency"
"value": "{{player.currency}}"

// Dynamic field path
"field": "player.ores.{{input.oreType}}"

// Negation
"value": "{{-input.cost}}"
```

## Validation Rules

The MCP validates template syntax against these rules:

- Templates must use double braces: `{{...}}` (single braces `{...}` are flagged as possible mistakes)
- Empty templates `{{}}` are flagged
- Templates without a dot-path (e.g. `{{foo}}` instead of `{{input.foo}}`) are warned
- Negation templates must follow `{{-source.field}}` format
