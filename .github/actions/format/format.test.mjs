// Tests for format.mjs (node --test). The real formatters are not run here: the proof is tested with a fake formatter
// that behaves as S11 measured them (it rewrites spacing outside strings, never inside), and merge-file with real git.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  atLeast, detectPins, formatterInputsChanged, globToRegExp, groupByNearestConfig, matchesAny, mergeFileArgs,
  parseGitVersion, parseList, planTools, prove, rustEdition, selectFiles, ESLINT_CONFIGS,
} from './format.mjs';

const fakeRepo = files => ({ exists: p => p in files, read: p => files[p] });

test('parseList drops blanks and comments', () => {
  assert.deepEqual(parseList('a\n\n # note\n  b/**  \r\nc'), ['a', 'b/**', 'c']);
  assert.deepEqual(parseList(undefined), []);
});

test('globs: ** spans directories, * stays in a segment, a bare name matches at any depth', () => {
  assert.ok(globToRegExp('**/*.cs').test('a/b/c.cs'));
  assert.ok(globToRegExp('**/*.cs').test('c.cs'));
  assert.ok(!globToRegExp('src/*.cs').test('src/a/b.cs'));
  assert.ok(globToRegExp('src/*.cs').test('src/b.cs'));
  assert.ok(globToRegExp('*.g.cs').test('deep/x/Foo.g.cs'));
  assert.ok(globToRegExp('vendor/').test('vendor/a/b.js'));
  assert.ok(!globToRegExp('vendor/').test('src/vendor/a.js'));
  assert.ok(globToRegExp('./docs/**').test('docs/a.md'));
  assert.ok(!globToRegExp('a.c?').test('a.cs.x'));
  assert.ok(globToRegExp('file(1).txt').test('file(1).txt'));
});

test('selectFiles: exclude always, .github only in the pipeline', () => {
  const files = ['.github/workflows/ci.yml', 'src/a.cs', 'vendor/x.js', '.githubish/a.cs'];
  assert.deepEqual(selectFiles(files, { mode: 'format', exclude: ['vendor/**'] }), ['.github/workflows/ci.yml', 'src/a.cs', '.githubish/a.cs']);
  assert.deepEqual(selectFiles(files, { mode: 'pipeline', exclude: [] }), ['src/a.cs', 'vendor/x.js', '.githubish/a.cs']);
  assert.ok(matchesAny('a/b.min.js', ['*.min.js']));
});

test('git versions and the merge-file call (S10)', () => {
  assert.deepEqual(parseGitVersion('git version 2.47.1.windows.1'), [2, 47, 1]);
  assert.deepEqual(parseGitVersion('git version 2.43'), [2, 43, 0]);
  assert.equal(parseGitVersion('nope'), null);
  assert.ok(atLeast([2, 44, 0], 2, 44) && atLeast([3, 0, 0], 2, 44) && !atLeast([2, 43, 9], 2, 44));
  assert.ok(!mergeFileArgs([2, 43, 0], 'theirs', 'o', 'b', 't').some(a => a.startsWith('--diff-algorithm')));
  assert.deepEqual(mergeFileArgs([2, 54, 0], 'theirs', 'o', 'b', 't'),
    ['merge-file', '-p', '--theirs', '--diff-algorithm=myers', '-L', 'ours', '-L', 'base', '-L', 'theirs', 'o', 'b', 't']);
  assert.ok(mergeFileArgs([2, 54, 0], null, 'o', 'b', 't').includes('--diff3'));
});

test('formatter inputs (D9.3)', () => {
  assert.deepEqual(formatterInputsChanged(['src/a.cs', 'src/.editorconfig', 'global.json', 'web/package.json',
    '.prettierrc.json', 'prettier.config.mjs', 'rustfmt.toml', 'requirements-dev.txt', 'docs/x.md', 'Directory.Build.props']),
  ['src/.editorconfig', 'global.json', 'web/package.json', '.prettierrc.json', 'prettier.config.mjs', 'rustfmt.toml', 'requirements-dev.txt', 'Directory.Build.props']);
});

test('detectPins: JS from the root package.json and its lockfile', () => {
  const pins = detectPins(fakeRepo({
    'package.json': JSON.stringify({ devDependencies: { prettier: '3.3.3', eslint: '^9.10.0' }, packageManager: 'pnpm@9.12.0' }),
    'pnpm-lock.yaml': '',
  }), ['package.json', 'src/a.ts']);
  assert.equal(pins.prettier, '3.3.3');
  assert.equal(pins.eslint, '^9.10.0');
  assert.deepEqual(pins.node, { lockfile: 'pnpm-lock.yaml', packageManager: 'pnpm@9.12.0' });
});

test('detectPins: one root solution, Go toolchain, Rust channel, Terraform version', () => {
  const pins = detectPins(fakeRepo({
    'global.json': '{}', 'go.mod': 'module x\n\ngo 1.22\n\ntoolchain go1.22.5\n', 'Cargo.toml': '[package]',
    'rust-toolchain.toml': '[toolchain]\nchannel = "1.79.0"\n', '.terraform-version': 'v1.9.2\n',
  }), ['App.slnx', 'src/App.csproj', 'nested/Other.sln', 'main.tf']);
  assert.deepEqual(pins.dotnet, { globalJson: true, solution: 'App.slnx', solutions: 1 });
  assert.equal(pins.go, '1.22.5');
  assert.equal(pins.rust, '1.79.0');
  assert.equal(pins.terraform, '1.9.2');
  assert.equal(detectPins(fakeRepo({ 'go.mod': 'module x\ngo 1.21\n' }), []).go, '1.21');
  assert.equal(detectPins(fakeRepo({}), ['a.sln', 'b.slnx']).dotnet.solution, null);
});

test('detectPins: Python tool and version from uv.lock, requirements or pre-commit (S8)', () => {
  const uv = detectPins(fakeRepo({
    'pyproject.toml': '[tool.ruff]\nline-length = 100\n',
    'uv.lock': 'version = 1\n\n[[package]]\nname = "ruff"\nversion = "0.6.9"\nsource = {}\n',
  }), []);
  assert.deepEqual(uv.python, { tool: 'ruff', version: '0.6.9' });

  const req = detectPins(fakeRepo({ 'pyproject.toml': '[tool.black]\n', 'requirements-dev.txt': 'pytest==8\nblack==24.8.0\n' }),
    ['requirements-dev.txt']);
  assert.deepEqual(req.python, { tool: 'black', version: '24.8.0' });

  const preCommit = detectPins(fakeRepo({
    'ruff.toml': '',
    '.pre-commit-config.yaml': 'repos:\n  - repo: https://github.com/astral-sh/ruff-pre-commit\n    rev: v0.5.0\n    hooks: []\n',
  }), []);
  assert.deepEqual(preCommit.python, { tool: 'ruff', version: '0.5.0' });

  assert.equal(detectPins(fakeRepo({ 'pyproject.toml': '[project]\n' }), []).python, undefined);
});

test('planTools: tools per mode, with the reasons for the skipped ones', () => {
  const pins = { dotnet: { solution: null, solutions: 0 }, prettier: '3', go: '1.22' };
  const files = ['a.cs', 'b.ts', 'c.go', 'd.py', 'e.md', 'f.tf'];
  const format = planTools('format', pins, files);
  assert.deepEqual(format.tools.map(t => t.id), ['dotnet-whitespace', 'prettier', 'gofmt', 'terraform-fmt']);
  assert.deepEqual(format.tools.find(t => t.id === 'prettier').files, ['b.ts', 'e.md']);
  assert.ok(format.notes.some(n => n.startsWith('dotnet-style: skipped, there is no solution')));
  assert.ok(format.notes.some(n => n.startsWith('ruff-format: skipped')));

  const pipeline = planTools('pipeline', pins, files);
  assert.ok(!pipeline.tools.some(t => t.id === 'dotnet-style'));

  const lint = planTools('lint', { ...pins, eslint: '9' }, files);
  assert.deepEqual(lint.tools.map(t => t.id), ['eslint']);
  assert.ok(lint.notes.some(n => n.includes('Go and Terraform')));
});

test('ESLint groups files under their nearest config (S2)', () => {
  const exists = p => ['eslint.config.mjs', 'apps/web/eslint.config.js'].includes(p);
  const groups = groupByNearestConfig(['apps/web/src/a.ts', 'apps/web/b.ts', 'libs/x/c.ts', 'd.ts'], exists, ESLINT_CONFIGS);
  assert.deepEqual([...groups], [['apps/web', ['apps/web/src/a.ts', 'apps/web/b.ts']], ['', ['libs/x/c.ts', 'd.ts']]]);
});

test('rustfmt edition from the nearest Cargo.toml, workspace inheritance included', () => {
  const files = {
    'Cargo.toml': '[workspace]\nmembers = ["crates/*"]\n\n[workspace.package]\nedition = "2021"\n',
    'crates/a/Cargo.toml': '[package]\nname = "a"\nedition.workspace = true\n',
    'crates/b/Cargo.toml': '[package]\nname = "b"\nedition = "2018"\n',
  };
  const read = p => files[p];
  const exists = p => p in files;
  assert.equal(rustEdition('crates/a/src/lib.rs', read, exists), '2021');
  assert.equal(rustEdition('crates/b/src/lib.rs', read, exists), '2018');
  assert.equal(rustEdition('src/main.rs', p => '[package]\n', p => p === 'Cargo.toml'), '2015');
});

// ── The proof (D16, D17) ──────────────────────────────────────────────────────────────────────────────────────────

/** Formats as the S11 formatters do: runs of spaces and tabs outside string literals become one space, indentation is
 *  two spaces per level of leading whitespace pairs, trailing whitespace goes. Text inside "…" is kept. */
function fakeFormat(text) {
  return text.split('\n').map(line => {
    const indent = line.match(/^[ \t]*/)[0].replace(/\t/g, '  ').length;
    let out = '';
    let inString = false;
    let previousSpace = false;
    for (const c of line.trimStart()) {
      if (c === '"') inString = !inString;
      if (!inString && (c === ' ' || c === '\t')) {
        if (!previousSpace) out += ' ';
        previousSpace = true;
        continue;
      }
      previousSpace = false;
      out += c;
    }
    return ' '.repeat(Math.floor(indent / 2) * 2) + out.trimEnd();
  }).join('\n');
}

const formatter = async variant => Object.fromEntries(Object.entries(variant).map(([p, t]) => [p, fakeFormat(t)]));

test('prove: a spacing-only conflict is proven and formatted', async () => {
  const verdict = await prove([{
    path: 'a.cs', target: 'int x = 1;\n',
    oursResolution: 'int x = 1;\nint y = 2;\n', theirsResolution: 'int  x  =  1;\nint y   = 2;  \n',
  }], formatter);
  assert.equal(verdict.proven, true);
  assert.deepEqual(verdict.unformatted, []);
  assert.deepEqual(verdict.result, { 'a.cs': 'int x = 1;\nint y = 2;\n' });
});

test('prove: whitespace inside a string literal is refused (S7, S11)', async () => {
  const verdict = await prove([
    { path: 'ok.cs', target: 'a;\n', oursResolution: 'a ;\n', theirsResolution: 'a  ;\n' },
    { path: 'str.cs', target: 's = "a b";\n', oursResolution: 's = "a b";\n', theirsResolution: 's = "a  b";\n' },
  ], formatter);
  assert.equal(verdict.proven, false);
  assert.deepEqual(verdict.unproven, ['str.cs']);
});

test('prove: an unformatted base copy keeps the proven resolution as it is (D16)', async () => {
  const verdict = await prove([{
    path: 'a.cs', target: 'int  x = 1;\n', oursResolution: 'int  x = 1;\n', theirsResolution: 'int x  = 1;\n',
  }], formatter);
  assert.equal(verdict.proven, true);
  assert.deepEqual(verdict.unformatted, ['a.cs']);
  assert.equal(verdict.result, null);
});

// ── The pipeline end to end, on a temporary repository (the refusals need no formatter) ─────────────────────────

const script = fileURLToPath(new URL('./format.mjs', import.meta.url));

function pipelineRepo(file, baseText, oursText, theirsText) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'format-pipe-'));
  const git = (...args) => {
    const r = spawnSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.invalid', '-c', 'core.autocrlf=false', ...args],
      { cwd: dir, encoding: 'utf8' });
    assert.equal(r.status, 0, r.stderr);
    return r.stdout.trim();
  };
  const put = text => { fs.mkdirSync(path.dirname(path.join(dir, file)), { recursive: true }); fs.writeFileSync(path.join(dir, file), text); };
  git('init', '-q', '-b', 'main');
  put(baseText); git('add', '-A'); git('commit', '-qm', 'base');
  const mergeBase = git('rev-parse', 'HEAD');
  put(theirsText); git('commit', '-qam', 'pr');
  const theirs = git('rev-parse', 'HEAD');
  git('checkout', '-q', '-b', 'target', mergeBase);
  put(oursText); git('commit', '-qam', 'target');
  const ours = git('rev-parse', 'HEAD');
  return { dir, git, put, mergeBase, ours, theirs };
}

function runPipeline(repo, extra = {}) {
  const out = path.join(repo.dir, '..', path.basename(repo.dir) + '-out');
  const r = spawnSync(process.execPath, [script, 'run'], {
    cwd: repo.dir, encoding: 'utf8',
    env: { ...process.env, GITHUB_OUTPUT: '', MODE: 'pipeline', EXPECTED_SHA: repo.git('rev-parse', 'HEAD'), OURS_SHA: repo.ours,
      THEIRS_SHA: repo.theirs, MERGE_BASE: repo.mergeBase, OUT_DIR: out, EXCLUDE: '', ...extra },
  });
  const patch = fs.existsSync(path.join(out, 'merge-queue.patch')) ? fs.readFileSync(path.join(out, 'merge-queue.patch'), 'utf8') : null;
  fs.rmSync(out, { recursive: true, force: true });
  fs.rmSync(repo.dir, { recursive: true, force: true });
  return { status: r.status, stdout: r.stdout + r.stderr, patch };
}

/** The writer's whitespace resolution: the prepared commit on the target holds merge-file --theirs. */
function prepareTheirs(repo, file, text) {
  repo.put(text);
  repo.git('commit', '-qam', 'prepared');
}

test('pipeline: nothing resolved with the pull request\'s side gives an empty patch', () => {
  const repo = pipelineRepo('a.txt', 'x\n', 'x ours\n', 'x theirs\n');
  repo.put('x ours\n');
  const r = runPipeline(repo);
  assert.equal(r.status, 0, r.stdout);
  assert.equal(r.patch, '');
});

test('pipeline: a .github file resolved with the pull request\'s side fails the run', () => {
  const repo = pipelineRepo('.github/workflows/ci.yml', 'a: 1\n', 'a: 2\n', 'a:  1\n');
  prepareTheirs(repo, '.github/workflows/ci.yml', 'a:  1\n');
  const r = runPipeline(repo);
  assert.equal(r.status, 1);
  assert.match(r.stdout, /may not format/);
  assert.equal(r.patch, '');
});

test('pipeline: an excluded file fails alike, and a file no formatter covers fails too', () => {
  const excluded = pipelineRepo('gen/a.cs', 'int x;\n', 'int x; // t\n', 'int  x;\n');
  prepareTheirs(excluded, 'gen/a.cs', 'int  x;\n');
  assert.match(runPipeline(excluded, { EXCLUDE: 'gen/**' }).stdout, /may not format/);

  const bare = pipelineRepo('notes.txt', 'a b\n', 'a b c\n', 'a  b\n');
  prepareTheirs(bare, 'notes.txt', 'a  b\n');
  const r = runPipeline(bare);
  assert.equal(r.status, 1);
  assert.match(r.stdout, /No formatter of this repository covers notes\.txt/);
});

test('pipeline: a changed formatter input fails the run (D9.3)', () => {
  const repo = pipelineRepo('a.cs', 'int x;\n', 'int x; // t\n', 'int  x;\n');
  repo.git('checkout', '-q', repo.theirs);
  repo.put('int  x;\n');
  fs.writeFileSync(path.join(repo.dir, '.editorconfig'), 'root = true\n');
  repo.git('add', '-A'); repo.git('commit', '-qm', 'pr config');
  repo.theirs = repo.git('rev-parse', 'HEAD');
  repo.git('checkout', '-q', repo.ours);
  prepareTheirs(repo, 'a.cs', 'int  x;\n');
  const r = runPipeline(repo);
  assert.equal(r.status, 1);
  assert.match(r.stdout, /changes what the formatter reads \(\.editorconfig\)/);
});

test('pipeline: the commits are required', () => {
  const repo = pipelineRepo('a.txt', 'x\n', 'y\n', 'z\n');
  const r = runPipeline(repo, { OURS_SHA: '' });
  assert.equal(r.status, 1);
  assert.match(r.stdout, /needs ours_sha, theirs_sha and merge_base/);
});

// ── merge-file with real git (S10) ────────────────────────────────────────────────────────────────────────────────

test('merge-file --ours/--theirs rebuild both whole-file resolutions of a conflict', () => {
  const version = parseGitVersion(spawnSync('git', ['--version'], { encoding: 'utf8' }).stdout);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'format-test-'));
  try {
    const write = (name, text) => { const p = path.join(dir, name); fs.writeFileSync(p, text); return p; };
    const base = write('base', 'class A\n{\n    int x = 1;\n}\n// tail\n');
    const ours = write('ours', 'class A\n{\n    int x = 1; // base edit\n}\n// tail\n');
    const theirs = write('theirs', 'class A\n{\n  int  x = 1;\n}\n// tail\n// pr addition\n');
    const run = side => spawnSync('git', mergeFileArgs(version, side, ours, base, theirs), { encoding: 'utf8' });

    const conflicted = run(null);
    assert.equal(conflicted.status, 1, 'one conflict');
    assert.match(conflicted.stdout, /<<<<<<< ours/);

    const o = run('ours');
    const t = run('theirs');
    assert.equal(o.status, 0);
    assert.equal(o.stdout, 'class A\n{\n    int x = 1; // base edit\n}\n// tail\n// pr addition\n');
    assert.equal(t.stdout, 'class A\n{\n  int  x = 1;\n}\n// tail\n// pr addition\n');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
