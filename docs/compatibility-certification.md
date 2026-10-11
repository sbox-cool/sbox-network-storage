# Compatibility certification (library side)

The library and the server are both LTS. Games embed the library for months;
servers update independently. A pairing is certified once with real evidence,
then held until a fundamental change forces re-testing.

Tracking: [library tracker #6](https://github.com/sbox-cool/sbox-network-storage/issues/6),
[server epic #57](https://github.com/sbox-cool/sbox-network-storage-server/issues/57).
Backend rules: [server docs/compatibility.md](https://github.com/sbox-cool/sbox-network-storage-server/blob/main/docs/compatibility.md).
Backend PR: [server #68](https://github.com/sbox-cool/sbox-network-storage-server/pull/68).

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
