#!/usr/bin/env node
// The formatting action's logic (docs/coverage_file_formatter_PRD.md, D12, D13, D16, D17; README.md beside this file).
//
//   node format.mjs plan   prints the toolchains the run needs ($GITHUB_OUTPUT: dotnet, go, terraform)
//   node format.mjs run    formats or lints, and writes the patch (and, outside the pipeline, the report)
//
// Environment:
//   MODE           format | lint | pipeline
//   EXCLUDE        globs, one per line, never touched (D11)
//   EXPECTED_SHA   the commit the run must be on (required in the pipeline)
//   OURS_SHA, THEIRS_SHA, MERGE_BASE   pipeline only (D17): the base branch's side, the pull request's side, the base
//   OUT_DIR        where merge-queue.patch and format-report.json go
//
// Nothing here pushes, commits or holds a token. The patch is relative to HEAD and touches only files that exist there.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// ── Pure logic (format.test.mjs) ──────────────────────────────────────────────────────────────────────────────────

/** Lines of a multi-line input, trimmed, without blanks and `#` comments. */
export function parseList(text) {
  return (text ?? '').split(/\r?\n/).map(l => l.trim()).filter(l => l && !l.startsWith('#'));
}

/** A glob as coverage.yml writes them: `**` any depth, `*` and `?` within a segment; a pattern without `/` matches the
 *  file name at any depth (as .gitignore does). */
export function globToRegExp(glob) {
  let g = glob.replace(/^\.\//, '');
  const anchored = g.includes('/');
  if (g.endsWith('/')) g += '**';
  let re = '';
  for (let i = 0; i < g.length; i++) {
    const c = g[i];
    if (c === '*' && g[i + 1] === '*') {
      i++;
      if (g[i + 1] === '/') { i++; re += '(?:.*/)?'; } else re += '.*';
    } else if (c === '*') re += '[^/]*';
    else if (c === '?') re += '[^/]';
    else re += c.replace(/[.+^${}()|[\]\\]/g, '\\$&');
  }
  return new RegExp(anchored ? `^${re}$` : `^(?:.*/)?${re}$`);
}

export function matchesAny(file, globs) {
  return globs.some(g => globToRegExp(g).test(file));
}

/** D9.2: the pipeline never touches workflows; D11: nobody touches `exclude`. */
export function selectFiles(files, { mode, exclude = [] }) {
  return files.filter(f => !(mode === 'pipeline' && (f === '.github' || f.startsWith('.github/'))) && !matchesAny(f, exclude));
}

/** "git version 2.43.0" (or "2.47.1.windows.1") → [2, 43, 0]. */
export function parseGitVersion(text) {
  const m = /(\d+)\.(\d+)(?:\.(\d+))?/.exec(text ?? '');
  return m ? [Number(m[1]), Number(m[2]), Number(m[3] ?? 0)] : null;
}

export function atLeast(version, major, minor) {
  return !!version && (version[0] > major || (version[0] === major && version[1] >= minor));
}

/** S10: the writer's own call. `--diff-algorithm` exists from git 2.44; before it merge-file always used myers, and the
 *  output was measured byte-identical on 325 real triples. `side` is ours | theirs | null (the conflicted document). */
export function mergeFileArgs(version, side, ours, base, theirs) {
  return ['merge-file', '-p', ...(side ? [`--${side}`] : ['--diff3']),
    ...(atLeast(version, 2, 44) ? ['--diff-algorithm=myers'] : []),
    '-L', 'ours', '-L', 'base', '-L', 'theirs', ours, base, theirs];
}

/** D9.3: files whose change means the formatter itself changed, so the pull request's run cannot prove anything. */
const FORMATTER_INPUTS = [
  /(^|\/)\.editorconfig$/, /(^|\/)\.globalconfig$/, /(^|\/)global\.json$/, /(^|\/)Directory\.Build\.(props|targets)$/,
  /(^|\/)\.prettierrc(\.[a-z]+)?$/, /(^|\/)prettier\.config\.[cm]?[jt]s$/, /(^|\/)\.prettierignore$/,
  /(^|\/)package\.json$/, /(^|\/)package-lock\.json$/, /(^|\/)pnpm-lock\.yaml$/, /(^|\/)yarn\.lock$/, /(^|\/)\.yarnrc\.yml$/,
  /(^|\/)\.?rustfmt\.toml$/, /(^|\/)rust-toolchain(\.toml)?$/,
  /(^|\/)pyproject\.toml$/, /(^|\/)\.?ruff\.toml$/, /(^|\/)uv\.lock$/, /(^|\/)requirements[^/]*\.txt$/, /(^|\/)\.pre-commit-config\.yaml$/,
  /(^|\/)go\.mod$/, /(^|\/)\.terraform-version$/,
];

export function formatterInputsChanged(files) {
  return files.filter(f => FORMATTER_INPUTS.some(re => re.test(f)));
}

const PRETTIER_EXT = ['.js', '.jsx', '.mjs', '.cjs', '.ts', '.tsx', '.mts', '.cts', '.json', '.jsonc', '.json5', '.css', '.scss',
  '.less', '.html', '.vue', '.md', '.mdx', '.yaml', '.yml', '.graphql', '.gql', '.hbs', '.handlebars'];
const ESLINT_EXT = ['.js', '.jsx', '.mjs', '.cjs', '.ts', '.tsx', '.mts', '.cts', '.vue'];
const PY_EXT = ['.py', '.pyi'];

/** D13. `modes` says where a tool runs; the pipeline only ever formats. */
export const TOOLS = [
  { id: 'dotnet-whitespace', ext: ['.cs', '.vb'], modes: ['format', 'pipeline'] },
  { id: 'dotnet-style', ext: ['.cs', '.vb'], modes: ['format'] },
  { id: 'dotnet-analyzers', ext: ['.cs', '.vb'], modes: ['lint'] },
  { id: 'prettier', ext: PRETTIER_EXT, modes: ['format', 'pipeline'] },
  { id: 'eslint', ext: ESLINT_EXT, modes: ['lint'] },
  { id: 'gofmt', ext: ['.go'], modes: ['format', 'pipeline'] },
  { id: 'rustfmt', ext: ['.rs'], modes: ['format', 'pipeline'] },
  { id: 'clippy', ext: ['.rs'], modes: ['lint'] },
  { id: 'ruff-format', ext: PY_EXT, modes: ['format', 'pipeline'] },
  { id: 'ruff-check', ext: PY_EXT, modes: ['lint'] },
  { id: 'black', ext: PY_EXT, modes: ['format', 'pipeline'] },
  { id: 'terraform-fmt', ext: ['.tf', '.tfvars'], modes: ['format', 'pipeline'] },
];

const extOf = f => path.posix.extname(f).toLowerCase();

function packageJsonDeps(text) {
  try {
    const json = JSON.parse(text);
    return { ...json.dependencies, ...json.devDependencies };
  } catch {
    return {};
  }
}

/** The repository's pins and configs (S2, S8), from the files at its root. `repo` is { exists(path), read(path) }. */
export function detectPins(repo, files) {
  const pins = { notes: [] };
  const read = p => (repo.exists(p) ? repo.read(p) : null);

  // JS: only what the root package.json pins (S2); installed with scripts off through its own package manager.
  const pkg = read('package.json');
  if (pkg) {
    const deps = packageJsonDeps(pkg);
    if (deps.prettier) pins.prettier = deps.prettier;
    if (deps.eslint) pins.eslint = deps.eslint;
    let manager = null;
    try { manager = JSON.parse(pkg).packageManager ?? null; } catch { /* the deps above already failed alike */ }
    pins.node = {
      lockfile: ['package-lock.json', 'pnpm-lock.yaml', 'yarn.lock', 'bun.lockb', 'bun.lock'].find(f => repo.exists(f)) ?? null,
      packageManager: manager,
    };
  }

  // .NET: a single root solution for style and analyzers; whitespace needs none (S1).
  const solutions = files.filter(f => !f.includes('/') && /\.slnx?$/i.test(f));
  pins.dotnet = {
    globalJson: repo.exists('global.json'),
    solution: solutions.length === 1 ? solutions[0] : null,
    solutions: solutions.length,
  };

  if (repo.exists('go.mod')) {
    const mod = read('go.mod');
    pins.go = /^toolchain\s+go(\S+)/m.exec(mod)?.[1] ?? /^go\s+(\S+)/m.exec(mod)?.[1] ?? 'go.mod';
  }

  if (repo.exists('Cargo.toml')) {
    const toolchain = read('rust-toolchain.toml') ?? read('rust-toolchain');
    pins.rust = toolchain ? (/channel\s*=\s*"([^"]+)"/.exec(toolchain)?.[1] ?? toolchain.trim().split(/\s+/)[0]) : null;
  }

  // Python: ruff when configured, else black (D13), at the version a lockfile, a requirements file or pre-commit pins.
  const pyproject = read('pyproject.toml') ?? '';
  const ruff = /^\[tool\.ruff/m.test(pyproject) || repo.exists('ruff.toml') || repo.exists('.ruff.toml');
  const black = /^\[tool\.black/m.test(pyproject);
  if (ruff || black) {
    const name = ruff ? 'ruff' : 'black';
    pins.python = { tool: name, version: pythonPin(name, read, files) };
  }

  if (files.some(f => ['.tf', '.tfvars'].includes(extOf(f)))) {
    pins.terraform = (read('.terraform-version') ?? '').trim().replace(/^v/, '') || 'latest';
  }
  return pins;
}

function pythonPin(name, read, files) {
  const uv = read('uv.lock');
  if (uv) {
    const m = new RegExp(`\\[\\[package\\]\\]\\s*\\nname = "${name}"\\s*\\nversion = "([^"]+)"`).exec(uv);
    if (m) return m[1];
  }
  const poetry = read('poetry.lock');
  if (poetry) {
    const m = new RegExp(`\\[\\[package\\]\\]\\s*\\nname = "${name}"\\s*\\nversion = "([^"]+)"`).exec(poetry);
    if (m) return m[1];
  }
  for (const f of files.filter(f => /(^|\/)requirements[^/]*\.txt$/.test(f)).sort()) {
    const m = new RegExp(`^${name}==([\\w.]+)`, 'mi').exec(read(f) ?? '');
    if (m) return m[1];
  }
  const preCommit = read('.pre-commit-config.yaml');
  if (preCommit) {
    const repoName = name === 'ruff' ? 'ruff-pre-commit' : '(?:black|black-pre-commit-mirror)';
    const m = new RegExp(`repo:\\s*\\S*/${repoName}\\s*\\n\\s*rev:\\s*["']?v?([\\w.]+)`).exec(preCommit);
    if (m) return m[1];
  }
  return null;
}

/** Which tools this run uses, and why the others are skipped. */
export function planTools(mode, pins, files) {
  const tools = [];
  const notes = [];
  for (const tool of TOOLS.filter(t => t.modes.includes(mode))) {
    const mine = files.filter(f => tool.ext.includes(extOf(f)));
    if (mine.length === 0) continue;
    const skip = reasonToSkip(tool.id, pins);
    if (skip) notes.push(`${tool.id}: skipped, ${skip}`);
    else tools.push({ id: tool.id, files: mine });
  }
  if (mode === 'lint' && files.some(f => ['.go', '.tf', '.tfvars'].includes(extOf(f))))
    notes.push('Go and Terraform have no lint step here: run FormatRepository for them.');
  return { tools, notes };
}

function reasonToSkip(id, pins) {
  switch (id) {
    case 'dotnet-style':
    case 'dotnet-analyzers':
      return pins.dotnet.solution ? null
        : pins.dotnet.solutions > 1 ? 'there is more than one solution at the root' : 'there is no solution at the root';
    case 'prettier': return pins.prettier ? null : 'the root package.json does not depend on prettier';
    case 'eslint': return pins.eslint ? null : 'the root package.json does not depend on eslint';
    case 'gofmt': return pins.go ? null : 'there is no go.mod at the root';
    case 'rustfmt':
    case 'clippy': return 'rust' in pins ? null : 'there is no Cargo.toml at the root';
    case 'ruff-format':
    case 'ruff-check': return pins.python?.tool === 'ruff' ? null : 'ruff is not configured';
    case 'black': return pins.python?.tool === 'black' ? null : 'black is not configured, or ruff is';
    default: return null;
  }
}

/** S2: ESLint 9 reads the config of the directory it runs in, ESLint 10 the file's own: grouping each file under its
 *  nearest eslint.config.* and running there is right for both. Files with none fall to the root (''). */
export function groupByNearestConfig(files, exists, names) {
  const groups = new Map();
  for (const file of files) {
    let dir = path.posix.dirname(file);
    let found = '';
    for (;;) {
      const here = dir === '.' ? '' : dir;
      if (names.some(n => exists(here ? `${here}/${n}` : n))) { found = here; break; }
      if (!here) break;
      dir = path.posix.dirname(dir);
    }
    if (!groups.has(found)) groups.set(found, []);
    groups.get(found).push(file);
  }
  return groups;
}

export const ESLINT_CONFIGS = ['eslint.config.js', 'eslint.config.mjs', 'eslint.config.cjs', 'eslint.config.ts', 'eslint.config.mts', 'eslint.config.cts'];

/** The rust edition rustfmt must be told for a file, from its nearest Cargo.toml (a workspace member inherits the root's). */
export function rustEdition(file, read, exists) {
  let dir = path.posix.dirname(file);
  for (;;) {
    const here = dir === '.' ? '' : dir;
    const toml = here ? `${here}/Cargo.toml` : 'Cargo.toml';
    if (exists(toml)) {
      const text = read(toml);
      const own = /^\s*edition\s*=\s*"(\d{4})"/m.exec(text);
      if (own) return own[1];
      if (/^\s*edition\.workspace\s*=\s*true/m.test(text) || /^\s*edition\s*=\s*\{\s*workspace\s*=\s*true/m.test(text)) {
        const root = exists('Cargo.toml') ? read('Cargo.toml') : '';
        return /\[workspace\.package\][^[]*?edition\s*=\s*"(\d{4})"/s.exec(root)?.[1] ?? '2021';
      }
    }
    if (!here) return '2015';
    dir = path.posix.dirname(dir);
  }
}

/**
 * D17: the proof that the pull request's side of every conflict differs from the base branch's only in what the
 * formatter rewrites. `format(variant)` formats { path: text } and answers the formatted { path: text }. Both whole-file
 * resolutions (every conflict ours, every conflict theirs) must format to the same bytes. D16: the base branch's own
 * copy must already be formatted; when it is not, the proven resolution is kept as the writer made it, unformatted.
 */
export async function prove(candidates, format) {
  const ours = await format(Object.fromEntries(candidates.map(c => [c.path, c.oursResolution])));
  const theirs = await format(Object.fromEntries(candidates.map(c => [c.path, c.theirsResolution])));
  const unproven = candidates.filter(c => ours[c.path] !== theirs[c.path]).map(c => c.path);
  if (unproven.length > 0) return { proven: false, unproven };
  const target = await format(Object.fromEntries(candidates.map(c => [c.path, c.target])));
  const unformatted = candidates.filter(c => target[c.path] !== c.target).map(c => c.path);
  return { proven: true, unproven: [], unformatted, result: unformatted.length > 0 ? null : theirs };
}

// ── The runner side ───────────────────────────────────────────────────────────────────────────────────────────────

const CHUNK = 400;

function sh(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, ...opts });
  if (r.error) throw new Error(`${cmd}: ${r.error.message}`);
  return r;
}

function must(cmd, args, opts = {}) {
  const r = sh(cmd, args, opts);
  if (r.status !== 0) throw new Error(`${cmd} ${args.slice(0, 3).join(' ')} exited ${r.status}: ${(r.stderr || r.stdout || '').trim().slice(0, 2000)}`);
  return r.stdout;
}

const git = (...args) => must('git', args);

function chunks(list) {
  const out = [];
  for (let i = 0; i < list.length; i += CHUNK) out.push(list.slice(i, i + CHUNK));
  return out;
}

function gha(name, value) {
  if (process.env.GITHUB_OUTPUT) fs.appendFileSync(process.env.GITHUB_OUTPUT, `${name}=${value}\n`);
  console.log(`${name}=${value}`);
}

const workRepo = { exists: p => fs.existsSync(p), read: p => fs.readFileSync(p, 'utf8') };

/** Regular files at HEAD (mode 100644 or 100755; never links or submodules). */
function trackedFiles() {
  return git('ls-files', '-s', '-z').split('\0').filter(Boolean)
    .map(l => /^(\d{6}) [0-9a-f]+ \d\t(.*)$/s.exec(l))
    .filter(m => m && (m[1] === '100644' || m[1] === '100755'))
    .map(m => m[2]);
}

class Runner {
  constructor(mode, pins) {
    this.mode = mode;
    this.pins = pins;
    this.versions = {};
    this.temp = fs.mkdtempSync(path.join(process.env.RUNNER_TEMP || os.tmpdir(), 'format-'));
    this.remaining = 0;
  }

  version(id, cmd, args, opts) {
    if (this.versions[id]) return;
    const r = sh(cmd, args, opts);
    this.versions[id] = (r.stdout || r.stderr || '').trim().split('\n')[0];
  }

  ensureNode() {
    if (this.nodeReady) return;
    this.nodeReady = true;
    const { lockfile, packageManager } = this.pins.node;
    const pm = packageManager?.split('@')[0];
    // S2: always install, scripts off; npx <tool>@<version> without an install breaks on any plugin.
    if (lockfile === 'pnpm-lock.yaml' || pm === 'pnpm') must('corepack', ['pnpm', 'install', '--frozen-lockfile', '--ignore-scripts'], { stdio: 'inherit' });
    else if (lockfile === 'yarn.lock' || pm === 'yarn') {
      const berry = fs.existsSync('.yarnrc.yml') || /^yarn@[2-9]/.test(packageManager ?? '');
      must('corepack', berry ? ['yarn', 'install', '--immutable', '--mode=skip-build'] : ['yarn', 'install', '--frozen-lockfile', '--ignore-scripts'],
        { stdio: 'inherit', env: { ...process.env, YARN_ENABLE_SCRIPTS: 'false' } });
    } else if (lockfile === 'package-lock.json') must('npm', ['ci', '--ignore-scripts', '--no-audit', '--no-fund'], { stdio: 'inherit' });
    else throw new Error(`No supported lockfile for the JS tools (${lockfile ?? 'none'}): commit package-lock.json, pnpm-lock.yaml or yarn.lock.`);
  }

  ensurePython() {
    if (this.python) return this.python;
    const { tool, version } = this.pins.python;
    const venv = path.join(this.temp, 'venv');
    must('python3', ['-m', 'venv', venv]);
    const bin = path.join(venv, 'bin');
    must(path.join(bin, 'pip'), ['install', '--quiet', version ? `${tool}==${version}` : tool]);
    this.python = bin;
    return bin;
  }

  ensureRust(component) {
    sh('rustup', ['component', 'add', component]);
  }

  /** Runs one tool over its files, in place. */
  run(id, files) {
    if (files.length === 0) return;
    switch (id) {
      case 'dotnet-whitespace':
        this.version(id, 'dotnet', ['format', '--version']);
        for (const c of chunks(files)) sh('dotnet', ['format', 'whitespace', '--folder', '--include', ...c], { stdio: 'inherit' });
        return;
      case 'dotnet-style':
      case 'dotnet-analyzers':
        this.version('dotnet', 'dotnet', ['format', '--version']);
        for (const c of chunks(files))
          sh('dotnet', ['format', id === 'dotnet-style' ? 'style' : 'analyzers', this.pins.dotnet.solution, '--include', ...c], { stdio: 'inherit' });
        return;
      case 'prettier':
        this.ensureNode();
        this.version(id, 'npx', ['--no-install', 'prettier', '--version']);
        for (const c of chunks(files)) must('npx', ['--no-install', 'prettier', '--write', '--ignore-unknown', '--log-level', 'warn', ...c], { stdio: 'inherit' });
        return;
      case 'eslint': {
        this.ensureNode();
        this.version(id, 'npx', ['--no-install', 'eslint', '--version']);
        const root = process.cwd();
        for (const [dir, group] of groupByNearestConfig(files, p => fs.existsSync(p), ESLINT_CONFIGS)) {
          const cwd = path.join(root, dir);
          for (const c of chunks(group.map(f => path.posix.relative(dir, f)))) {
            const report = path.join(this.temp, 'eslint.json');
            sh('npx', ['--no-install', 'eslint', '--fix', '--format', 'json', '--output-file', report, ...c], { cwd });
            try { this.remaining += JSON.parse(fs.readFileSync(report, 'utf8')).reduce((n, r) => n + r.errorCount + r.warningCount, 0); } catch { /* none written */ }
          }
        }
        return;
      }
      case 'gofmt':
        this.version(id, 'go', ['version']);
        for (const c of chunks(files)) must('gofmt', ['-w', ...c]);
        return;
      case 'rustfmt': {
        this.ensureRust('rustfmt');
        this.version(id, 'rustfmt', ['--version']);
        const byEdition = new Map();
        for (const f of files) {
          const e = rustEdition(f, workRepo.read, workRepo.exists);
          byEdition.set(e, [...(byEdition.get(e) ?? []), f]);
        }
        for (const [edition, group] of byEdition) for (const c of chunks(group)) must('rustfmt', ['--edition', edition, ...c]);
        return;
      }
      case 'clippy':
        this.ensureRust('clippy');
        this.version(id, 'cargo', ['clippy', '--version']);
        // clippy fixes the whole workspace; run() keeps only `files` afterwards.
        sh('cargo', ['clippy', '--fix', '--allow-dirty', '--allow-no-vcs', '--workspace', '--all-targets'], { stdio: 'inherit' });
        return;
      case 'ruff-format':
      case 'ruff-check': {
        const bin = this.ensurePython();
        this.version('ruff', path.join(bin, 'ruff'), ['--version']);
        for (const c of chunks(files))
          sh(path.join(bin, 'ruff'), id === 'ruff-format' ? ['format', ...c] : ['check', '--fix', '--exit-zero', ...c], { stdio: 'inherit' });
        return;
      }
      case 'black': {
        const bin = this.ensurePython();
        this.version(id, path.join(bin, 'black'), ['--version']);
        for (const c of chunks(files)) sh(path.join(bin, 'black'), ['--quiet', ...c], { stdio: 'inherit' });
        return;
      }
      case 'terraform-fmt':
        this.version(id, 'terraform', ['version']);
        for (const f of files) must('terraform', ['fmt', f]);
        return;
      default:
        throw new Error(`Unknown tool ${id}.`);
    }
  }
}

function writeAll(variant) {
  for (const [file, text] of Object.entries(variant)) fs.writeFileSync(file, text);
}

function readAll(files) {
  return Object.fromEntries(files.map(f => [f, fs.readFileSync(f, 'utf8')]));
}

const utf8 = new TextDecoder('utf-8', { fatal: true });

/** The file at `rev` as text; null when absent, undefined when it is not UTF-8 (the proof cannot handle it). */
function blob(rev, file) {
  const r = sh('git', ['cat-file', 'blob', `${rev}:${file}`], { encoding: 'buffer' });
  if (r.status !== 0) return null;
  try { return utf8.decode(r.stdout); } catch { return undefined; }
}

function mergeFile(version, side, ours, base, theirs) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'merge-'));
  const [o, b, t] = ['ours', 'base', 'theirs'].map(n => path.join(dir, n));
  fs.writeFileSync(o, ours); fs.writeFileSync(b, base); fs.writeFileSync(t, theirs);
  const r = sh('git', mergeFileArgs(version, side, o, b, t));
  fs.rmSync(dir, { recursive: true, force: true });
  if (r.status < 0 || r.status > 127) throw new Error(`git merge-file exited ${r.status}.`);
  return { conflicts: r.status, text: r.stdout };
}

/** The conflicted files the writer resolved with the pull request's side (D17): the prepared commit holds exactly
 *  `merge-file --theirs`, and `--ours` would have been different. Paths resolved by other strategies are not ours.
 *  `refused` are such files this run may not touch (`.github/**`, `exclude`) or cannot read as text: the run fails on
 *  them rather than leave a resolution unproven. */
function pipelineCandidates(all, files, oursSha, theirsSha, mergeBase, version) {
  const allowed = new Set(files);
  const tracked = new Set(all);
  const touched = new Set(git('diff', '--name-only', '-z', '--no-renames', mergeBase, oursSha).split('\0').filter(Boolean));
  const candidates = [];
  const refused = [];
  for (const file of git('diff', '--name-only', '-z', '--no-renames', mergeBase, theirsSha).split('\0').filter(Boolean)) {
    if (!touched.has(file) || !tracked.has(file)) continue;
    const blobs = [blob(mergeBase, file), blob(oursSha, file), blob(theirsSha, file), blob('HEAD', file)];
    if (blobs.some(b => b === null)) continue;
    if (blobs.some(b => b === undefined)) {
      // Not text: merge-file still conflicts on it, so anything resolved here was not a whitespace rule's doing
      // unless HEAD differs from both sides; refuse only then.
      const raw = rev => sh('git', ['rev-parse', `${rev}:${file}`]).stdout.trim();
      if (raw('HEAD') !== raw(oursSha) && raw('HEAD') !== raw(theirsSha)) refused.push(file);
      continue;
    }
    const [base, ours, theirs, head] = blobs;
    const conflicted = mergeFile(version, null, ours, base, theirs);
    if (conflicted.conflicts === 0) continue;
    const oursResolution = mergeFile(version, 'ours', ours, base, theirs).text;
    const theirsResolution = mergeFile(version, 'theirs', ours, base, theirs).text;
    if (head !== theirsResolution || oursResolution === theirsResolution) continue;
    if (!allowed.has(file)) refused.push(file);
    else candidates.push({ path: file, target: ours, oursResolution, theirsResolution });
  }
  return { candidates, refused };
}

function formatterFor(runner, tools) {
  return async variant => {
    writeAll(variant);
    for (const tool of tools) runner.run(tool.id, tool.files.filter(f => f in variant));
    return readAll(Object.keys(variant));
  };
}

function writePatch(outDir, keep) {
  // Keep only what the run may change; restore everything else (clippy and dotnet style reach further than told).
  const changed = git('diff', '--name-only', '-z', 'HEAD').split('\0').filter(Boolean);
  const stray = changed.filter(f => !keep.has(f));
  for (const c of chunks(stray)) git('checkout', 'HEAD', '--', ...c);
  // Untracked files the tools left (caches, venvs) never reach the patch: `git diff HEAD` shows tracked files only.
  const patch = git('diff', '--no-color', '--no-ext-diff', '--no-renames', '--binary', '--full-index', 'HEAD');
  fs.writeFileSync(path.join(outDir, 'merge-queue.patch'), patch);
  const files = git('diff', '--name-only', '-z', 'HEAD').split('\0').filter(Boolean);
  gha('patch-path', path.join(outDir, 'merge-queue.patch'));
  gha('changed', String(files.length > 0));
  gha('file-count', String(files.length));
  return files;
}

async function main(command) {
  const mode = process.env.MODE ?? 'format';
  if (!['format', 'lint', 'pipeline'].includes(mode)) throw new Error(`mode must be format, lint or pipeline, not '${mode}'.`);
  const expected = (process.env.EXPECTED_SHA ?? '').trim();
  const head = git('rev-parse', 'HEAD').trim();
  if (expected && expected !== head) throw new Error(`The checkout is ${head}, not the expected ${expected}.`);
  if (mode === 'pipeline' && !expected) throw new Error('The pipeline mode needs expected_sha.');
  const exclude = parseList(process.env.EXCLUDE);
  const all = trackedFiles();
  const files = selectFiles(all, { mode, exclude });
  const pins = detectPins(workRepo, all);

  if (command === 'plan') {
    const { tools } = planTools(mode, pins, files);
    gha('dotnet', String(tools.some(t => t.id.startsWith('dotnet'))));
    gha('go', String(tools.some(t => t.id === 'gofmt')));
    gha('terraform', tools.some(t => t.id === 'terraform-fmt') ? pins.terraform : '');
    return 0;
  }

  const outDir = process.env.OUT_DIR || fs.mkdtempSync(path.join(os.tmpdir(), 'format-out-'));
  fs.mkdirSync(outDir, { recursive: true });
  const report = { mode, head, exclude, tools: [], notes: [], remainingProblems: 0, files: [] };
  let runner = null;
  const finish = (keep, code) => {
    report.files = writePatch(outDir, keep);
    report.tools = report.tools.map(t => ({ ...t, version: runner?.versions[t.id] ?? runner?.versions[t.id.split('-')[0]] ?? null }));
    report.remainingProblems = runner?.remaining ?? 0;
    fs.writeFileSync(path.join(outDir, 'format-report.json'), JSON.stringify(report, null, 2));
    for (const n of report.notes) console.log(`::notice::${n}`);
    return code;
  };

  if (mode !== 'pipeline') {
    const { tools, notes } = planTools(mode, pins, files);
    report.notes.push(...notes);
    runner = new Runner(mode, pins);
    for (const tool of tools) {
      runner.run(tool.id, tool.files);
      report.tools.push({ id: tool.id, files: tool.files.length });
    }
    return finish(new Set(files), 0);
  }

  // The pipeline (D8–D10, D16, D17).
  const oursSha = (process.env.OURS_SHA ?? '').trim();
  const theirsSha = (process.env.THEIRS_SHA ?? '').trim();
  const mergeBase = (process.env.MERGE_BASE ?? '').trim();
  if (![oursSha, theirsSha, mergeBase].every(s => /^[0-9a-f]{40}$/.test(s)))
    throw new Error('The pipeline mode needs ours_sha, theirs_sha and merge_base (full SHAs); is the workflow the formatting template?');
  const version = parseGitVersion(git('--version'));
  if (!atLeast(version, 2, 43)) throw new Error(`git ${version?.join('.')} is older than 2.43, the oldest the proof was measured on.`);
  const { candidates, refused } = pipelineCandidates(all, files, oursSha, theirsSha, mergeBase, version);
  if (refused.length > 0) {
    console.log(`::error::The pull request's side was taken for ${refused.join(', ')}, which this run may not format (.github/**, exclude) or read as text, so the resolution cannot be proven. Resolve the conflict by hand.`);
    return finish(new Set(), 1);
  }
  if (candidates.length === 0) {
    report.notes.push('No conflict was resolved with the pull request\'s side: nothing to prove or format.');
    return finish(new Set(), 0);
  }
  const inputs = formatterInputsChanged(git('diff', '--name-only', '-z', '--no-renames', mergeBase, theirsSha).split('\0').filter(Boolean));
  if (inputs.length > 0) {
    console.log(`::error::The pull request changes what the formatter reads (${inputs.join(', ')}), so this run cannot prove that ${candidates.map(c => c.path).join(', ')} differ only in whitespace (D9.3). Resolve the conflict by hand.`);
    return finish(new Set(), 1);
  }
  const { tools, notes } = planTools('pipeline', pins, candidates.map(c => c.path));
  report.notes.push(...notes);
  const covered = new Set(tools.flatMap(t => t.files));
  const bare = candidates.filter(c => !covered.has(c.path)).map(c => c.path);
  if (bare.length > 0) {
    console.log(`::error::No formatter of this repository covers ${bare.join(', ')}, so their conflicts cannot be proven whitespace-only.`);
    return finish(new Set(), 1);
  }
  runner = new Runner(mode, pins);
  for (const tool of tools) report.tools.push({ id: tool.id, files: tool.files.length });
  const verdict = await prove(candidates, formatterFor(runner, tools));
  if (!verdict.proven) {
    writeAll(Object.fromEntries(candidates.map(c => [c.path, c.theirsResolution])));
    console.log(`::error::Not whitespace-only once formatted: ${verdict.unproven.join(', ')}. The conflict needs a human.`);
    return finish(new Set(), 1);
  }
  if (verdict.result) {
    writeAll(verdict.result);
    report.notes.push(`Proven whitespace-only and formatted: ${candidates.map(c => c.path).join(', ')}.`);
  } else {
    writeAll(Object.fromEntries(candidates.map(c => [c.path, c.theirsResolution])));
    report.notes.push(`Proven whitespace-only, left unformatted because the base branch's copy is not formatted (D16): ${verdict.unformatted.join(', ')}. Run FormatRepository first.`);
  }
  return finish(new Set(candidates.map(c => c.path)), 0);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  main(process.argv[2] ?? 'run').then(code => process.exit(code), e => {
    console.log(`::error::${e.message}`);
    process.exit(1);
  });
}
