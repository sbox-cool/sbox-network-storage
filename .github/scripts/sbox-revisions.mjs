#!/usr/bin/env node
// Links sbox.game package revisions of sboxcool.network-storage to the GitHub
// commit whose shipped files match the revision's public manifest (size and
// CRC64 per file). With --apply, publishes lightweight tags `r<revision>` and
// GitHub releases for matched revisions. Zero dependencies; Node 22+.
//
//   node .github/scripts/sbox-revisions.mjs [--revision <id>] [--json <file>]
//     [--fetch] [--apply] [--cache-dir <dir>]

import { execFileSync, spawn } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const PACKAGE = 'sboxcool.network-storage';
export const STORE_URL = 'https://sbox.game/sboxcool/network-storage';
export const MAIN_REF = 'refs/remotes/origin/main';
export const LIBRARY_VERSION_FILE = 'Code/Core/NetworkStorageClient.cs';
const REMOTE_PREFIX = 'refs/remotes/origin/';
const USER_AGENT = 'sbox-network-storage-revsync';
const VERSIONS_URL = `https://public.facepunch.com/sbox/package/versions/2/${PACKAGE}`;
const CURRENT_URL = `https://public.facepunch.com/sbox/package/get/2/${PACKAGE}`;

export const manifestUrl = (revision) =>
  `https://public.facepunch.com/sbox/package/manifest/1/${PACKAGE}/${revision}`;

const USAGE = `Usage: node .github/scripts/sbox-revisions.mjs [options]
  --revision <id>   only this sbox.game revision
  --json <file>     write the full report
  --fetch           git fetch --prune origin before matching
  --apply           create missing r<revision> tags, releases and the current-revision issue (GitHub)
  --cache-dir <dir> manifest/file cache (default: <tmpdir>/sbox-revisions-cache)`;

// ---------------------------------------------------------------- CRC64 ----
// Reflected CRC-64 (poly 0x9A6C9329AC4BC9B5, init/xorout all ones), table-driven
// on 32-bit halves so large blobs stay fast without BigInt.
const POLY_HI = 0x9a6c9329;
const POLY_LO = 0xac4bc9b5;
const TABLE_HI = new Uint32Array(256);
const TABLE_LO = new Uint32Array(256);
for (let n = 0; n < 256; n++) {
  let hi = 0;
  let lo = n;
  for (let k = 0; k < 8; k++) {
    const odd = lo & 1;
    lo = ((lo >>> 1) | (hi << 31)) >>> 0;
    hi >>>= 1;
    if (odd) {
      hi = (hi ^ POLY_HI) >>> 0;
      lo = (lo ^ POLY_LO) >>> 0;
    }
  }
  TABLE_HI[n] = hi;
  TABLE_LO[n] = lo;
}

/** CRC-64 of a buffer as 16 lowercase hex digits. */
export function crc64Hex(buf) {
  let hi = 0xffffffff;
  let lo = 0xffffffff;
  for (let i = 0; i < buf.length; i++) {
    const idx = (lo ^ buf[i]) & 0xff;
    const nlo = ((lo >>> 8) | (hi << 24)) >>> 0;
    const nhi = hi >>> 8;
    lo = (nlo ^ TABLE_LO[idx]) >>> 0;
    hi = (nhi ^ TABLE_HI[idx]) >>> 0;
  }
  hi = (hi ^ 0xffffffff) >>> 0;
  lo = (lo ^ 0xffffffff) >>> 0;
  return hi.toString(16).padStart(8, '0') + lo.toString(16).padStart(8, '0');
}

// ------------------------------------------------------- content digests ----
const digest = (buf) => `${buf.length}:${crc64Hex(buf)}`;
const toLF = (buf) =>
  buf.includes(0x0d) ? Buffer.from(buf.toString('latin1').replace(/\r\n/g, '\n'), 'latin1') : buf;
const toCRLF = (buf) => Buffer.from(buf.toString('latin1').replace(/\n/g, '\r\n'), 'latin1');

/**
 * Every "size:crc64" a git blob can ship as: raw, LF-normalized and CRLF-normalized.
 * The publisher ships CRLF (Windows) while git stores LF.
 */
export function blobDigests(raw) {
  const lf = toLF(raw);
  return new Set([digest(raw), digest(lf), digest(toCRLF(lf))]);
}

// -------------------------------------------------------- path rules -------
/**
 * Publisher ships a tracked path unless a segment starts with "." or is a build directory
 * (bin/, obj/), or it is a csproj/launchSettings. Verified against every manifest.
 */
export function isShippable(p) {
  return (
    !p.split('/').some((segment) => segment.startsWith('.') || segment === 'bin' || segment === 'obj') &&
    !p.endsWith('.csproj') &&
    !/(^|\/)Properties\/launchSettings\.json$/.test(p)
  );
}

/** Publish-generated paths (`.bin/` at any depth, compiled `*_c`, `*.meta`); ignored only when untracked. */
export function isGenerated(p) {
  return /(^|\/)\.bin\//.test(p) || /(^|\/)[^/]*_c$/.test(p) || p.endsWith('.meta');
}

/** Dot-files the publisher writes itself (package revision marker, editor MCP config); reported, not failures. */
export const PUBLISHER_METADATA = new Set(['.version', '.mcp.json']);

/** Parse `git ls-tree -r -z` output into shippable blob paths -> blob sha. */
export function parseTree(output) {
  const tree = new Map();
  for (const record of output.split('\0')) {
    if (!record) continue;
    const tab = record.indexOf('\t');
    const [, type, sha] = record.slice(0, tab).split(' ');
    const p = record.slice(tab + 1);
    if (type === 'blob' && isShippable(p)) tree.set(p, sha);
  }
  return tree;
}

/**
 * Compare a commit's shippable tree against a manifest (Map path -> {size, crc}).
 * exact: every non-generated shippable tracked file matches and no non-generated file is missing or extra.
 * source: as exact, except that only *.sbproj files differ.
 * Publish-generated paths (`.bin/`, `*_c`, `*.meta`) never block a verdict: the publisher regenerates them.
 * Tracked generated files that differ or are absent from the manifest are listed in generatedMismatch;
 * untracked generated manifest files are listed in ignoredGenerated.
 */
export function compareToManifest(manifest, tree, digestsOf) {
  const missing = [];
  const extra = [];
  const differing = [];
  const generatedMismatch = [];
  const ignoredGenerated = [];
  const publisherMetadata = [];
  for (const p of tree.keys()) {
    if (!manifest.has(p)) (isGenerated(p) ? generatedMismatch : missing).push(p);
  }
  for (const [p, entry] of manifest) {
    if (tree.has(p)) {
      if (digestsOf(tree.get(p)).has(`${entry.size}:${entry.crc}`)) continue;
      (isGenerated(p) ? generatedMismatch : differing).push(p);
    } else if (isGenerated(p)) {
      ignoredGenerated.push(p);
    } else if (PUBLISHER_METADATA.has(p)) {
      publisherMetadata.push(p);
    } else {
      extra.push(p);
    }
  }
  let verdict = 'none';
  if (!missing.length && !extra.length) {
    if (!differing.length) verdict = 'exact';
    else if (differing.every((p) => p.endsWith('.sbproj'))) verdict = 'source';
  }
  return {
    verdict,
    missing: missing.sort(),
    extra: extra.sort(),
    differing: differing.sort(),
    generatedMismatch: generatedMismatch.sort(),
    ignoredGenerated: ignoredGenerated.sort(),
    publisherMetadata: publisherMetadata.sort(),
  };
}

/** Manifest paths the publisher should never ship (dot-paths, build dirs, csproj, launchSettings), excluding generated and publisher metadata. */
export function manifestExclusionViolations(manifest) {
  return [...manifest.keys()]
    .filter((p) => !isGenerated(p) && !PUBLISHER_METADATA.has(p) && !isShippable(p))
    .sort();
}

/**
 * Pick the source commit for a revision from its matching commits:
 * exact beats source; then commits committed at or before publish (fallback: all);
 * then main-reachable preferred over branch-only; then newest.
 */
export function chooseCommit(createdMs, matches) {
  const verdict = matches.some((m) => m.verdict === 'exact') ? 'exact' : 'source';
  const pool = matches.filter((m) => m.verdict === verdict);
  if (!pool.length) return null;
  const preceding = pool.filter((m) => m.dateMs <= createdMs);
  let scope = preceding.length ? preceding : pool;
  const onMain = scope.filter((m) => m.mainReachable);
  if (onMain.length) scope = onMain;
  return [...scope].sort(newestFirst)[0];
}

const newestFirst = (a, b) => b.dateMs - a.dateMs || (a.sha < b.sha ? -1 : a.sha > b.sha ? 1 : 0);

/** Closest candidate for an unmatched revision: fewest differing files, then main, then publish-date order. */
export function bestCandidate(createdMs, results) {
  const score = (r) => r.missing.length + r.extra.length + r.differing.length;
  const precedes = (r) => Number(r.dateMs <= createdMs);
  return [...results].sort(
    (a, b) =>
      score(a) - score(b) ||
      Number(b.mainReachable) - Number(a.mainReachable) ||
      precedes(b) - precedes(a) ||
      newestFirst(a, b),
  )[0];
}

/** Line-level summary of what differs between the published sbproj and the source one. */
export function lineDiff(publishedText, sourceText) {
  const count = (text) => {
    const m = new Map();
    for (const line of text.replace(/\r\n/g, '\n').split('\n')) m.set(line, (m.get(line) ?? 0) + 1);
    return m;
  };
  const only = (a, b) => {
    const out = [];
    for (const [line, n] of a) for (let i = (b.get(line) ?? 0); i < n; i++) out.push(line);
    return out;
  };
  const published = count(publishedText);
  const source = count(sourceText);
  return { publishedOnly: only(published, source), sourceOnly: only(source, published) };
}

/** Parse `git cat-file --batch` output, calling onBlob(sha, content) for each object. */
export function parseCatFileBatch(buf, onBlob) {
  let pos = 0;
  while (pos < buf.length) {
    const eol = buf.indexOf(0x0a, pos);
    const [sha, type, size] = buf.toString('latin1', pos, eol).split(' ');
    if (type !== 'blob') throw new Error(`git cat-file: ${sha} is ${type ?? 'missing'}, expected blob`);
    pos = eol + 1;
    const n = Number(size);
    onBlob(sha, buf.subarray(pos, pos + n));
    pos += n + 1;
  }
}

/** Tag planning. Existing tags are never moved; only lightweight tags at the same commit count as present. */
export function planTag(tag, commit, existing) {
  if (!existing) return 'create';
  if (existing.type !== 'commit' || existing.sha !== commit) {
    throw new Error(
      `${tag} already exists as ${existing.type} ${existing.sha}, not ${commit}; refusing to move or rewrite it`,
    );
  }
  return 'exists';
}

/** Parse a GitHub remote URL into owner/repo. */
export function parseGithubRepo(url) {
  const m = /github\.com[:/]([^/]+)\/([^/\s]+?)(?:\.git)?\/?$/.exec(url.trim());
  return m ? `${m[1]}/${m[2]}` : null;
}

// ------------------------------------------------------------ git ----------
function git(cwd, args, { buffer = false, input } = {}) {
  return execFileSync('git', args, {
    cwd,
    input,
    maxBuffer: 1 << 30,
    encoding: buffer ? null : 'utf8',
    stdio: ['pipe', 'pipe', 'pipe'],
  });
}

function loadCommits(root) {
  const refs = git(root, ['for-each-ref', '--format=%(refname)', REMOTE_PREFIX])
    .split('\n')
    .filter((ref) => ref && ref !== `${REMOTE_PREFIX}HEAD`);
  if (!refs.includes(MAIN_REF)) throw new Error(`${MAIN_REF} is missing; fetch origin (or run with --fetch)`);

  const log = git(root, ['log', '--format=%H%x09%cI%x09%s', ...refs]);
  const mainShas = new Set(git(root, ['rev-list', MAIN_REF]).split('\n').filter(Boolean));
  const branchesOf = new Map();
  for (const ref of refs) {
    if (ref === MAIN_REF) continue;
    const name = ref.slice(REMOTE_PREFIX.length);
    for (const sha of git(root, ['rev-list', ref]).split('\n').filter(Boolean)) {
      branchesOf.set(sha, [...(branchesOf.get(sha) ?? []), name]);
    }
  }
  return log
    .split('\n')
    .filter(Boolean)
    .map((line) => {
      const [sha, date, ...subject] = line.split('\t');
      return {
        sha,
        date,
        dateMs: Date.parse(date),
        subject: subject.join('\t'),
        mainReachable: mainShas.has(sha),
        branches: branchesOf.get(sha) ?? [],
      };
    });
}

/** LibraryVersion at a commit; the constant was introduced in 7767ed9, so older source (file absent or constant absent) is legacy "1.0". */
function libraryVersionAt(root, tree) {
  const sha = tree.get(LIBRARY_VERSION_FILE);
  if (!sha) return '1.0';
  const source = git(root, ['cat-file', 'blob', sha]);
  const m = /public\s+const\s+string\s+LibraryVersion\s*=\s*"([^"]+)"/.exec(source);
  return m ? m[1] : '1.0';
}

// ------------------------------------------------------------ network ------
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function fetchBuffer(url, attempts = 3) {
  let last;
  for (let i = 1; i <= attempts; i++) {
    try {
      const res = await fetch(url, { headers: { 'User-Agent': USER_AGENT } });
      if (res.ok) return Buffer.from(await res.arrayBuffer());
      last = new Error(`GET ${url}: HTTP ${res.status}`);
      if (res.status < 500 && res.status !== 429) break;
    } catch (error) {
      last = error;
    }
    await sleep(500 * i);
  }
  throw last;
}

async function writeAtomic(file, content) {
  await mkdir(path.dirname(file), { recursive: true });
  const tmp = `${file}.${process.pid}.tmp`;
  await writeFile(tmp, content);
  await rename(tmp, file);
}

async function readCached(file) {
  try {
    return await readFile(file);
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw error;
  }
}

async function loadVersions() {
  const [versionsRaw, currentRaw] = await Promise.all([fetchBuffer(VERSIONS_URL), fetchBuffer(CURRENT_URL)]);
  const list = JSON.parse(versionsRaw.toString('utf8'));
  if (!Array.isArray(list) || !list.length) throw new Error('versions API returned no revisions');
  const versions = list.map((v) => {
    if (!Number.isSafeInteger(v?.Id) || Number.isNaN(Date.parse(v.Created))) {
      throw new Error(`malformed version record: ${JSON.stringify(v).slice(0, 200)}`);
    }
    return {
      id: v.Id,
      created: v.Created,
      changes: v.Changes ?? '',
      engineVersion: v.EngineVersion,
      manifestUrl: manifestUrl(v.Id),
    };
  });
  const currentId = JSON.parse(currentRaw.toString('utf8'))?.Version?.Id;
  if (!Number.isSafeInteger(currentId)) throw new Error('current package API returned no Version.Id');
  const current = versions.find((v) => v.id === currentId);
  if (!current) throw new Error(`current revision ${currentId} is missing from the versions API`);
  return { versions, current };
}

function parseManifest(json, revision) {
  if (!json || !Array.isArray(json.Files)) throw new Error(`manifest ${revision} has no Files array`);
  if (json.Package !== undefined && json.Package !== PACKAGE) {
    throw new Error(`manifest ${revision} is for ${json.Package}, not ${PACKAGE}`);
  }
  const files = new Map();
  for (const f of json.Files) {
    if (
      typeof f?.path !== 'string' ||
      !Number.isInteger(f.size) ||
      typeof f.crc !== 'string' ||
      !/^[0-9a-f]{1,16}$/i.test(f.crc)
    ) {
      throw new Error(`manifest ${revision} has a malformed entry: ${JSON.stringify(f).slice(0, 200)}`);
    }
    if (files.has(f.path)) throw new Error(`manifest ${revision} lists ${f.path} twice`);
    files.set(f.path, { size: f.size, crc: f.crc.toLowerCase().padStart(16, '0'), url: f.url });
  }
  return files;
}

async function loadManifest(revision, cacheDir) {
  const file = path.join(cacheDir, `manifest-${revision}.json`);
  const cached = await readCached(file);
  if (cached) return parseManifest(JSON.parse(cached.toString('utf8')), revision);
  const text = (await fetchBuffer(manifestUrl(revision))).toString('utf8');
  const manifest = parseManifest(JSON.parse(text), revision);
  await writeAtomic(file, text);
  return manifest;
}

async function cachedPublishedFile(entry, cacheDir) {
  const file = path.join(cacheDir, 'files', `${entry.size}-${entry.crc}`);
  const cached = await readCached(file);
  if (cached) return cached;
  const buf = await fetchBuffer(entry.url);
  if (digest(buf) !== `${entry.size}:${entry.crc}`) throw new Error(`${entry.url} does not match its manifest entry`);
  await writeAtomic(file, buf);
  return buf;
}

async function mapLimit(items, limit, fn) {
  const out = new Array(items.length);
  let next = 0;
  await Promise.all(
    Array.from({ length: Math.min(limit, items.length) }, async () => {
      while (next < items.length) {
        const i = next++;
        out[i] = await fn(items[i]);
      }
    }),
  );
  return out;
}

// ------------------------------------------------------------ report -------
async function sbprojDiffs(root, chosen, manifest, tree, cacheDir) {
  const diffs = [];
  for (const p of chosen.differing) {
    if (!p.endsWith('.sbproj')) continue;
    const entry = manifest.get(p);
    const published = await cachedPublishedFile(entry, cacheDir);
    const source = git(root, ['cat-file', 'blob', tree.get(p)], { buffer: true });
    diffs.push({
      path: p,
      published: { size: entry.size, crc: entry.crc },
      source: { size: source.length, crc: crc64Hex(source) },
      ...lineDiff(published.toString('utf8'), source.toString('utf8')),
    });
  }
  return diffs;
}

function summarizeCommit(c) {
  return { commit: c.sha, commitDate: c.date, subject: c.subject, mainReachable: c.mainReachable };
}

async function evaluateRevision(root, v, manifest, commits, trees, digests, cacheDir) {
  const createdMs = Date.parse(v.created);
  const results = commits.map((c) => ({
    ...c,
    ...compareToManifest(manifest, trees.get(c.sha), (sha) => digests.get(sha)),
  }));
  const base = {
    revision: v.id,
    created: v.created,
    changes: v.changes,
    engineVersion: v.engineVersion,
    manifestUrl: v.manifestUrl,
    manifestExclusionViolations: manifestExclusionViolations(manifest),
  };

  const matches = results.filter((r) => r.verdict !== 'none');
  const chosen = chooseCommit(createdMs, matches.map((r) => ({ ...r, dateMs: r.dateMs })));
  if (!chosen) {
    const best = bestCandidate(createdMs, results);
    return {
      ...base,
      verdict: 'none',
      commit: null,
      best: {
        ...summarizeCommit(best),
        score: best.missing.length + best.extra.length + best.differing.length,
        differing: best.differing,
        missing: best.missing,
        extra: best.extra,
        ...(best.mainReachable ? {} : { branches: best.branches }),
      },
    };
  }

  const pool = matches.filter((m) => m.verdict === chosen.verdict).sort((a, b) => a.dateMs - b.dateMs);
  const ref = (m) => ({ commit: m.sha, date: m.date });
  const tree = trees.get(chosen.sha);
  return {
    ...base,
    verdict: chosen.verdict,
    commit: chosen.sha,
    ...summarizeCommit(chosen),
    ...(chosen.mainReachable ? {} : { branches: chosen.branches }),
    libraryVersion: libraryVersionAt(root, tree),
    matchingCommits: {
      count: pool.length,
      first: ref(pool[0]),
      last: ref(pool[pool.length - 1]),
      commits: pool.map((m) => m.sha),
    },
    ignoredGenerated: chosen.ignoredGenerated.length,
    generatedMismatch: chosen.generatedMismatch,
    publisherMetadata: chosen.publisherMetadata,
    sbprojDiff: chosen.verdict === 'source' ? await sbprojDiffs(root, chosen, manifest, tree, cacheDir) : undefined,
    compareUrl: null,
  };
}

function linkCompares(entries, repo) {
  const matched = entries.filter((e) => e.commit).sort((a, b) => Date.parse(a.created) - Date.parse(b.created));
  for (let i = 1; i < matched.length; i++) {
    const [prev, cur] = [matched[i - 1], matched[i]];
    cur.compareUrl = repo && prev.commit !== cur.commit ? `https://github.com/${repo}/compare/${prev.commit}...${cur.commit}` : null;
  }
}

const short = (sha) => (sha ? sha.slice(0, 7) : '-');

function tableRow(e) {
  let notes = '';
  if (e.commit) {
    if (!e.mainReachable) notes = `branch-only: ${e.branches.join(', ')}`;
    if (e.verdict === 'source') {
      const lines = (e.sbprojDiff ?? []).map((d) => `${d.path}: ${d.publishedOnly.length} published-only, ${d.sourceOnly.length} source-only lines`);
      notes = [notes, ...lines].filter(Boolean).join('; ');
    }
  } else if (e.best) {
    const b = e.best;
    const examples = [...b.differing.map((p) => `~${p}`), ...b.missing.map((p) => `-${p}`), ...b.extra.map((p) => `+${p}`)];
    notes =
      `best ${short(b.commit)} (${b.mainReachable ? 'main' : 'branch'}): ${b.differing.length} differing, ` +
      `${b.missing.length} missing, ${b.extra.length} extra` +
      (examples.length ? ` e.g. ${examples.slice(0, 4).join(', ')}` : '');
  }
  if (e.manifestExclusionViolations.length) notes += `; ships excluded: ${e.manifestExclusionViolations.join(', ')}`;
  return [
    String(e.revision),
    e.created.slice(0, 16).replace('T', ' '),
    e.verdict,
    short(e.commit),
    e.commit ? (e.mainReachable ? 'yes' : 'no') : '-',
    e.matchingCommits ? String(e.matchingCommits.count) : '-',
    e.libraryVersion ?? '-',
    notes,
  ];
}

const TABLE_HEAD = ['REVISION', 'CREATED (UTC)', 'VERDICT', 'COMMIT', 'MAIN', 'MATCHES', 'LIB', 'NOTES'];

function printTable(entries) {
  const rows = [TABLE_HEAD, ...entries.map(tableRow)];
  const widths = TABLE_HEAD.map((_, i) => Math.max(...rows.map((r) => r[i].length)));
  for (const row of rows) {
    console.log(row.map((cell, i) => (i === row.length - 1 ? cell : cell.padEnd(widths[i]))).join('  ').trimEnd());
  }
}

function markdownSummary(report) {
  const { exact, source, none } = report.counts;
  const lines = [
    `## sbox.game revisions: ${report.revisions.length} checked`,
    '',
    `exact: ${exact} · source: ${source} · none: ${none} · current: r${report.current.id}`,
    '',
    `| ${TABLE_HEAD.join(' | ')} |`,
    `| ${TABLE_HEAD.map(() => '---').join(' | ')} |`,
  ];
  for (const e of report.revisions) lines.push(`| ${tableRow(e).map((c) => c.replace(/\|/g, '\\|')).join(' | ')} |`);
  return `${lines.join('\n')}\n`;
}

// ------------------------------------------------------------ GitHub -------
async function run_(cmd, args, input) {
  return new Promise((resolve, reject) => {
    const child = spawn(cmd, args, { stdio: ['pipe', 'pipe', 'pipe'] });
    const out = [];
    const err = [];
    child.stdout.on('data', (d) => out.push(d));
    child.stderr.on('data', (d) => err.push(d));
    child.on('error', reject);
    child.on('close', (code) => {
      if (code === 0) resolve(Buffer.concat(out).toString('utf8'));
      else reject(new Error(Buffer.concat(err).toString('utf8').trim() || `${cmd} exited ${code}`));
    });
    child.stdin.end(input ?? '');
  });
}

/** GitHub REST call; returns null for 404. Uses GITHUB_TOKEN/GH_TOKEN, else the gh CLI. */
async function github(repo, method, endpoint, body) {
  const token = process.env.GITHUB_TOKEN || process.env.GH_TOKEN;
  if (token) {
    const res = await fetch(`https://api.github.com/repos/${repo}/${endpoint}`, {
      method,
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
        'User-Agent': USER_AGENT,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    if (res.status === 404) return null;
    if (!res.ok) throw new Error(`${method} ${endpoint}: HTTP ${res.status}: ${text.slice(0, 300)}`);
    return text ? JSON.parse(text) : null;
  }
  try {
    const out = await run_(
      'gh',
      ['api', '--method', method, `repos/${repo}/${endpoint}`, ...(body === undefined ? [] : ['--input', '-'])],
      body === undefined ? '' : JSON.stringify(body),
    );
    return out.trim() ? JSON.parse(out) : null;
  } catch (error) {
    if (/HTTP 404/.test(error.message)) return null;
    throw new Error(`${method} ${endpoint}: ${error.message}`);
  }
}

function releaseTitle(libraryVersion, revision) {
  return `${libraryVersion} · sbox.game r${revision}`;
}

export function releaseBody(entry, { repo, currentId }) {
  const isCurrent = entry.revision === currentId;
  const lines = [
    `Published on [sbox.game](${STORE_URL}) as \`${PACKAGE}\` revision **${entry.revision}**${isCurrent ? ' (current)' : ''}.`,
    '',
    `- **Published:** ${entry.created}`,
    `- **Changes:** ${entry.changes || '(none recorded)'}`,
    `- **Engine version:** ${entry.engineVersion}`,
    `- **Source commit:** [${short(entry.commit)}](https://github.com/${repo}/commit/${entry.commit}) ${entry.subject}`,
  ];
  if (entry.compareUrl) lines.push(`- **Compare with previous matched revision:** ${entry.compareUrl}`);
  lines.push(`- **Manifest:** ${entry.manifestUrl}`);
  lines.push(`- **sbox.game:** ${STORE_URL}`);
  lines.push(
    entry.verdict === 'exact'
      ? '- **Verdict:** `exact`: every shipped file matches the manifest by size and CRC64, in both directions.'
      : '- **Verdict:** `source`: every shipped file matches by size and CRC64 except `*.sbproj`, which the publisher rewrites on upload.',
  );
  if (!entry.mainReachable) {
    lines.push(
      '',
      `> **Not on \`main\`.** This commit exists only on ${entry.branches.map((b) => `\`${b}\``).join(', ')}. ` +
        'Its shipped files match this revision, but it is not in main history.',
    );
  }
  for (const d of entry.sbprojDiff ?? []) {
    lines.push(
      '',
      `\`${d.path}\` differs (published ${d.published.size} bytes / ${d.published.crc}; source ${d.source.size} bytes / ${d.source.crc}):`,
      '',
      '```diff',
      ...d.publishedOnly.map((l) => `- ${l}`),
      ...d.sourceOnly.map((l) => `+ ${l}`),
      '```',
    );
  }
  const record = {
    revision: entry.revision,
    created: entry.created,
    changes: entry.changes,
    engineVersion: entry.engineVersion,
    verdict: entry.verdict,
    commit: entry.commit,
    mainReachable: entry.mainReachable,
    ...(entry.branches ? { branches: entry.branches } : {}),
    libraryVersion: entry.libraryVersion,
    manifestUrl: entry.manifestUrl,
    matchingCommits: entry.matchingCommits.count,
  };
  lines.push('', '```json', JSON.stringify(record, null, 2), '```', '');
  return lines.join('\n');
}

function issueTitle(revision) {
  return `sbox.game revision ${revision} has no matching GitHub commit`;
}

function issueBody(entry, report) {
  const b = entry.best;
  const list = (items, prefix) =>
    items.length ? items.slice(0, 50).map((p) => `- ${prefix}\`${p}\``).join('\n') + (items.length > 50 ? `\n- ... ${items.length - 50} more` : '') : '- none';
  return [
    `Revision **${entry.revision}** (created ${entry.created}, engine ${entry.engineVersion}) of \`${PACKAGE}\` on [sbox.game](${STORE_URL}) has no commit in any \`origin/*\` branch whose shipped files match its manifest by size and CRC64.`,
    '',
    `Manifest: ${entry.manifestUrl}`,
    '',
    b
      ? `Best candidate: \`${b.commit}\` (${b.mainReachable ? 'on main' : `branch-only: ${b.branches.join(', ')}`}), ${b.subject}, score ${b.score}.`
      : 'No candidate commits were found.',
    '',
    '**Differing files** (same path, different content):',
    b ? list(b.differing, '') : '- none',
    '',
    '**Missing from manifest** (tracked and shippable, not shipped by sbox.game):',
    b ? list(b.missing, '') : '- none',
    '',
    '**Extra in manifest** (shipped by sbox.game, not tracked):',
    b ? list(b.extra, '') : '- none',
    '',
    `Reproduce: \`node .github/scripts/sbox-revisions.mjs --revision ${entry.revision}\` (report generated ${report.generatedAt}).`,
    '',
    'Created by the sbox-revisions workflow; it will not close this issue automatically.',
  ].join('\n');
}

async function syncCurrentIssue(report, repo) {
  const entry = report.revisions.find((e) => e.revision === report.current.id);
  if (!entry || entry.commit) return null;
  const title = issueTitle(entry.revision);
  const body = issueBody(entry, report);
  let found = null;
  for (let page = 1; !found; page++) {
    const items = (await github(repo, 'GET', `issues?state=open&per_page=100&page=${page}`)) ?? [];
    found = items.find((i) => !i.pull_request && i.title === title) ?? null;
    if (items.length < 100) break;
  }
  if (found) {
    await github(repo, 'PATCH', `issues/${found.number}`, { body });
    return `updated issue #${found.number}`;
  }
  const created = await github(repo, 'POST', 'issues', { title, body });
  return `opened issue #${created.number}`;
}

async function applyReport(report, repo) {
  const actions = [];
  const errors = [];
  for (const entry of report.revisions) {
    if (!entry.commit) continue;
    const tag = `r${entry.revision}`;
    try {
      const ref = await github(repo, 'GET', `git/ref/tags/${tag}`);
      const existing = ref ? { sha: ref.object.sha, type: ref.object.type } : null;
      if (planTag(tag, entry.commit, existing) === 'create') {
        await github(repo, 'POST', 'git/refs', { ref: `refs/tags/${tag}`, sha: entry.commit });
        actions.push(`created tag ${tag} -> ${short(entry.commit)}`);
      }
      if (await github(repo, 'GET', `releases/tags/${tag}`)) {
        actions.push(`release ${tag} exists`);
        continue;
      }
      await github(repo, 'POST', 'releases', {
        tag_name: tag,
        target_commitish: entry.commit,
        name: releaseTitle(entry.libraryVersion, entry.revision),
        body: releaseBody(entry, { repo, currentId: report.current.id }),
        draft: false,
        prerelease: false,
        make_latest: entry.revision === report.current.id ? 'true' : 'false',
      });
      actions.push(`created release ${tag}`);
    } catch (error) {
      errors.push(`${tag}: ${error.message}`);
    }
  }
  try {
    const issue = await syncCurrentIssue(report, repo);
    if (issue) actions.push(issue);
  } catch (error) {
    errors.push(`issue: ${error.message}`);
  }
  return { actions, errors };
}

// -------------------------------------------------------------- CLI --------
function parseArgs(argv) {
  const opts = {
    apply: false,
    fetch: false,
    help: false,
    json: null,
    revision: null,
    cacheDir: path.join(os.tmpdir(), 'sbox-revisions-cache'),
  };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    const value = () => {
      const v = argv[++i];
      if (v === undefined || v.startsWith('--')) throw new Error(`${arg} needs a value`);
      return v;
    };
    switch (arg) {
      case '--apply':
        opts.apply = true;
        break;
      case '--fetch':
        opts.fetch = true;
        break;
      case '--help':
      case '-h':
        opts.help = true;
        break;
      case '--json':
        opts.json = value();
        break;
      case '--cache-dir':
        opts.cacheDir = value();
        break;
      case '--revision': {
        const v = value();
        if (!/^\d+$/.test(v)) throw new Error(`--revision must be a numeric revision id, got ${v}`);
        opts.revision = Number(v);
        break;
      }
      default:
        throw new Error(`unknown argument ${arg}\n${USAGE}`);
    }
  }
  return opts;
}

export async function run(argv) {
  const opts = parseArgs(argv);
  if (opts.help) {
    console.log(USAGE);
    return;
  }
  const root = git(process.cwd(), ['rev-parse', '--show-toplevel']).trim();
  if (git(root, ['rev-parse', '--is-shallow-repository']).trim() === 'true') {
    throw new Error('shallow clone: fetch full history (actions/checkout with fetch-depth: 0)');
  }
  if (opts.fetch) execFileSync('git', ['fetch', '--prune', 'origin'], { cwd: root, stdio: 'inherit' });

  let repo = process.env.GITHUB_REPOSITORY || null;
  if (!repo) {
    try {
      repo = parseGithubRepo(git(root, ['remote', 'get-url', 'origin']));
    } catch {
      repo = null;
    }
  }
  if (opts.apply && !repo) throw new Error('cannot determine the GitHub repository (set GITHUB_REPOSITORY or add an origin remote)');

  const { versions, current } = await loadVersions();
  const targets = opts.revision === null ? versions : versions.filter((v) => v.id === opts.revision);
  if (!targets.length) throw new Error(`revision ${opts.revision} is not a published revision of ${PACKAGE}`);

  const commits = loadCommits(root);
  const trees = new Map();
  const blobs = new Set();
  for (const c of commits) {
    const tree = parseTree(git(root, ['ls-tree', '-r', '-z', '--full-tree', c.sha]));
    trees.set(c.sha, tree);
    for (const sha of tree.values()) blobs.add(sha);
  }
  const digests = new Map();
  if (blobs.size) {
    parseCatFileBatch(
      git(root, ['cat-file', '--batch'], { buffer: true, input: `${[...blobs].join('\n')}\n` }),
      (sha, content) => digests.set(sha, blobDigests(content)),
    );
  }

  const manifests = await mapLimit(targets, 6, (v) => loadManifest(v.id, opts.cacheDir));
  const entries = [];
  for (let i = 0; i < targets.length; i++) {
    entries.push(await evaluateRevision(root, targets[i], manifests[i], commits, trees, digests, opts.cacheDir));
  }
  linkCompares(entries, repo);
  entries.sort((a, b) => b.revision - a.revision);

  const count = (verdict) => entries.filter((e) => e.verdict === verdict).length;
  const report = {
    generatedAt: new Date().toISOString(),
    package: PACKAGE,
    repo,
    current: { id: current.id, created: current.created },
    counts: { exact: count('exact'), source: count('source'), none: count('none') },
    revisions: entries,
  };

  printTable(entries);
  const currentEntry = entries.find((e) => e.revision === current.id);
  const currentNote = !currentEntry
    ? 'not in this run'
    : currentEntry.commit
      ? 'matched'
      : 'unmatched';
  console.log(
    `\nexact: ${report.counts.exact}  source: ${report.counts.source}  none: ${report.counts.none}  ` +
      `(current r${current.id}: ${currentNote})`,
  );

  try {
    if (opts.apply) {
      report.apply = await applyReport(report, repo);
      for (const a of report.apply.actions) console.log(`apply: ${a}`);
      for (const e of report.apply.errors) console.error(`apply error: ${e}`);
    }
  } finally {
    if (opts.json) await writeFile(opts.json, `${JSON.stringify(report, null, 2)}\n`);
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, markdownSummary(report));
  }
  if (report.apply?.errors.length) throw new Error(`${report.apply.errors.length} apply error(s); see report`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  run(process.argv.slice(2)).catch((error) => {
    console.error(`sbox-revisions: ${error.message}`);
    process.exitCode = 1;
  });
}
