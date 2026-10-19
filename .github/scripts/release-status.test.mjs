import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  behindStatus,
  buildStatus,
  ciConclusion,
  engineFromSummary,
  INSTALL_MAIN,
  libraryVersionFromSource,
  newestEngineTag,
  newestRevisionRelease,
  PENDING_CAP,
  pickCiRun,
  publishedAtOf,
  revisionOfTag,
  selectPending,
  STORE_URL,
} from './release-status.mjs';

const SHA_A = 'a'.repeat(40);
const SHA_B = 'b'.repeat(40);

function pr(number, mergedAt, base = 'main') {
  return {
    number,
    title: `PR ${number}`,
    merged_at: mergedAt,
    html_url: `https://github.com/example/repo/pull/${number}`,
    base: { ref: base },
  };
}

test('revisionOfTag accepts only r<digits>', () => {
  assert.equal(revisionOfTag('r423774'), 423774);
  assert.equal(revisionOfTag('v1.0.309133'), null);
  assert.equal(revisionOfTag(undefined), null);
});

test('newestRevisionRelease picks the highest non-draft revision', () => {
  const releases = [
    { tag_name: 'r100', draft: false },
    { tag_name: 'r900', draft: true },
    { tag_name: 'r423774', draft: false, published_at: 'x' },
    { tag_name: 'v1.0.309133', draft: false },
    { tag_name: 'r422519', draft: false },
  ];
  const best = newestRevisionRelease(releases);
  assert.equal(best.tag, 'r423774');
  assert.equal(best.revision, 423774);
  assert.equal(newestRevisionRelease([{ tag_name: 'v1', draft: false }]), null);
});

test('publishedAtOf prefers the sbox.game date in the release body', () => {
  const release = {
    body: '# x\n\n- **Published:** 2026-10-15T08:12:25+00:00\n- **Engine:** 30\n',
    published_at: '2026-10-15T22:59:52Z',
  };
  assert.equal(publishedAtOf(release), '2026-10-15T08:12:25.000Z');
});

test('publishedAtOf falls back to the release date', () => {
  assert.equal(
    publishedAtOf({ body: 'no date here', published_at: '2026-10-15T22:59:52Z' }),
    '2026-10-15T22:59:52Z',
  );
  assert.equal(publishedAtOf({ body: null, published_at: null }), null);
});

test('newestEngineTag compares dotted numbers, not strings', () => {
  const tags = [
    { name: '26.9.29', sha: SHA_A },
    { name: '26.10.02', sha: SHA_B },
    { name: '26.10.13', sha: SHA_A },
    { name: 'nightly', sha: SHA_B },
  ];
  assert.deepEqual(newestEngineTag(tags), { name: '26.10.13', sha: SHA_A });
  assert.equal(newestEngineTag([{ name: 'nightly', sha: SHA_A }]), null);
});

test('libraryVersionFromSource reads the constant and falls back to legacy 1.0', () => {
  const source = 'public static class X {\n\tpublic const string LibraryVersion = "1.2.0";\n}';
  assert.equal(libraryVersionFromSource(source), '1.2.0');
  assert.equal(libraryVersionFromSource('class X {}'), '1.0');
  assert.equal(libraryVersionFromSource(''), '1.0');
});

test('selectPending: no merged PRs after the base gives an empty list and null', () => {
  assert.deepEqual(selectPending([]), { pending: [], pendingSince: null });
  // Unmerged and non-main-base PRs do not count.
  const result = selectPending([pr(1, null), pr(2, '2026-10-01T00:00:00Z', 'feat/x'), null]);
  assert.deepEqual(result, { pending: [], pendingSince: null });
});

test('selectPending: newest first, deduped, pendingSince is the oldest', () => {
  const result = selectPending([
    pr(37, '2026-10-16T10:00:00Z'),
    pr(12, '2026-10-17T10:00:00Z'),
    pr(41, '2026-10-18T10:00:00Z'),
    pr(37, '2026-10-16T10:00:00Z'),
  ]);
  assert.deepEqual(
    result.pending.map((item) => item.number),
    [41, 12, 37],
  );
  assert.equal(result.pending[0].url, 'https://github.com/example/repo/pull/41');
  assert.equal(result.pendingSince, '2026-10-16T10:00:00Z');
});

test('selectPending caps the list but keeps pendingSince over the full set', () => {
  const prs = Array.from({ length: PENDING_CAP + 5 }, (_, i) =>
    pr(i + 1, new Date(Date.UTC(2026, 0, 1) + i * 3600_000).toISOString()),
  );
  const result = selectPending(prs);
  assert.equal(result.pending.length, PENDING_CAP);
  assert.equal(result.pending[0].number, PENDING_CAP + 5);
  assert.equal(result.pendingSince, new Date(Date.UTC(2026, 0, 1)).toISOString());
});

test('behindStatus: engine newer than publish counts whole days to generatedAt', () => {
  assert.deepEqual(
    behindStatus({
      publishedAt: '2026-10-15T08:12:25Z',
      engineDate: '2026-10-13T00:00:00Z',
      generatedAt: '2026-10-16T12:00:00Z',
    }),
    { libraryBehind: false, daysBehind: 0 },
  );
  assert.deepEqual(
    behindStatus({
      publishedAt: '2026-10-10T00:00:00Z',
      engineDate: '2026-10-13T06:00:00Z',
      generatedAt: '2026-10-16T12:00:00Z',
    }),
    { libraryBehind: true, daysBehind: 3 },
  );
});

test('behindStatus: equal dates are not behind; missing inputs stay null', () => {
  assert.deepEqual(
    behindStatus({
      publishedAt: '2026-10-13T00:00:00Z',
      engineDate: '2026-10-13T00:00:00Z',
      generatedAt: '2026-10-16T00:00:00Z',
    }),
    { libraryBehind: false, daysBehind: 0 },
  );
  assert.deepEqual(behindStatus({ publishedAt: null, engineDate: null, generatedAt: 'x' }), {
    libraryBehind: null,
    daysBehind: null,
  });
});

test('CI helpers map run states and find the run for the head sha', () => {
  assert.equal(ciConclusion(undefined), 'unknown');
  assert.equal(ciConclusion({ status: 'in_progress', conclusion: null }), 'pending');
  assert.equal(ciConclusion({ status: 'completed', conclusion: 'success' }), 'success');
  assert.equal(ciConclusion({ status: 'completed', conclusion: 'timed_out' }), 'failure');
  assert.equal(ciConclusion({ status: 'completed', conclusion: 'cancelled' }), 'unknown');
  const runs = [
    { head_sha: SHA_B, id: 2 },
    { head_sha: SHA_A, id: 1 },
  ];
  assert.equal(pickCiRun(runs, SHA_A).id, 1);
  assert.equal(pickCiRun(runs, 'c'.repeat(40)), null);
});

test('engineFromSummary extracts the sbox-public commit', () => {
  const summary = `# Compat\n- Engine: 0000000 (sbox-public ${SHA_A}) (dedicated); OS: linux\n`;
  assert.equal(engineFromSummary(summary), `sbox-public@${SHA_A}`);
  assert.equal(engineFromSummary('- Engine: 26.10.13 (dedicated)'), null);
  assert.equal(engineFromSummary(null), null);
});

test('buildStatus with no inputs keeps the full shape and uses nulls', () => {
  const status = buildStatus({ generatedAt: '2026-10-16T12:00:00Z' });
  assert.deepEqual(status, {
    schema: 1,
    generatedAt: '2026-10-16T12:00:00Z',
    library: {
      published: { revision: null, version: null, commit: null, publishedAt: null, url: STORE_URL },
      main: {
        version: null,
        commit: null,
        committedAt: null,
        ci: { conclusion: 'unknown', runUrl: null, engine: null },
      },
      pending: null,
      pendingSince: null,
      installMain: INSTALL_MAIN,
    },
    engine: { latest: { version: null, date: null, url: null }, libraryBehind: null, daysBehind: null },
    server: {
      latestRelease: { version: null, publishedAt: null, url: null },
      main: { commit: null, committedAt: null },
      pending: null,
      pendingSince: null,
    },
  });
});

test('buildStatus assembles a full status and computes engine lag', () => {
  const status = buildStatus({
    generatedAt: '2026-10-16T12:00:00Z',
    library: {
      published: {
        revision: 423774,
        version: '1.1.0',
        commit: SHA_A,
        publishedAt: '2026-10-10T08:12:25.000Z',
      },
      main: {
        version: '1.2.0',
        commit: SHA_B,
        committedAt: '2026-10-16T09:00:00Z',
        ci: { conclusion: 'success', runUrl: 'https://example/run/1', engine: `sbox-public@${SHA_A}` },
      },
      pending: selectPending([pr(41, '2026-10-16T09:00:00Z')]).pending,
      pendingSince: '2026-10-16T09:00:00Z',
    },
    engine: {
      latest: { version: '26.10.13', date: '2026-10-13T00:00:00Z', url: 'https://example/tree' },
    },
    server: {
      latestRelease: { version: '0.5.4', publishedAt: '2026-10-14T12:38:38Z', url: 'https://example/v0.5.4' },
      main: { commit: SHA_A, committedAt: '2026-10-16T08:00:00Z' },
      pending: [],
      pendingSince: null,
    },
  });
  assert.equal(status.library.published.url, STORE_URL);
  assert.equal(status.library.pending[0].number, 41);
  assert.deepEqual(status.library.pending[0], {
    number: 41,
    title: 'PR 41',
    mergedAt: '2026-10-16T09:00:00Z',
    url: 'https://github.com/example/repo/pull/41',
  });
  assert.deepEqual(status.engine, {
    latest: { version: '26.10.13', date: '2026-10-13T00:00:00Z', url: 'https://example/tree' },
    libraryBehind: true,
    daysBehind: 3,
  });
  assert.deepEqual(status.server.pending, []);
});
