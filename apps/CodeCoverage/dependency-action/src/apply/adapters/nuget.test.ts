import * as fs from 'node:fs';
import * as path from 'node:path';
import type { PlanItem } from '../../contracts/types';
import { ctxFor, fakeExec, item, tempDir, writeTree } from '../../testing/helpers';
import { nugetAdapter, restoreTargets } from './nuget';

/**
 * The NuGet adapter's restore steps with dotnet faked: what it restores for a project or a props file (D-S14a: restore,
 * never build), packages.lock.json as the exactness check, the tool manifest, and blockers read from project.assets.json.
 */

const nu = (packageFile: string, depName = 'Newtonsoft.Json'): PlanItem =>
  item({ depName, datasource: 'nuget', manager: 'nuget', packageFile, currentValue: '13.0.1', newValue: '13.0.3' });

function repo(files: Record<string, string>): string {
  const dir = tempDir('dep-nuget-');
  writeTree(dir, files);
  return dir;
}

const lock = (resolved: string, type = 'Direct') => JSON.stringify({ version: 1, dependencies: { 'net8.0': { 'Newtonsoft.Json': { type, resolved } } } });

describe('nuget adapter', () => {
  it('matches the four manifest kinds only', () => {
    for (const f of ['App.csproj', 'Directory.Packages.props', '.config/dotnet-tools.json', 'global.json', 'Directory.Build.props']) expect(nugetAdapter.matches(nu(f))).toBe(true);
    expect(nugetAdapter.matches(nu('packages.config'))).toBe(false);
    expect(nugetAdapter.tools(nu('App.csproj'))).toEqual(['dotnet']);
    expect(nugetAdapter.consistencyDir!(nu('src/App.csproj'))).toBe('src/App.csproj');
  });

  it('restores a project itself, and a props file through its solution or else every project under it', async () => {
    const withSln = repo({ 'src/Directory.Packages.props': '', 'src/App.sln': '', 'src/A/A.csproj': '' });
    const withoutSln = repo({ 'Directory.Packages.props': '', 'A/A.csproj': '', 'B/B.fsproj': '', 'README.md': '' });

    expect(await restoreTargets(ctxFor(withSln), 'src/A/A.csproj')).toEqual(['src/A/A.csproj']);
    expect(await restoreTargets(ctxFor(withSln), 'src/Directory.Packages.props')).toEqual(['src/App.sln']);
    expect(await restoreTargets(ctxFor(withoutSln), 'Directory.Packages.props')).toEqual(['A/A.csproj', 'B/B.fsproj']);
  });

  it('lists the lockfiles under the manifest directory with the manifest', async () => {
    const dir = repo({ 'src/A/A.csproj': '', 'src/A/packages.lock.json': '{}', 'other/packages.lock.json': '{}' });
    expect(await nugetAdapter.files(ctxFor(dir), nu('src/A/A.csproj'))).toEqual(['src/A/A.csproj', 'src/A/packages.lock.json']);
  });

  it('relock force-evaluates each restore target once, then restores tools', async () => {
    const dir = repo({ 'A/A.csproj': '', '.config/dotnet-tools.json': '{}' });
    const { exec, calls } = fakeExec(() => ({ code: 0 }));

    const r = await nugetAdapter.relock!(ctxFor(dir, exec), [nu('A/A.csproj'), nu('A/A.csproj', 'Serilog'), nu('.config/dotnet-tools.json', 'dotnet-ef')]);

    expect(r.ok).toBe(true);
    expect(calls.map((c) => c.args)).toEqual([['restore', 'A/A.csproj', '--force-evaluate'], ['tool', 'restore']]);
    expect(calls[1].cwd).toBe(path.join(dir, '.'));
  });

  it('a failed restore or tool restore is the outcome', async () => {
    const dir = repo({ 'A/A.csproj': '' });
    const restoreFails = await nugetAdapter.relock!(ctxFor(dir, fakeExec(() => ({ code: 1, stdout: 'error NU1102: Unable to find package Newtonsoft.Json with version (>= 99.0.0)' })).exec), [nu('A/A.csproj')]);
    const toolFails = await nugetAdapter.relock!(ctxFor(dir, fakeExec((_c, args) => ({ code: args[0] === 'tool' ? 1 : 0 })).exec), [nu('.config/dotnet-tools.json', 'dotnet-ef')]);

    expect(restoreFails).toMatchObject({ ok: false });
    expect(toolFails).toMatchObject({ ok: false });
  });

  it('verify compares direct and central-transitive entries of every lockfile with the plan', async () => {
    const good = repo({ 'A/A.csproj': '', 'A/packages.lock.json': lock('13.0.3') });
    const bad = repo({ 'A/A.csproj': '', 'A/packages.lock.json': lock('13.0.1', 'CentralTransitive') });
    const transitive = repo({ 'A/A.csproj': '', 'A/packages.lock.json': lock('13.0.1', 'Transitive') });

    expect((await nugetAdapter.verify!(ctxFor(good), [nu('A/A.csproj')])).ok).toBe(true);
    expect((await nugetAdapter.verify!(ctxFor(bad), [nu('A/A.csproj')])).output).toContain('Newtonsoft.Json resolved 13.0.1 (planned 13.0.3)');
    expect((await nugetAdapter.verify!(ctxFor(transitive), [nu('A/A.csproj')])).ok).toBe(true);
  });

  it('consistency restores in locked mode only where a lockfile exists', async () => {
    const without = repo({ 'A/A.csproj': '' });
    const withLock = repo({ 'A/A.csproj': '', 'A/packages.lock.json': lock('13.0.3') });
    const { exec, calls } = fakeExec(() => ({ code: 0 }));

    expect((await nugetAdapter.consistency!(ctxFor(without, exec), 'A/A.csproj')).ok).toBe(true);
    expect(calls).toHaveLength(0);
    expect((await nugetAdapter.consistency!(ctxFor(withLock, exec), 'A/A.csproj')).ok).toBe(true);
    expect(calls[0].args).toEqual(['restore', 'A/A.csproj', '--locked-mode']);
  });

  it('reads blockers from project.assets.json first, deduplicated, and from the restore output otherwise', async () => {
    const assets = JSON.stringify({ logs: [
      { code: 'NU1605', level: 'Error', message: 'Detected package downgrade: Serilog from 4.0.0 to 3.1.0', libraryId: 'Serilog' },
      { code: 'NU1605', level: 'Error', message: 'Detected package downgrade: Serilog from 4.0.0 to 3.1.0', libraryId: 'Serilog' },
    ] });
    const withAssets = repo({ 'A/A.csproj': '', 'A/obj/project.assets.json': assets });
    const bare = repo({ 'A/A.csproj': '' });
    const text = 'A.csproj : error NU1102: Unable to find package Newtonsoft.Json with version (>= 99.0.0)';

    const structured = await nugetAdapter.parseBlockers(text, ctxFor(withAssets), [nu('A/A.csproj'), nu('A/A.csproj', 'Serilog')]);
    const fromText = await nugetAdapter.parseBlockers(text, ctxFor(bare), [nu('A/A.csproj')]);

    expect(structured).toEqual([expect.objectContaining({ depName: 'Serilog', code: 'NU1605' })]);
    expect(fromText).toEqual([expect.objectContaining({ depName: 'Newtonsoft.Json', code: 'NU1102' })]);
  });

  it('edits global.json msbuild-sdks', async () => {
    const dir = repo({ 'global.json': '{\n  "msbuild-sdks": {\n    "Microsoft.Build.Traversal": "13.0.1"\n  }\n}\n' });
    await nugetAdapter.edit(ctxFor(dir), nu('global.json', 'Microsoft.Build.Traversal'));
    expect(fs.readFileSync(path.join(dir, 'global.json'), 'utf8')).toContain('"Microsoft.Build.Traversal": "13.0.3"');
  });
});
