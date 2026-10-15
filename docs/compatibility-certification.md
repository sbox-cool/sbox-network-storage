# Compatibility certification (library side)

The library and the server are both LTS. Games embed the library for months;
servers update independently. A pairing is certified once with real evidence,
then held until a fundamental change forces re-testing.

Tracking: [library tracker #6](https://github.com/sbox-cool/sbox-network-storage/issues/6),
[server epic #57](https://github.com/sbox-cool/sbox-network-storage-server/issues/57).
Backend rules: [server docs/compatibility.md](https://github.com/sbox-cool/sbox-network-storage-server/blob/main/docs/compatibility.md).
Backend PRs: [server #66](https://github.com/sbox-cool/sbox-network-storage-server/pull/66), [#67](https://github.com/sbox-cool/sbox-network-storage-server/pull/67), [#70](https://github.com/sbox-cool/sbox-network-storage-server/pull/70).

## Certify once

A certification is one server `tested` row: exact server version + source
commit, library version + package revision (or null when genuinely
unassigned), s&box engine build, database
providers actually exercised, a `pass` result, and evidence URLs (run logs,
both-repo PRs/issues). `ci-only` backend HTTP tests never certify a pairing.
A certified pattern covers that pattern only (revision-init, save/load round
trip, endpoint success/error, game values, queries, heartbeat/analytics,
Sync Tool push/pull, revision handling). New patterns need new rows.

## Fundamental change = re-test before release

These invalidate affected certifications and MUST be re-tested on both sides
before release:

- Library public method added/removed/renamed, signature or behavior changed.
- Request/response shapes, error codes/envelopes, defaults, retry/fallback.
- Auth handling (public/secret/dedicated keys, auth sessions, encryption).
- Save/endpoint/query/game-value semantics, revision-status handling.
- Sync Tool authoring model, preflight/push/pull/promote behavior (including
  server-side promotion).
- Identity headers/body versions, server-advertised capability gating, update
  prompts.
- Any change the server lists as fundamental in its compatibility doc.

Internal refactors with no wire, semantic or storage-contract change do not
invalidate certification, but still require `UnitTests/` and
`Tests/test_source_compiler.py` plus the backend parity corpus on the server.

## LTS scrutiny for every PR/issue

- Label wire-affecting changes `compat` with `priority: P0/P1/P2`; link the
  server counterpart issue/PR and the dependency order. State additive vs
  deprecated vs removal, which old games stay working, the replacement and
  sunset, and the test evidence.
- Library public methods and server endpoints are LTS surface: no silent
  rename, removal, narrowing or meaning change. Ship a replacement first,
  keep the old form for at least six months after its successor ships with
  at least 90 days' removal notice, add a migration guide, and record real
  old-client + new-client evidence plus unidentified-traffic impact. The later
  date wins; announced sunsets cannot be shortened.
- Reviewers MUST reject silent removals, narrowed support, renames without
  translation, shortened notice, patch-only syntax changes, and fundamental
  changes without new certification rows. When in doubt, keep the old form
  and add side-by-side. Players cannot update the library inside a published
  game; only a game republish delivers a fix.

## Automated issue tracking and PR gate

`Monitor untested builds` checks main pushes and runs every 15 minutes. It
observes the actual GitHub head, published s&box revision and latest stable
server release. Missing exact-build validation updates a stable owned issue,
not a new issue on every poll. Partial coverage, old heads, HTTP-only tests,
source recompilation and different providers do not certify a published pair.
Human investigation notes and labels are preserved.

`Compatibility` compares the PR's C# declarations and meaningful source tokens
against its merge base using trusted Roslyn tooling. It never executes PR build
files in a write-token job. Existing public declaration removal/change blocks
the check; additions, defaults, body/private helper changes and editor changes
require appropriate review and evidence. Static analysis cannot prove wire or
behavior compatibility, so it cannot replace LTS review or native tests.

Select one compatibility classification in the PR template. For meaningful
source/CI changes, a write/maintain/admin collaborator must comment:

```text
/compatibility-review FULL_HEAD_SHA none|additive-default-off|changed|library-code
```

Use the single matching classification, not the literal pipe-separated list.
The acknowledgement is head-specific and cannot waive public API removal,
generated binary/build metadata changes or missing native/editor validation.
Candidate project settings are deliberately not executed by the trusted source
harness, so its pass cannot approve changed compiler/build flags. Authoring-schema
changes also require editor evidence, not just runtime tests.

`Native compatibility` is manually dispatched only after that acknowledgement,
for an open same-repository PR. It compares the published baseline's runtime
source and exact candidate runtime source against the same tagged server source,
engine and SQLite provider. Both must exercise every inventoried method before
the native status passes. This is not a signed server binary, loaded package
archive, PostgreSQL, dedicated-auth or editor certificate. Editor changes require
separate reviewed canonical editor evidence or a trusted editor workflow.

Configure an approved `self-hosted, sbox-native` runner, `SBOX_GAME` and
`SBOX_CERTIFICATION_RUNNER_ENABLED=true` before dispatching. Never execute fork
code on that persistent runner. The native job has read-only repository
permissions; only a separate hosted job publishes its result.

The workflows pin shared tooling to an immutable commit in `sbox-cool/sboxns.com`.
Policy details and limitations live in its `tools/compatibility/README.md`.
Library main requires `Network Storage / compatibility` with up-to-date branches,
including administrators; force pushes and deletion are disabled. The bootstrap
workflow PR still needs a real maintainer exact-head acknowledgement before its
check can pass. Scheduled/event automation starts only after the workflows merge
to main. No native runner is configured by adding these files alone.
