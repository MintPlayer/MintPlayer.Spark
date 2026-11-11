import * as fs from 'node:fs';
import * as path from 'node:path';
import type { PlanItem } from '../../contracts/types';
import { ctxFor, fakeExec, item, tempDir, writeTree } from '../../testing/helpers';
import { lockHasVersion, normalizePypi, pipAdapter, pipCompileAdapter, poetryAdapter, pypiName, uvAdapter } from './python';

/**
 * The PyPI adapters' tool steps (uv, Poetry, pip-compile, plain pip) with the tool faked: the commands and flags each
 * runs (D-S14b's --no-build with feed secrets), the exactness check against the lockfile, and PEP 503 name matching.
 */

const py = (partial: Partial<PlanItem> = {}): PlanItem =>
  item({ depName: 'requests', datasource: 'pypi', manager: 'pep621', packageFile: 'pyproject.toml', currentValue: '==2.28.2', newValue: '==2.31.0', newVersion: '2.31.0', ...partial });

function repo(files: Record<string, string>): string {
  const dir = tempDir('dep-py-');
  writeTree(dir, files);
  return dir;
}

const UV_LOCK = 'version = 1\n\n[[package]]\nname = "Requests"\nversion = "2.31.0"\n';

describe('PEP 503 names', () => {
  it('treat -, _ and . alike, case-insensitively', () => {
    expect(normalizePypi('Zope.Interface_x')).toBe('zope-interface-x');
    expect(pypiName('zope.interface').test('Zope_Interface==5')).toBe(true);
    expect(pypiName('requests').test('requests-oauthlib==1')).toBe(false);
  });

  it('find name and version pairs in a uv or poetry lock', () => {
    expect(lockHasVersion(UV_LOCK, 'requests', '2.31.0')).toBe(true);
    expect(lockHasVersion(UV_LOCK, 'requests', '2.28.2')).toBe(false);
  });
});

describe('uv adapter', () => {
  const uv = py({ lockFiles: ['uv.lock'] });

  it('relocks with --upgrade-package per item, and --no-build when feed secrets are present', async () => {
    const { exec, calls } = fakeExec(() => ({ code: 0 }));
    const ctx = { ...ctxFor(repo({}), exec), feedSecrets: true };

    expect((await uvAdapter.relock!(ctx, [uv, py({ depName: 'idna', lockFiles: ['uv.lock'], newVersion: '3.7' })])).ok).toBe(true);
    expect(calls[0]).toMatchObject({ cmd: 'uv', args: ['lock', '--upgrade-package', 'requests==2.31.0', '--upgrade-package', 'idna==3.7', '--no-build'] });
  });

  it('verifies the lock and checks consistency', async () => {
    const dir = repo({ 'uv.lock': UV_LOCK });
    const { exec, calls } = fakeExec(() => ({ code: 1, stderr: 'lockfile needs to be updated' }));

    expect((await uvAdapter.verify!(ctxFor(dir), [uv])).ok).toBe(true);
    expect((await uvAdapter.verify!(ctxFor(dir), [py({ lockFiles: ['uv.lock'], newVersion: '9.9' })])).output).toContain('requests==9.9 in uv.lock');
    expect((await uvAdapter.consistency!(ctxFor(dir, exec), '.')).ok).toBe(false);
    expect(calls[0].args).toEqual(['lock', '--check']);
    expect(uvAdapter.consistencyDir!(uv)).toBe('.');
    expect(uvAdapter.matches(uv)).toBe(true);
    expect(await uvAdapter.files(ctxFor(dir), uv)).toEqual(['pyproject.toml', 'uv.lock']);
    expect(uvAdapter.tools(uv)).toEqual(['uv']);
  });

  it('parses a resolver failure', () => {
    const output = 'No solution found when resolving dependencies:\n  ╰─▶ Because there is no version of requests==9.9 and you require requests==9.9, we can conclude that your requirements are unsatisfiable.\n';
    expect(uvAdapter.parseBlockers(output, ctxFor('.'), [uv])).toEqual([expect.objectContaining({ depName: 'requests', version: '9.9', code: 'uv-no-version' })]);
  });
});

describe('poetry adapter', () => {
  const poetry = py({ manager: 'poetry', lockFiles: ['sub/poetry.lock'], packageFile: 'sub/pyproject.toml' });

  it('relocks in the lockfile directory, verifies, and checks the lock', async () => {
    const dir = repo({ 'sub/poetry.lock': UV_LOCK });
    const { exec, calls } = fakeExec(() => ({ code: 0 }));

    expect((await poetryAdapter.relock!(ctxFor(dir, exec), [poetry])).ok).toBe(true);
    expect(calls[0]).toMatchObject({ cmd: 'poetry', args: ['lock', '--no-interaction'], cwd: path.join(dir, 'sub') });
    expect((await poetryAdapter.verify!(ctxFor(dir), [poetry])).ok).toBe(true);
    expect((await poetryAdapter.verify!(ctxFor(dir), [{ ...poetry, newVersion: '1.0' }])).ok).toBe(false);
    expect((await poetryAdapter.consistency!(ctxFor(dir, exec), 'sub')).ok).toBe(true);
    expect(calls[1].args).toEqual(['check', '--lock', '--no-interaction']);
    expect(poetryAdapter.consistencyDir!(poetry)).toBe('sub');
    expect(poetryAdapter.tools(poetry)).toEqual(['poetry']);
    expect(poetryAdapter.parseBlockers('all fine', ctxFor(dir), [poetry])).toEqual([]);
  });
});

describe('pip-compile adapter', () => {
  const compiled = py({ manager: 'pip-compile', packageFile: 'requirements.in', currentValue: '==2.28.2' });

  it('matches a .in file and lists the compiled .txt beside it', async () => {
    expect(pipCompileAdapter.matches(compiled)).toBe(true);
    expect(await pipCompileAdapter.files(ctxFor('.'), compiled)).toEqual(['requirements.in', 'requirements.txt']);
  });

  it('leaves an unpinned requirement alone: the update is lock-only', async () => {
    const dir = repo({ 'requirements.in': 'requests\n' });
    await pipCompileAdapter.edit(ctxFor(dir), compiled);
    expect(fs.readFileSync(path.join(dir, 'requirements.in'), 'utf8')).toBe('requests\n');
  });

  it('edits a pinned requirement', async () => {
    const dir = repo({ 'requirements.in': 'requests==2.28.2\n' });
    await pipCompileAdapter.edit(ctxFor(dir), compiled);
    expect(fs.readFileSync(path.join(dir, 'requirements.in'), 'utf8')).toBe('requests==2.31.0\n');
  });

  it('recompiles without a header unless the existing file has one, and stops at a failure', async () => {
    const dir = repo({ 'requirements.txt': '# autogenerated by pip-compile\nrequests==2.31.0\n' });
    const { exec, calls } = fakeExec(() => ({ code: 0 }));

    expect((await pipCompileAdapter.relock!(ctxFor(dir, exec), [compiled])).ok).toBe(true);
    expect(calls[0].args).toEqual(['--quiet', '--upgrade-package', 'requests==2.31.0', '--output-file', 'requirements.txt', 'requirements.in']);

    const bare = repo({});
    const second = fakeExec(() => ({ code: 2, stderr: 'boom' }));
    expect((await pipCompileAdapter.relock!(ctxFor(bare, second.exec), [compiled])).ok).toBe(false);
    expect(second.calls[0].args).toContain('--no-header');
  });

  it('verifies the compiled pin', async () => {
    const dir = repo({ 'requirements.txt': 'Requests==2.31.0 \\\n    --hash=sha256:x\n' });
    expect((await pipCompileAdapter.verify!(ctxFor(dir), [compiled])).ok).toBe(true);
    expect((await pipCompileAdapter.verify!(ctxFor(dir), [{ ...compiled, newVersion: '3.0' }])).output).toContain('requests==3.0 in requirements.txt');
    expect(pipCompileAdapter.tools(compiled)).toEqual(['pip-compile']);
  });
});

describe('pip adapter', () => {
  const req = py({ manager: 'pip_requirements', packageFile: 'requirements.txt' });

  it('dry-runs a requirements file, binary-only with feed secrets, and skips a missing one', async () => {
    const dir = repo({ 'requirements.txt': 'requests==2.31.0\n' });
    const { exec, calls } = fakeExec(() => ({ code: 0 }));

    expect((await pipAdapter.verify!({ ...ctxFor(dir, exec), feedSecrets: true }, [req, py({ packageFile: 'setup.py' }), py({ packageFile: 'other/requirements-dev.txt' })])).ok).toBe(true);
    expect(calls).toHaveLength(1);
    expect(calls[0].args).toEqual(expect.arrayContaining(['--dry-run', '--only-binary=:all:', '-r', 'requirements.txt']));
  });

  it('a failed dry run is the outcome, and its conflict parses into blockers', async () => {
    const dir = repo({ 'requirements.txt': 'requests==2.31.0\n' });
    const output = 'ERROR: Cannot install requests==2.31.0\n\nThe conflict is caused by:\n    requests 2.31.0 depends on urllib3<3\n\nResolutionImpossible';

    expect((await pipAdapter.verify!(ctxFor(dir, fakeExec(() => ({ code: 1, stderr: output })).exec), [req])).ok).toBe(false);
    expect(pipAdapter.parseBlockers(output, ctxFor(dir), [req])).toEqual([expect.objectContaining({ depName: 'requests', version: '2.31.0' })]);
  });

  it('matches only an unlocked PyPI manifest and needs python3 only for a requirements file', async () => {
    expect(pipAdapter.matches(req)).toBe(true);
    expect(pipAdapter.matches(py({ lockFiles: ['uv.lock'] }))).toBe(false);
    expect(pipAdapter.tools(req)).toEqual(['python3']);
    expect(pipAdapter.tools(py({ packageFile: 'setup.py' }))).toEqual([]);
    expect(await pipAdapter.files(ctxFor('.'), req)).toEqual(['requirements.txt']);
  });
});
