# Coding agent tools

s&box's `sbox-cli` MCP bridge exposes a `network_storage` toolset while the editor, or a game or
dedicated server started with `-mcp`, is running. Start with `ns_status` (game code) or
`ns_editor_status` (editor), then `ns_sync_preflight` before `ns_sync_push`.

| Tool | Where | Description |
| --- | --- | --- |
| `ns_status` | Game and editor | Library version, configuration state (no keys), whether game code may reach the API. |
| `ns_configure` | Game and editor | Configure project ID, API key and base URL, or auto-configure when all are empty. |
| `ns_call_endpoint` | Game and editor | Call an endpoint; returns `ok`, `body`, `errorCode`, `errorMessage`. |
| `ns_get_document` | Game and editor | Read a document from a collection. |
| `ns_game_values` | Game and editor | Read game values. |
| `ns_editor_status` | Editor | Sync Tool config (no keys), local source counts, optional server check. |
| `ns_sync_preflight` | Editor | Dry-run the batch push; writes nothing. |
| `ns_sync_push` | Editor | Validate, preflight and push all sources; `verify` reads back and compares each resource. |
| `ns_auto_test` | Editor | Generate and run tests for one endpoint or all endpoints. |
| `ns_run_tests` | Editor | Run the saved test suite on the server. |

Endpoint and test failures come back as data, not tool errors. Tools that cannot run (for example
when the library is not configured) throw with a message saying what to fix.

A self-hosted server also has its own MCP server for operating it: `sbox-ns mcp`, see the server's
[MCP guide](https://github.com/sbox-cool/sbox-network-storage-server/blob/main/docs/mcp.md).
