# Compat gate

The library is LTS. Published games embed a copy of it for months, so a library
change that breaks the public API or the server contract breaks games that
cannot be updated. The **Compat gate** check (`.github/workflows/compat-gate.yml`)
holds those changes until they are approved and tested. It is the single required
check; it passes only when every job below passes or is skipped.

For the policy behind it, see [compatibility-certification.md](compatibility-certification.md)
and the server's [docs/compatibility.md](https://github.com/sbox-cool/sbox-network-storage-server/blob/main/docs/compatibility.md).

## Jobs

| Job | Runs on | What it proves | Blocks when |
|---|---|---|---|
| Classify changes | every event | Which paths changed (`Code/`, `Editor/`, `Assets/`, `*.sbproj`, the workflow) | never |
| API surface | pull requests | Public declarations in `Code/` and `Editor/` compared with the merge-base, using the pinned [ContractInspector](https://github.com/sbox-cool/sboxns.com/tree/ca71493c0617e675a99bfbcdbd0251c6fb86e7c9/tools/compatibility/ContractInspector) from sboxns.com. The sticky PR comment lists removed, changed and added symbols with file and line | a public symbol is removed or changed without `compat: breaking-approved`, or a file cannot be parsed |
| API surface comment | same-repo pull requests | Posts or updates the sticky comment (`<!-- compat-gate:api-surface -->`) | never (informational) |
| Editor evidence | pull requests | Whether `Code/` or `Editor/` changed; if so, whether a real s&box editor run is recorded | `Code/` or `Editor/` changed without `editor-tested` |
| Engine (server latest-release) | pushes to `main`, nightly, dispatch, and pull requests that touch engine-relevant paths | The library under test runs in a real s&box dedicated server with the **latest published server release** (`sbox-ns.exe` from the release zip, SHA256 checked against `SHA256SUMS`) | `sbox-cli-e2e` fails |
| Engine (server oldest-supported) | same as above | The same library against the release named by its own `NetworkStorage.MinServerVersion` (`v0.4.0` today), the oldest server it promises to work with, for owners who never update | `sbox-cli-e2e` fails |
| Engine (server main) | same as above | The same library runs against the server ref in `SERVER_REF` (`main`, which carries `run.cs` and `--server-binary` since server PR #97), built from source | `sbox-cli-e2e` fails |
| Engine (published revision) | pushes to `main` and nightly | The newest published library revision (`r<revision>` release tag) runs against the latest server release. Skipped while the library has no `r<revision>` release | `sbox-cli-e2e` fails |
| Nightly failure issue | pushes and nightly, on engine failure | Opens or updates one `compat` issue per failing leg | never (informational) |
| Compat gate | always | Aggregates every job above | any job failed or was cancelled |

Engine results come from hosted Windows runners. They are `ci-only` evidence in the
server's terms: they show the dedicated target works, but they never certify a
pairing and never replace an editor run.

## Labels

| Label | Who applies it | Effect |
|---|---|---|
| `compat: breaking-approved` | maintainer, after review | Lets the API surface check pass with removed or changed public symbols. The migration rules still apply: keep the old form for the required period, ship the replacement first, and link the server counterpart |
| `editor-tested` | maintainer, after the editor run below | Lets Editor evidence pass for `Code/` or `Editor/` changes |
| `pending-sbox-editor-test` | the gate, automatically | Marks a PR that still waits for an editor run. Added when the token can write it; otherwise add it by hand |
| `compat` | maintainers | Wire-affecting work and nightly failure issues |

Label changes re-run the gate automatically.

## Editor evidence (s&box editor run)

Hosted runners cannot run the s&box editor, so this step is manual and recorded in
the server repo:

1. Run `tools/sbox-cli-e2e` from `sbox-cool/sbox-network-storage-server` on a
   Windows PC with Steam, against this PR's library checkout (`--library <path>`).
2. Follow [docs/editor-test-plan.md](https://github.com/sbox-cool/sbox-network-storage-server/blob/main/docs/editor-test-plan.md):
   write down the five facts (engine build, library version/source, server build,
   OS, result) and record one `tested` row in `compat/library-matrix.json` there.
   A run missing any fact is invalid. Never record `pass` without a real editor run.
3. Apply `editor-tested` to the PR. The gate re-runs and passes.

Non-code PRs (docs, CI, tests outside `Code/`, `Editor/`) pass this job without a label.

## Running the gate manually

Use **Actions > Compat gate > Run workflow** on any branch:

- `library_ref`: library branch, tag or SHA to test. Defaults to the dispatched ref.
- `server_ref`: server branch or ref for the source-built leg and `tools/sbox-cli-e2e/run.cs`. Empty uses `SERVER_REF` in the workflow.

The latest-release leg always uses the newest server release.

## Operating notes

- The workflow never runs with secrets. Pull requests from forks run with a read-only token and skip the sticky comment and label writes.
- Engine legs on pull requests run only when `Code/`, `Editor/`, `Assets/`, a `*.sbproj` or the workflow itself changes.
- The release zip is verified against the release's `SHA256SUMS`. The signed checksum file's signature is not checked by this workflow.
- Pins: `ContractInspector` at sboxns.com `ca71493c0617e675a99bfbcdbd0251c6fb86e7c9`; s&box engine built from `Facepunch/sbox-public` at its default branch on each run. Update the sboxns pin deliberately, in the same PR as any inspector change that needs it.
