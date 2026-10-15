import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  blobDigests,
  chooseCommit,
  compareToManifest,
  crc64Hex,
  isGenerated,
  isShippable,
  manifestExclusionViolations,
  parseTree,
  planTag,
} from './sbox-revisions.mjs';

const text = (s) => Buffer.from(s, 'utf8');

// Build a manifest Map and a commit tree from path -> content maps.
// The commit tree goes through `git ls-tree -z` text so the shippable filter is exercised.
function fixture({ manifestFiles, commitFiles }) {
  const manifest = new Map(
    Object.entries(manifestFiles).map(([p, b]) => [p, { size: b.length, crc: crc64Hex(b), url: `u/${p}` }]),
  );
  const digests = new Map();
  let lsTree = '';
  for (const [p, b] of Object.entries(commitFiles)) {
    const sha = `blob-${p}`;
    digests.set(sha, blobDigests(b));
    lsTree += `100644 blob ${sha}\t${p}\0`;
  }
  return {
    manifest,
    tree: parseTree(lsTree),
    digestsOf: (sha) => digests.get(sha),
  };
}

const compare = (f) => compareToManifest(f.manifest, f.tree, f.digestsOf);

test('CRC-64 matches the reference check value', () => {
  assert.equal(crc64Hex(text('123456789')), 'ae8b14860a799888');
});

test('CRLF-published files match LF git blobs', () => {
  const f = fixture({
    manifestFiles: { 'Code/A.cs': text('a\r\nb\r\n') },
    commitFiles: { 'Code/A.cs': text('a\nb\n') },
  });
  assert.equal(compare(f).verdict, 'exact');
});

test('untracked generated manifest files are ignored', () => {
  const f = fixture({
    manifestFiles: {
      'Code/A.cs': text('x'),
      '.bin/manifest.json': text('{}'),
      'Code/.bin/package.xml': text('<x/>'),
      'Assets/m.vmat_c': text('compiled'),
    },
    commitFiles: { 'Code/A.cs': text('x') },
  });
  const r = compare(f);
  assert.equal(r.verdict, 'exact');
  assert.deepEqual(r.ignoredGenerated, ['.bin/manifest.json', 'Assets/m.vmat_c', 'Code/.bin/package.xml']);
  assert.deepEqual(r.generatedMismatch, []);
});

test('tracked generated files that differ do not block the verdict but are reported', () => {
  const f = fixture({
    manifestFiles: { 'Code/A.cs': text('x'), 'Assets/m.vmat_c': text('compiled') },
    commitFiles: { 'Code/A.cs': text('x'), 'Assets/m.vmat_c': text('local build') },
  });
  const r = compare(f);
  assert.equal(r.verdict, 'exact');
  assert.deepEqual(r.generatedMismatch, ['Assets/m.vmat_c']);
});

test('a tracked source file that differs is a failure', () => {
  const f = fixture({
    manifestFiles: { 'Code/A.cs': text('published') },
    commitFiles: { 'Code/A.cs': text('source') },
  });
  const r = compare(f);
  assert.equal(r.verdict, 'none');
  assert.deepEqual(r.differing, ['Code/A.cs']);
});

test('reverse check: tracked shippable file absent from manifest rejects the commit', () => {
  const f = fixture({
    manifestFiles: { 'Code/A.cs': text('x') },
    commitFiles: { 'Code/A.cs': text('x'), 'Code/New.cs': text('y') },
  });
  const r = compare(f);
  assert.equal(r.verdict, 'none');
  assert.deepEqual(r.missing, ['Code/New.cs']);
});

test('dot-paths, build directories, csproj and launchSettings are not shipped', () => {
  const f = fixture({
    manifestFiles: { 'Code/A.cs': text('x') },
    commitFiles: {
      'Code/A.cs': text('x'),
      '.gitignore': text('bin/'),
      '.github/ci.yml': text('on: push'),
      'Code/obj/Debug/network-storage.dll': text('dll'),
      'Editor/bin/Debug/x.dll': text('dll'),
      'Editor/x.csproj': text('<Project/>'),
      'Editor/Properties/launchSettings.json': text('{}'),
    },
  });
  assert.equal(compare(f).verdict, 'exact');
  assert.equal(isShippable('Editor/Properties/launchSettings.json'), false);
  assert.equal(isShippable('Code/obj/Debug/x.dll'), false);
  assert.equal(isShippable('Editor/Properties/Other.cs'), true);
});

test('publisher metadata is reported, not a failure; other dot-files are exceptions', () => {
  const ok = fixture({
    manifestFiles: { 'Code/A.cs': text('x'), '.version': text('1.0.1'), '.mcp.json': text('{}') },
    commitFiles: { 'Code/A.cs': text('x') },
  });
  const r = compare(ok);
  assert.equal(r.verdict, 'exact');
  assert.deepEqual(r.publisherMetadata, ['.mcp.json', '.version']);
  assert.deepEqual(manifestExclusionViolations(ok.manifest), []);

  const bad = fixture({
    manifestFiles: { 'Code/A.cs': text('x'), '.github/ci.yml': text('on: push') },
    commitFiles: { 'Code/A.cs': text('x') },
  });
  const b = compare(bad);
  assert.equal(b.verdict, 'none');
  assert.deepEqual(b.extra, ['.github/ci.yml']);
  assert.deepEqual(manifestExclusionViolations(bad.manifest), ['.github/ci.yml']);
});

test('sbproj-only difference is the weaker source verdict', () => {
  const f = fixture({
    manifestFiles: { 'Code/A.cs': text('x'), 'network-storage.sbproj': text('{"AI":{}}') },
    commitFiles: { 'Code/A.cs': text('x'), 'network-storage.sbproj': text('{}') },
  });
  const r = compare(f);
  assert.equal(r.verdict, 'source');
  assert.deepEqual(r.differing, ['network-storage.sbproj']);
});

test('isGenerated covers .bin at any depth, compiled outputs and meta files', () => {
  assert.equal(isGenerated('.bin/manifest.json'), true);
  assert.equal(isGenerated('Code/.bin/package.xml'), true);
  assert.equal(isGenerated('Assets/a.generated.vtex_c'), true);
  assert.equal(isGenerated('Assets/a.meta'), true);
  assert.equal(isGenerated('Code/A.cs'), false);
});

test('commit selection: exact beats source, then newest preceding publish, main preferred', () => {
  const created = Date.parse('2026-10-11T00:40:00Z');
  const c = (sha, date, mainReachable, verdict) => ({
    sha,
    dateMs: Date.parse(date),
    mainReachable,
    verdict,
  });

  // exact beats a newer source match
  assert.equal(
    chooseCommit(created, [c('src', '2026-10-10T00:00:00Z', true, 'source'), c('ex', '2026-07-16T00:00:00Z', true, 'exact')]).sha,
    'ex',
  );

  // newest commit at or before publish wins over later ones
  assert.equal(
    chooseCommit(created, [
      c('old', '2026-07-01T00:00:00Z', true, 'exact'),
      c('new', '2026-10-10T00:00:00Z', true, 'exact'),
      c('after', '2026-10-12T00:00:00Z', true, 'exact'),
    ]).sha,
    'new',
  );

  // main preferred over a newer branch-only commit that also precedes publish
  assert.equal(
    chooseCommit(created, [
      c('main', '2026-07-16T00:00:00Z', true, 'exact'),
      c('branch', '2026-10-10T00:00:00Z', false, 'exact'),
    ]).sha,
    'main',
  );

  // fallback when nothing precedes publish: newest of the pool, main preferred
  assert.equal(
    chooseCommit(created, [
      c('late-branch', '2026-10-13T00:00:00Z', false, 'exact'),
      c('late-main', '2026-10-12T00:00:00Z', true, 'exact'),
    ]).sha,
    'late-main',
  );

  assert.equal(chooseCommit(created, []), null);
});

test('existing tags are never moved', () => {
  assert.equal(planTag('r1', 'abc', null), 'create');
  assert.equal(planTag('r1', 'abc', { sha: 'abc', type: 'commit' }), 'exists');
  assert.throws(() => planTag('r1', 'abc', { sha: 'def', type: 'commit' }), /refusing to move/);
  assert.throws(() => planTag('r1', 'abc', { sha: 'abc', type: 'tag' }), /refusing to move/);
});
