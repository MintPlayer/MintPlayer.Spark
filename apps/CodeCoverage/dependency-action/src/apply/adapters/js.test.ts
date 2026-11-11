import * as fs from 'node:fs';
import * as path from 'node:path';
import type { PlanItem } from '../../contracts/types';
import { ctxFor, fakeExec, item, tempDir, writeTree } from '../../testing/helpers';
import { npmAdapter, pnpmAdapter, yarnAdapter } from './js';

/**
 * The npm, pnpm and yarn adapters' tool steps (pin, relock, verify, consistency), with the package manager faked: which
 * command runs where, the manifest put back to our bytes after a manager rewrote it (gap A), the peer warnings that
 * count as blockers on exit 0 (D-C2), and the exactness check against each lockfile format.
 */

const PKG = '{\n  "dependencies": {\n    "express": "^4.19.2"\n  }\n}\n';

const npmItem = (lock: string, packageFile = 'package.json', depName = 'express'): PlanItem =>
  item({ depName, datasource: 'npm', manager: 'npm', depType: 'dependencies', packageFile, lockFiles: [lock], currentValue: '^4.18.2', newValue: '^4.19.2', newVersion: '4.19.2' });

function repo(files: Record<string, string>): string {
  const dir = tempDir('dep-js-');
  writeTree(dir, files);
  return dir;
}

const read = (dir: string, rel: string) => fs.readFileSync(path.join(dir, ...rel.split('/')), 'utf8');

describe('npm adapter', () => {
  it('matches npm items without a pnpm or yarn lockfile, and lists manifest and lockfiles', async () => {
    expect(npmAdapter.matches(npmItem('package-lock.json'))).toBe(true);
    expect(npmAdapter.matches(npmItem('pnpm-lock.yaml'))).toBe(false);
    expect(await npmAdapter.files(ctxFor('.'), npmItem('package-lock.json'))).toEqual(['package.json', 'package-lock.json']);
    expect(npmAdapter.tools(npmItem('package-lock.json'))).toEqual(['npm']);
    expect(npmAdapter.consistencyDir!(npmItem('web/package-lock.json', 'web/package.json'))).toBe('web');
  });

  it('pins per workspace member and puts back a package.json npm rewrote', async () => {
    const dir = repo({ 'package.json': PKG, 'packages/a/package.json': PKG, 'package-lock.json': '{}' });
    const { exec, calls } = fakeExec((cmd, _args, cwd) => {
      fs.writeFileSync(path.join(cwd, 'package.json'), '{"rewritten":true}');
      return { code: 0 };
    });

    const r = await npmAdapter.pin!(ctxFor(dir, exec), [npmItem('package-lock.json'), npmItem('package-lock.json', 'packages/a/package.json', 'chalk')]);

    expect(r.ok).toBe(true);
    expect(calls.map((c) => c.args.slice(0, 2).concat(c.args.filter((a) => a.startsWith('--workspace'))))).toEqual([
      ['install', 'express@4.19.2'],
      ['install', 'chalk@4.19.2', '--workspace=packages/a'],
    ]);
    expect(read(dir, 'package.json')).toBe(PKG);
  });

  it('a failed pin puts the manifest back and reports the tool output', async () => {
    const dir = repo({ 'package.json': PKG, 'package-lock.json': '{}' });
    const { exec } = fakeExec((_cmd, _args, cwd) => {
      fs.writeFileSync(path.join(cwd, 'package.json'), 'broken');
      return { code: 1, stderr: 'npm ERR! ERESOLVE' };
    });

    const r = await npmAdapter.pin!(ctxFor(dir, exec), [npmItem('package-lock.json')]);

    expect(r).toMatchObject({ ok: false, kind: 'resolver' });
    expect(r.output).toContain('ERESOLVE');
    expect(read(dir, 'package.json')).toBe(PKG);
  });

  it('relock fails on a non-zero exit and on a peer override warning even with exit 0', async () => {
    const dir = repo({ 'package.json': PKG, 'package-lock.json': '{}' });

    const failed = await npmAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 127, stderr: 'npm: not found' })).exec), [npmItem('package-lock.json')]);
    const peer = await npmAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 0, stderr: 'npm WARN ERESOLVE overriding peer dependency' })).exec), [npmItem('package-lock.json')]);
    const ok = await npmAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 0 })).exec), [npmItem('package-lock.json')]);

    expect(failed).toMatchObject({ ok: false, kind: 'tool' });
    expect(peer).toMatchObject({ ok: false, kind: 'resolver' });
    expect(ok.ok).toBe(true);
  });

  it('verify finds the planned version at the root or under the workspace member', async () => {
    const lock = { packages: { 'node_modules/express': { version: '4.19.2' }, 'packages/a/node_modules/chalk': { version: '4.19.2' } } };
    const dir = repo({ 'package-lock.json': JSON.stringify(lock) });

    const ok = await npmAdapter.verify!(ctxFor(dir), [npmItem('package-lock.json'), npmItem('package-lock.json', 'packages/a/package.json', 'chalk')]);
    const miss = await npmAdapter.verify!(ctxFor(dir), [npmItem('package-lock.json', 'package.json', 'lodash')]);

    expect(ok.ok).toBe(true);
    expect(miss).toMatchObject({ ok: false, kind: 'resolver' });
    expect(miss.output).toContain('lodash@4.19.2 in package-lock.json');
  });

  it('consistency runs npm ci --dry-run only where a package-lock.json exists', async () => {
    const dir = repo({ 'web/package-lock.json': '{}' });
    const { exec, calls } = fakeExec(() => ({ code: 0 }));

    expect((await npmAdapter.consistency!(ctxFor(dir, exec), '.')).ok).toBe(true);
    expect(calls).toHaveLength(0);
    expect((await npmAdapter.consistency!(ctxFor(dir, exec), 'web')).ok).toBe(true);
    expect(calls[0].args.slice(0, 2)).toEqual(['ci', '--ignore-scripts']);
  });

  it('parses npm blockers for the planned packages', async () => {
    const blockers = await npmAdapter.parseBlockers('', ctxFor('.'), [npmItem('package-lock.json')]);
    expect(blockers).toEqual([]);
  });
});

describe('pnpm adapter', () => {
  const pnpmItem = (packageFile = 'package.json', depName = 'express') => ({ ...npmItem('pnpm-lock.yaml', packageFile, depName) });

  it('pins with pnpm update, filtered per member, and puts the manifest back', async () => {
    const dir = repo({ 'package.json': PKG, 'apps/web/package.json': PKG, 'pnpm-lock.yaml': '' });
    const { exec, calls } = fakeExec((_cmd, _args, cwd) => {
      fs.writeFileSync(path.join(cwd, 'package.json'), 'rewritten');
      return { code: 0 };
    });

    const r = await pnpmAdapter.pin!(ctxFor(dir, exec), [pnpmItem(), pnpmItem('apps/web/package.json', 'chalk')]);

    expect(r.ok).toBe(true);
    expect(calls[0].args).toContain('express@4.19.2');
    expect(calls[1].args).toEqual(expect.arrayContaining(['--filter', './apps/web']));
    expect(read(dir, 'package.json')).toBe(PKG);
  });

  it('a failed pin reports and restores', async () => {
    const dir = repo({ 'package.json': PKG, 'pnpm-lock.yaml': '' });
    const r = await pnpmAdapter.pin!(ctxFor(dir, fakeExec(() => ({ code: 1, stdout: 'no' })).exec), [pnpmItem()]);
    expect(r.ok).toBe(false);
  });

  it('relock fails on exit, and on a peer issue reported as ndjson', async () => {
    const dir = repo({ 'pnpm-lock.yaml': '' });
    const peer = JSON.stringify({
      name: 'pnpm:peer-dependency-issues',
      issuesByProjects: { '.': { bad: { react: [{ foundVersion: '17.0.2', wantedRange: '^18', parents: [{ name: 'express', version: '4.19.2' }] }] } } },
    });

    expect((await pnpmAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 1 })).exec), [pnpmItem()])).ok).toBe(false);
    expect((await pnpmAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 0 })).exec), [pnpmItem()])).ok).toBe(true);
    const r = await pnpmAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 0, stdout: peer })).exec), [pnpmItem()]);
    expect(r).toMatchObject({ ok: false, kind: 'resolver' });
    expect(await pnpmAdapter.parseBlockers(peer, ctxFor('.'), [pnpmItem()])).toEqual([
      expect.objectContaining({ depName: 'react', version: '17.0.2', code: 'ERR_PNPM_PEER_DEP_ISSUES' }),
    ]);
  });

  it('verify reads name@version from pnpm-lock.yaml', async () => {
    const dir = repo({ 'pnpm-lock.yaml': "packages:\n  express@4.19.2:\n    resolution: {}\n" });

    expect((await pnpmAdapter.verify!(ctxFor(dir), [pnpmItem()])).ok).toBe(true);
    expect((await pnpmAdapter.verify!(ctxFor(dir), [pnpmItem('package.json', 'chalk')])).output).toContain('chalk@4.19.2');
  });

  it('consistency runs a frozen install', async () => {
    const { exec, calls } = fakeExec(() => ({ code: 0 }));
    expect((await pnpmAdapter.consistency!(ctxFor(repo({}), exec), '.')).ok).toBe(true);
    expect(calls[0].args).toContain('--frozen-lockfile');
    expect(pnpmAdapter.consistencyDir!(pnpmItem())).toBe('.');
    expect(pnpmAdapter.tools(pnpmItem())).toEqual(['pnpm']);
    expect(await pnpmAdapter.parseBlockers('', ctxFor('.'), [pnpmItem()])).toEqual([]);
  });
});

describe('yarn adapter', () => {
  const yarnItem = (depName = 'express') => npmItem('yarn.lock', 'package.json', depName);
  const BERRY = '__metadata:\n  version: 8\n\n"express@npm:^4.19.2":\n  version: 4.19.2\n  resolution: "express@npm:4.19.2"\n';

  it('refuses a yarn classic lockfile', async () => {
    const dir = repo({ 'package.json': PKG, 'yarn.lock': '# yarn lockfile v1\n' });
    const r = await yarnAdapter.pin!(ctxFor(dir, fakeExec(() => ({ code: 0 })).exec), [yarnItem()]);
    expect(r).toMatchObject({ ok: false, kind: 'tool' });
    expect(r.output).toContain('yarn classic');
  });

  it('pins with corepack yarn set resolution and puts the manifest back, also after a failure', async () => {
    const dir = repo({ 'package.json': PKG, 'yarn.lock': BERRY });
    const { exec, calls } = fakeExec((_cmd, _args, cwd) => {
      fs.writeFileSync(path.join(cwd, 'package.json'), 'normalised');
      return { code: 0 };
    });

    expect((await yarnAdapter.pin!(ctxFor(dir, exec), [yarnItem()])).ok).toBe(true);
    expect(calls[0]).toMatchObject({ cmd: 'corepack', args: ['yarn', 'set', 'resolution', 'express@npm:^4.19.2', 'npm:4.19.2'] });
    expect(read(dir, 'package.json')).toBe(PKG);

    const failed = await yarnAdapter.pin!(ctxFor(dir, fakeExec(() => ({ code: 1, stderr: 'YN0001' })).exec), [yarnItem()]);
    expect(failed.ok).toBe(false);
    expect(read(dir, 'package.json')).toBe(PKG);
  });

  it('relock fails on exit and on a YN0060 peer warning', async () => {
    const dir = repo({ 'yarn.lock': BERRY });
    const peer = JSON.stringify({ type: 'warning', name: 60, displayName: 'YN0060', data: "react is listed by your project with version 17.0.2 (pcb45e), which doesn't satisfy what express requests (^18.2.0)." });

    expect((await yarnAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 1 })).exec), [yarnItem()])).ok).toBe(false);
    expect((await yarnAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 0 })).exec), [yarnItem()])).ok).toBe(true);
    const r = await yarnAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 0, stdout: peer })).exec), [yarnItem()]);
    expect(r).toMatchObject({ ok: false, kind: 'resolver' });
  });

  it('verify reads the resolution line', async () => {
    const dir = repo({ 'yarn.lock': BERRY });
    expect((await yarnAdapter.verify!(ctxFor(dir), [yarnItem()])).ok).toBe(true);
    expect((await yarnAdapter.verify!(ctxFor(dir), [yarnItem('chalk')])).ok).toBe(false);
  });

  it('consistency runs an immutable install', async () => {
    const { exec, calls } = fakeExec(() => ({ code: 0 }));
    expect((await yarnAdapter.consistency!(ctxFor(repo({}), exec), '.')).ok).toBe(true);
    expect(calls[0].args).toContain('--immutable');
    expect(yarnAdapter.consistencyDir!(yarnItem())).toBe('.');
    expect(yarnAdapter.tools(yarnItem())).toEqual(['corepack']);
    expect(yarnAdapter.parseBlockers('', ctxFor('.'), [yarnItem()])).toEqual([]);
  });
});
