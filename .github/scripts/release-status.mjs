#!/usr/bin/env node
// Builds release-status.json (schema 1) for the self-hosted server's owner
// dashboard: the published library revision against main, the PRs merged since
// that revision, the latest s&box engine tag, and the server's release state.
//
// Usage: node .github/scripts/release-status.mjs [--out <file>]
//
// Environment: GITHUB_TOKEN or GH_TOKEN (read-only API access is enough),
// GITHUB_REPOSITORY (default sbox-cool/sbox-network-storage). Downloading the
// engine summary uses the gh CLI with the same token.
//
// Core GitHub API failures throw, so the workflow fails and the published file is
// not replaced. Optional lookups (engine string from the CI artifact) become null.

import { execFile } from 'node:child_process';
import { readdir, readFile, writeFile, mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const execFileAsync = promisify(execFile);

export const SCHEMA = 1;
export const DEFAULT_LIBRARY_REPO = 'sbox-cool/sbox-network-storage';
export const SERVER_REPO = 'sbox-cool/sbox-network-storage-server';
export const ENGINE_REPO = 'Facepunch/sbox-public';
export const STORE_URL = 'https://sbox.game/sboxcool/network-storage';
export const LIBRARY_VERSION_FILE = 'Code/Core/NetworkStorageClient.cs';
export const CI_WORKFLOW_FILE = 'compat-gate.yml';
export const CI_ARTIFACT = 'engine-main';
export const PENDING_CAP = 50;
export const INSTALL_MAIN = Object.freeze({
  zipUrl: 'https://github.com/sbox-cool/sbox-network-storage/archive/refs/heads/main.zip',
  docsUrl: 'https://github.com/sbox-cool/sbox-network-storage#using-unreleased-features-main',
});

const API = 'https://api.github.com';
const USER_AGENT = 'sbox-cool-release-status';
const DAY_MS = 24 * 60 * 60 * 1000;
const LEGACY_LIBRARY_VERSION = '1.0';
const PULLS_CONCURRENCY = 8;

// ---------------------------------------------------------------------------
// Pure helpers
// ---------------------------------------------------------------------------

/** 'r423774' -> 423774; anything else -> null. */
export function revisionOfTag(tag) {
  const match = /^r(\d+)$/.exec(tag ?? '');
  return match ? Number(match[1]) : null;
}

/** Newest non-draft `r<revision>` release: { tag, revision, release } or null. */
export function newestRevisionRelease(releases) {
  let best = null;
  for (const release of releases) {
    if (release.draft) continue;
    const revision = revisionOfTag(release.tag_name);
    if (revision !== null && (best === null || revision > best.revision)) {
      best = { tag: release.tag_name, revision, release };
    }
  }
  return best;
}

/**
 * sbox.game publish date. sbox-revisions.mjs writes it into the release body as
 * "- **Published:** <date>"; the GitHub release date is the sync time and only
 * the fallback.
 */
export function publishedAtOf(release) {
  const match = /^- \*\*Published:\*\*\s*(\S+)/m.exec(release.body ?? '');
  const parsed = match ? Date.parse(match[1]) : Number.NaN;
  if (!Number.isNaN(parsed)) return new Date(parsed).toISOString();
  return release.published_at ?? null;
}

/** '26.10.13' -> [26, 10, 13]; anything that is not dotted numbers -> null. */
export function parseVersion(text) {
  const match = /^\d+(?:\.\d+)+$/.exec(String(text ?? '').trim());
  return match ? match[0].split('.').map(Number) : null;
}

export function compareVersions(a, b) {
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const diff = (a[i] ?? 0) - (b[i] ?? 0);
    if (diff !== 0) return diff;
  }
  return 0;
}

/** Highest dotted-version tag: { name, sha } or null. */
export function newestEngineTag(tags) {
  let best = null;
  let bestVersion = null;
  for (const tag of tags) {
    const version = parseVersion(tag.name);
    if (version && (bestVersion === null || compareVersions(version, bestVersion) > 0)) {
      best = tag;
      bestVersion = version;
    }
  }
  return best;
}

/**
 * LibraryVersion from NetworkStorageClient.cs. Source without the constant is the
 * legacy 1.0 line (same rule as sbox-revisions.mjs); a missing file passes ''.
 */
export function libraryVersionFromSource(text) {
  const match = /public\s+const\s+string\s+LibraryVersion\s*=\s*"([^"]+)"/.exec(text ?? '');
  return match ? match[1] : LEGACY_LIBRARY_VERSION;
}

/** Keeps PRs merged into `main`, dedupes by number, sorts newest first, caps the list. */
export function selectPending(prs, cap = PENDING_CAP) {
  const byNumber = new Map();
  for (const pr of prs) {
    if (!pr || !pr.merged_at || pr.base?.ref !== 'main') continue;
    byNumber.set(pr.number, {
      number: pr.number,
      title: pr.title,
      mergedAt: pr.merged_at,
      url: pr.html_url,
    });
  }
  const all = [...byNumber.values()].sort(
    (a, b) => Date.parse(b.mergedAt) - Date.parse(a.mergedAt) || b.number - a.number,
  );
  return {
    pending: all.slice(0, cap),
    // Oldest over the full set, so the cap never hides how long the wait is.
    pendingSince: all.length > 0 ? all[all.length - 1].mergedAt : null,
  };
}

/** libraryBehind: engine tag is newer than the published library. */
export function behindStatus({ publishedAt, engineDate, generatedAt }) {
  const published = Date.parse(publishedAt);
  const engine = Date.parse(engineDate);
  const generated = Date.parse(generatedAt);
  if ([published, engine, generated].some(Number.isNaN)) {
    return { libraryBehind: null, daysBehind: null };
  }
  const libraryBehind = engine > published;
  const daysBehind = libraryBehind ? Math.max(0, Math.floor((generated - engine) / DAY_MS)) : 0;
  return { libraryBehind, daysBehind };
}

/** Newest run whose head is `sha`, or null. Runs are listed newest first. */
export function pickCiRun(runs, sha) {
  return runs.find((run) => run.head_sha === sha) ?? null;
}

export function ciConclusion(run) {
  if (!run) return 'unknown';
  if (run.status !== 'completed') return 'pending';
  if (run.conclusion === 'success') return 'success';
  if (['failure', 'timed_out', 'startup_failure'].includes(run.conclusion)) return 'failure';
  return 'unknown';
}

/** Engine line from the compat-gate summary: "... (sbox-public <sha>) ..." -> "sbox-public@<sha>". */
export function engineFromSummary(text) {
  const match = /\(sbox-public ([0-9a-f]{40})\)/.exec(text ?? '');
  return match ? `sbox-public@${match[1]}` : null;
}

// Copies only the named keys, with null for anything missing.
function pick(source, keys) {
  return Object.fromEntries(keys.map((key) => [key, source?.[key] ?? null]));
}

/**
 * Assembles schema 1. Every input may be null/partial; missing values become null
 * so the file always has the full shape.
 */
export function buildStatus({ generatedAt, library, engine, server }) {
  const lib = library ?? {};
  const published = lib.published ?? null;
  const main = lib.main ?? null;
  const latest = engine?.latest ?? null;
  const release = server?.latestRelease ?? null;
  const behind = behindStatus({
    publishedAt: published?.publishedAt,
    engineDate: latest?.date,
    generatedAt,
  });
  return {
    schema: SCHEMA,
    generatedAt,
    library: {
      published: { ...pick(published, ['revision', 'version', 'commit', 'publishedAt']), url: STORE_URL },
      main: {
        ...pick(main, ['version', 'commit', 'committedAt']),
        ci: {
          conclusion: main?.ci?.conclusion ?? 'unknown',
          runUrl: main?.ci?.runUrl ?? null,
          engine: main?.ci?.engine ?? null,
        },
      },
      pending: lib.pending ? lib.pending.map((pr) => pick(pr, ['number', 'title', 'mergedAt', 'url'])) : null,
      pendingSince: lib.pendingSince ?? null,
      installMain: { ...INSTALL_MAIN },
    },
    engine: {
      latest: pick(latest, ['version', 'date', 'url']),
      libraryBehind: behind.libraryBehind,
      daysBehind: behind.daysBehind,
    },
    server: {
      latestRelease: pick(release, ['version', 'publishedAt', 'url']),
      main: pick(server?.main, ['commit', 'committedAt']),
      pending: server?.pending ? server.pending.map((pr) => pick(pr, ['number', 'title', 'mergedAt', 'url'])) : null,
      pendingSince: server?.pendingSince ?? null,
    },
  };
}

// ---------------------------------------------------------------------------
// GitHub access
// ---------------------------------------------------------------------------

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** GET one endpoint. Returns null on 404; retries 429/5xx a couple of times. */
async function gh(endpoint, { raw = false } = {}) {
  const token = process.env.GITHUB_TOKEN || process.env.GH_TOKEN;
  const headers = {
    Accept: raw ? 'application/vnd.github.raw' : 'application/vnd.github+json',
    'X-GitHub-Api-Version': '2022-11-28',
    'User-Agent': USER_AGENT,
  };
  if (token) headers.Authorization = `Bearer ${token}`;
  for (let attempt = 1; ; attempt++) {
    const res = await fetch(`${API}/${endpoint}`, { headers });
    if (res.status === 404) return null;
    if (res.ok) {
      const text = await res.text();
      return raw ? text : JSON.parse(text);
    }
    if ((res.status === 429 || res.status >= 500) && attempt < 3) {
      await sleep(2000 * attempt);
      continue;
    }
    const body = await res.text();
    throw new Error(`GET ${endpoint}: HTTP ${res.status} ${body.slice(0, 200)}`);
  }
}

/** All pages of a list endpoint (per_page=100). */
async function ghAll(endpoint) {
  const items = [];
  const sep = endpoint.includes('?') ? '&' : '?';
  for (let page = 1; ; page++) {
    const batch = (await gh(`${endpoint}${sep}per_page=100&page=${page}`)) ?? [];
    items.push(...batch);
    if (batch.length < 100) return items;
  }
}

async function mapLimit(items, limit, fn) {
  const out = new Array(items.length);
  let next = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const index = next++;
      out[index] = await fn(items[index]);
    }
  });
  await Promise.all(workers);
  return out;
}

/** Commit SHAs ahead of `base` on `head`, with a warning if the compare list was truncated. */
async function commitsAhead(repo, base, head) {
  const compare = await gh(`repos/${repo}/compare/${base}...${head}`);
  if (!compare) throw new Error(`compare ${repo} ${base}...${head}: not found`);
  if (compare.total_commits > compare.commits.length) {
    console.error(`release-status: ${repo} compare lists ${compare.commits.length} of ${compare.total_commits} commits`);
  }
  return compare.commits.map((commit) => commit.sha);
}

/** Merged PRs (any base) associated with the given commits. */
async function pullsOfCommits(repo, shas) {
  const lists = await mapLimit(shas, PULLS_CONCURRENCY, (sha) =>
    gh(`repos/${repo}/commits/${sha}/pulls?per_page=100`),
  );
  return lists.flatMap((list) => list ?? []);
}

async function commitOfTag(repo, tag) {
  const ref = await gh(`repos/${repo}/git/ref/tags/${tag}`);
  if (!ref) throw new Error(`tag ${tag} not found in ${repo}`);
  if (ref.object.type === 'commit') return ref.object.sha;
  const annotated = await gh(`repos/${repo}/git/tags/${ref.object.sha}`);
  return annotated.object.sha;
}

async function commitInfo(repo, sha) {
  const commit = await gh(`repos/${repo}/commits/${sha}`);
  if (!commit) throw new Error(`commit ${sha} not found in ${repo}`);
  return { sha: commit.sha, committedAt: commit.commit.committer.date };
}

/** LibraryVersion at a commit. A missing file is the legacy version. */
async function libraryVersionAt(repo, sha) {
  const text = await gh(`repos/${repo}/contents/${LIBRARY_VERSION_FILE}?ref=${sha}`, { raw: true });
  return libraryVersionFromSource(text ?? '');
}

async function findFile(dir, name) {
  const entries = await readdir(dir, { recursive: true });
  const hit = entries.find((entry) => path.basename(entry) === name);
  return hit ? path.join(dir, hit) : null;
}

/** Engine string from the main-leg compat-gate summary artifact; null when unavailable. */
async function engineOfRun(repo, runId) {
  const dir = await mkdtemp(path.join(os.tmpdir(), 'release-status-'));
  try {
    await execFileAsync('gh', ['run', 'download', String(runId), '-R', repo, '-n', CI_ARTIFACT, '-D', dir]);
    const summary = await findFile(dir, 'summary.md');
    return summary ? engineFromSummary(await readFile(summary, 'utf8')) : null;
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
}

async function mainCi(repo, sha) {
  const data = await gh(`repos/${repo}/actions/workflows/${CI_WORKFLOW_FILE}/runs?branch=main&per_page=50`);
  const run = pickCiRun(data?.workflow_runs ?? [], sha);
  if (!run) return { conclusion: 'unknown', runUrl: null, engine: null };
  let engine = null;
  if (run.status === 'completed') {
    try {
      engine = await engineOfRun(repo, run.id);
    } catch (error) {
      console.error(`release-status: engine string unavailable for run ${run.id}: ${error.message}`);
    }
  }
  return { conclusion: ciConclusion(run), runUrl: run.html_url, engine };
}

async function collectLibrary(repo) {
  const releases = await ghAll(`repos/${repo}/releases`);
  const newest = newestRevisionRelease(releases);
  const mainHead = await commitInfo(repo, 'main');
  const mainVersion = await libraryVersionAt(repo, mainHead.sha);

  let published = null;
  let pending = null;
  let pendingSince = null;
  if (newest) {
    const commit = await commitOfTag(repo, newest.tag);
    published = {
      revision: newest.revision,
      version: await libraryVersionAt(repo, commit),
      commit,
      publishedAt: publishedAtOf(newest.release),
    };
    const shas = await commitsAhead(repo, commit, mainHead.sha);
    ({ pending, pendingSince } = selectPending(await pullsOfCommits(repo, shas)));
  }

  return {
    published,
    main: {
      version: mainVersion,
      commit: mainHead.sha,
      committedAt: mainHead.committedAt,
      ci: await mainCi(repo, mainHead.sha),
    },
    pending,
    pendingSince,
  };
}

async function collectEngine() {
  const tags = await ghAll(`repos/${ENGINE_REPO}/tags`);
  const newest = newestEngineTag(tags.map((tag) => ({ name: tag.name, sha: tag.commit.sha })));
  if (!newest) return { latest: null };
  const commit = await commitInfo(ENGINE_REPO, newest.sha);
  return {
    latest: {
      version: newest.name,
      date: commit.committedAt,
      url: `https://github.com/${ENGINE_REPO}/tree/${newest.name}`,
    },
  };
}

async function collectServer() {
  const latest = await gh(`repos/${SERVER_REPO}/releases/latest`);
  const mainHead = await commitInfo(SERVER_REPO, 'main');
  const result = {
    latestRelease: null,
    main: { commit: mainHead.sha, committedAt: mainHead.committedAt },
    pending: null,
    pendingSince: null,
  };
  if (latest) {
    result.latestRelease = {
      version: latest.tag_name.replace(/^v/, ''),
      publishedAt: latest.published_at,
      url: latest.html_url,
    };
    const shas = await commitsAhead(SERVER_REPO, latest.tag_name, mainHead.sha);
    ({ pending: result.pending, pendingSince: result.pendingSince } = selectPending(
      await pullsOfCommits(SERVER_REPO, shas),
    ));
  }
  return result;
}

export async function collectStatus({ libraryRepo, generatedAt }) {
  const [library, engine, server] = await Promise.all([
    collectLibrary(libraryRepo),
    collectEngine(),
    collectServer(),
  ]);
  return buildStatus({ generatedAt, library, engine, server });
}

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------

export async function run(argv) {
  let out = null;
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--out') out = argv[++i];
    else throw new Error(`unknown argument: ${argv[i]}`);
  }
  const libraryRepo = process.env.GITHUB_REPOSITORY || DEFAULT_LIBRARY_REPO;
  const status = await collectStatus({ libraryRepo, generatedAt: new Date().toISOString() });
  const json = `${JSON.stringify(status, null, 2)}\n`;
  if (out) await writeFile(out, json);
  else process.stdout.write(json);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  run(process.argv.slice(2)).catch((error) => {
    console.error(`release-status: ${error.message}`);
    process.exitCode = 1;
  });
}
