# Releasing the library

Maintainer notes. Users do not need this page.

## Versions

- `NetworkStorage.LibraryVersion` (`Code/Core/NetworkStorageClient.cs`) is the library SemVer, for
  example `1.2.0`. `PackageVersion` reports the same value. Python reads that constant, so there is
  no second SemVer to update.
- sbox.game assigns each publish a global **package revision**. No committed file predicts it;
  `.version` (`1.0.<revision>`) is only the Library Manager install marker. The editor's revision
  notice ignores `.version` when the library folder has a `.git` entry, because a checkout's
  committed `.version` does not track published revisions.
- `NetworkStorage.MinServerVersion` (`0.4.0`) is the self-hosted baseline. It is metadata, not a
  runtime rejection or a certification claim.

Editor tools read `Libraries/sboxcool.network-storage/.version`; a differently named manual
checkout omits the optional `x-ns-library-revision` header. Restart the editor after replacing a
library: package metadata is cached for the session. Runtime never reads `.version`, because game
filesystem mounts do not reliably expose the library source directory.

The integration candidate for the next patch reports `1.2.1`. It is not a published
Library Manager package until the native run, source tag and sbox.game publication
below are complete. Leave `.version` unchanged until a real package revision exists.

## Release sequence

1. Bump `LibraryVersion` in `Code/Core/NetworkStorageClient.cs`.
2. Publish only a `main` commit whose **Compat gate** is green: the public API check, and this
   library running inside s&box against the latest server release, the oldest supported one
   (`MinServerVersion`) and server `main` ([compat gate](compat-gate.md)). Green engine legs
   certify `Code/` and `Editor/` changes. If `main` moved since the last run, run the gate on that
   commit by hand (Actions > Compat gate > Run workflow).
3. Record one native run of the exact version before publishing: a Windows PC with the Steam build
   of s&box runs `tools/sbox-cli-e2e` against it and records a `pass` row in the server's
   [`compat/library-matrix.json`](https://github.com/sbox-cool/sbox-network-storage-server/blob/main/compat/library-matrix.json)
   (see the server's `docs/editor-test-plan.md` and [compatibility certification](compatibility-certification.md)).
4. Tag the tested source `vX.Y.Z`, then publish on sbox.game from that commit.
5. Within about 30 minutes the revision sync tags the matching commit `r<revision>` and creates its
   GitHub Release. Add the tested server, engine and provider pairings with evidence links to its
   notes. From then on the server's Compat gate runs this revision against every server change,
   and this repo's nightly gate runs it against the latest server release, opening a `compat`
   issue if it breaks.

For rollback, replace the **whole library folder** with a supported tagged release, then
republish the game. The last legacy source `0532c00` (package `1.0.309133`) is kept as
`v1.0.309133`.

## Revision mapping

`.github/workflows/sbox-revisions.yml` maps every published revision to the commit whose shipped
files match the revision's public manifest by size and CRC64 (line endings normalised), then
creates a lightweight tag `r<revision>` and a GitHub Release `<LibraryVersion> · sbox.game
r<revision>`. Only the current revision is marked latest; existing tags never move. Revisions
published from a commit that exists only on a remote branch are reported as such.

```sh
node .github/scripts/sbox-revisions.mjs --revision <id>
```

Publish only from a committed and pushed tree; the mapping can only find commits that exist on
the remote.

## Open questions

No documented editor API was found to open Library Manager or start an update from code, so the
editor prompts link to the sbox.game page and tell the developer to replace the whole folder.
Whether Library Manager allows selecting older revisions is **not verified**.
