# Sync Tool

**Editor > Network Storage > Sync Tool** keeps your server's collections, endpoints and workflows
as YAML files in your project and pushes or pulls them through the API of the server in your
Setup (`baseUrl`). Game values are edited in the server's owner console or over its API.

## Workflow

1. Write collections, endpoints, workflows, tests and libraries as YAML under
   `Editor/Network Storage/`.
2. **Check for Updates** compares local files with the server.
3. **Push** sends local changes; **Pull** downloads the server's version.
4. **View Diff** shows a side-by-side comparison before anything is overwritten.

| Icon | Meaning |
| --- | --- |
| ✓ | In sync |
| ▲ | Local only |
| ▼ | Remote only |
| ● | Local and remote differ |

Sync moves resource definitions, not player data. Player records live in the server's database.

Pull backs up each local file it overwrites, and an endpoint's script file too, as `<name>.bak.<yyyyMMddHHmmss>`.
Changing the publish target (Live or Next) regenerates the game's generated code, so the game reads the same target as the editor.

## Secret key transport

The secret key is sent only to an `https://` Base URL, or to `http://localhost` or a loopback address such as `http://127.0.0.1:5000`. Any other Base URL is refused before a request is made, and Setup shows a warning. Redirects are not followed, so the key is never replayed to another host.

## Game reachability

Setup and the Sync Tool header warn when s&box game code cannot reach the Base URL. The check uses the engine's own rule, so it matches what game calls can do. The warning never blocks anything: the Sync Tool still works, but game calls fail with `Access ... is not allowed`. Use a domain on any port, or localhost on port 80, 443, 8080 or 8443. IP-literal hosts are refused.

## Source files

```text
Editor/
  Network Storage/              # folder configurable in Setup
    .env                        # credentials (gitignored, never published)
    collections/<id>.collection.yml
    endpoints/<slug>.endpoint.yml
    workflows/<id>.workflow.yml
    tests/<id>.test.yml
    libraries/<id>.library.yml
```

`.yaml` is accepted too; prefer `.yml`. Each file starts with:

```yaml
sourceVersion: 1
kind: endpoint
id: mine-ore
definition:
  method: POST
  steps: []
```

Existing JSON resources stay readable and pushable; new and pulled resources are written as YAML.
JSON is not converted to YAML automatically. The full model is in
[source-authoring.md](../source-authoring.md) and
[source-authoring.schema.json](../source-authoring.schema.json), with examples in
[Examples/SourceAuthoring](../Examples/SourceAuthoring/).

## Validate from the command line

```sh
python Libraries/sboxcool.network-storage/Editor/source_compiler.py --project-root .
python Libraries/sboxcool.network-storage/Editor/sync.py --project-root . --sources --dry-run
```
