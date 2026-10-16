import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { isValid } from '../contracts/validate';
import { item, tempDir } from '../testing/helpers';
import { nugetFeed, npmRegistry, type NpmRegistry } from './registries';
import { applyOnRepo, enabled, must } from './setup';

/**
 * node+dotnet slice: real npm / pnpm / dotnet against LOCAL registries (an in-process npm registry, a
 * NuGet folder feed with <clear/>). Golden: the manifest diff is the planned token only, the lockfile
 * holds exactly the planned version, and package scripts / build targets never run (S14 as regression).
 */

const npmPkg = (name: string, version: string, extra: Partial<{ peerDependencies: Record<string, string>; scripts: Record<string, string>; dependencies: Record<string, string> }> = {}) => ({ name, version, ...extra });

describe.skipIf(!enabled('node+dotnet', 'npm'))('npm against a local registry', () => {
  let reg: NpmRegistry;
  const sentinel = path.join(tempDir(), 'SENTINEL');
  beforeAll(async () => {
    reg = await npmRegistry([
      npmPkg('lib-a', '1.0.0'), npmPkg('lib-a', '1.1.0'), npmPkg('lib-a', '1.2.0'),
      npmPkg('host', '1.0.0', { peerDependencies: { 'peer-x': '^1.0.0' } }),
      npmPkg('peer-x', '1.0.0'), npmPkg('peer-x', '2.0.0'),
      npmPkg('lib-c', '1.0.0'),
      // S14 regression: a preinstall that writes a sentinel must never run.
      npmPkg('lib-c', '1.0.1', { scripts: { preinstall: `node -e "require('fs').writeFileSync('${sentinel.replace(/\\/g, '/')}','ran')"` } }),
    ]);
  });
  afterAll(() => reg.close());

  it('3 groups, the middle one conflicting: groups 1 and 3 ship, pinned exactly, scripts never ran', async () => {
    const env = { npm_config_registry: reg.url, npm_config_audit: 'false', npm_config_fund: 'false', npm_config_cache: tempDir() };
    const pkg = `{\n\t"name": "it",\n\t"version": "1.0.0",\n\t"dependencies": {\n\t\t"lib-a": "^1.0.0",\n\t\t"host": "1.0.0",\n\t\t"peer-x": "1.0.0",\n\t\t"lib-c": "1.0.0"\n\t}\n}\n`;
    const npmItem = (depName: string, cur: string, next: string, ver: string) =>
      item({ depName, datasource: 'npm', manager: 'npm', depType: 'dependencies', packageFile: 'package.json', lockFiles: ['package-lock.json'], currentValue: cur, newValue: next, newVersion: ver });
    const run = await applyOnRepo(
      { 'package.json': pkg, '.npmrc': `registry=${reg.url}\n`, '.gitignore': 'node_modules\n' },
      [
        // Range with an old lock: a plain relock would jump to 1.2.0 (the S13 latest-satisfying trap).
        { group: 'lib-a', items: [npmItem('lib-a', '^1.0.0', '^1.1.0', '1.1.0')] },
        { group: 'peer-x', items: [npmItem('peer-x', '1.0.0', '2.0.0', '2.0.0')] },
        { group: 'lib-c', items: [npmItem('lib-c', '1.0.0', '1.0.1', '1.0.1')] },
      ],
      { env, prepare: (dir) => must('npm', ['install', '--package-lock-only', '--ignore-scripts', 'lib-a@1.0.0', '--save-exact=false'], dir, env).then(() => fs.writeFileSync(path.join(dir, 'package.json'), pkg)).then(() => must('npm', ['install', '--package-lock-only', '--ignore-scripts'], dir, env)).then(() => undefined) },
    );
    expect(isValid('result', run.result).errors).toBe('');
    expect(run.result.groups.map((g) => g.status)).toEqual(['applied', 'unresolvable', 'applied']);
    expect(run.result.groups[1].blockers[0]).toMatchObject({ depName: 'host', code: 'ERESOLVE' });
    const lock = JSON.parse(run.read('package-lock.json'));
    expect(lock.packages['node_modules/lib-a'].version).toBe('1.1.0');
    expect(lock.packages['node_modules/peer-x'].version).toBe('1.0.0');
    expect(lock.packages['node_modules/lib-c'].version).toBe('1.0.1');
    expect(run.read('package.json')).toBe(pkg.replace('"lib-a": "^1.0.0"', '"lib-a": "^1.1.0"').replace('"lib-c": "1.0.0"', '"lib-c": "1.0.1"'));
    expect(fs.existsSync(path.join(run.dir, 'node_modules'))).toBe(false);
    expect(fs.existsSync(sentinel)).toBe(false);
    expect(run.result.consistency.status).toBe('passed');
  });
});

describe.skipIf(!enabled('node+dotnet', 'pnpm'))('pnpm against a local registry', () => {
  let reg: NpmRegistry;
  beforeAll(async () => {
    reg = await npmRegistry([npmPkg('lib-a', '1.0.0'), npmPkg('lib-a', '1.1.0'), npmPkg('lib-a', '1.2.0')]);
  });
  afterAll(() => reg.close());

  it('pins a range exactly with `pnpm update name@ver --lockfile-only` (S13)', async () => {
    const env = { npm_config_registry: reg.url, npm_config_store_dir: tempDir() };
    const pkg = '{\n  "name": "it",\n  "dependencies": {\n    "lib-a": "^1.0.0"\n  }\n}\n';
    const run = await applyOnRepo(
      { 'package.json': pkg, '.npmrc': `registry=${reg.url}\n` },
      [{ group: 'lib-a', items: [item({ depName: 'lib-a', datasource: 'npm', manager: 'npm', depType: 'dependencies', packageFile: 'package.json', lockFiles: ['pnpm-lock.yaml'], currentValue: '^1.0.0', newValue: '^1.1.0', newVersion: '1.1.0' })] }],
      { env, prepare: (dir) => must('pnpm', ['update', 'lib-a@1.0.0', '--lockfile-only', '--ignore-scripts'], dir, env).then(() => fs.writeFileSync(path.join(dir, 'package.json'), pkg)).then(() => undefined) },
    );
    expect(run.result.groups[0].status).toBe('applied');
    expect(run.read('pnpm-lock.yaml')).toMatch(/lib-a@1\.1\.0/);
    expect(run.read('pnpm-lock.yaml')).not.toMatch(/lib-a@1\.2\.0/);
  });
});

/** The SDK's own TFM: an older one would need its targeting pack from the (cleared) feed. */
const TFM = (() => {
  try {
    return `net${execFileSync('dotnet', ['--version']).toString().trim().split('.')[0]}.0`;
  } catch {
    return 'net10.0';
  }
})();

describe.skipIf(!enabled('node+dotnet', 'dotnet'))('NuGet against a local folder feed', () => {
  const feed = tempDir();
  const sentinel = path.join(tempDir(), 'NUGET-SENTINEL');
  beforeAll(() => {
    nugetFeed(feed, [
      { id: 'Lib.A', version: '1.0.0' }, { id: 'Lib.A', version: '1.1.0' },
      { id: 'Lib.Parent', version: '1.0.0', dependencies: [{ id: 'Lib.A', range: '1.0.0' }] },
      { id: 'Lib.Parent', version: '2.0.0', dependencies: [{ id: 'Lib.A', range: '2.0.0' }] },
      { id: 'Lib.A', version: '2.0.0' },
      {
        id: 'Lib.Targets', version: '1.0.0',
        files: { 'build/Lib.Targets.targets': `<Project><Target Name="Sentinel" BeforeTargets="Restore;_GenerateRestoreGraph;Build"><WriteLinesToFile File="${sentinel}" Lines="ran" /></Target></Project>` },
      },
      { id: 'Lib.Targets', version: '1.0.1', files: { 'build/Lib.Targets.targets': `<Project><Target Name="Sentinel" BeforeTargets="Restore;_GenerateRestoreGraph;Build"><WriteLinesToFile File="${sentinel}" Lines="ran" /></Target></Project>` } },
    ]);
  });

  const files = (): Record<string, string> => ({
    'NuGet.config': `<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key="local" value="${feed}" />\n  </packageSources>\n</configuration>\n`,
    'Directory.Packages.props': '<Project>\r\n  <PropertyGroup>\r\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\r\n  </PropertyGroup>\r\n  <ItemGroup>\r\n    <PackageVersion Include="Lib.A" Version="1.0.0" />\r\n    <PackageVersion Include="Lib.Parent" Version="1.0.0" />\r\n    <PackageVersion Include="Lib.Targets" Version="1.0.0" />\r\n  </ItemGroup>\r\n</Project>\r\n',
    'App/App.csproj': `<Project Sdk="Microsoft.NET.Sdk">\r\n  <PropertyGroup>\r\n    <TargetFramework>${TFM}</TargetFramework>\r\n    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>\r\n  </PropertyGroup>\r\n  <ItemGroup>\r\n    <PackageReference Include="Lib.A" />\r\n    <PackageReference Include="Lib.Parent" />\r\n    <PackageReference Include="Lib.Targets" />\r\n  </ItemGroup>\r\n</Project>\r\n`,
    '.gitignore': 'bin/\nobj/\n',
  });
  const cpm = (depName: string, cur: string, next: string) =>
    item({ depName, datasource: 'nuget', manager: 'nuget', packageFile: 'Directory.Packages.props', currentValue: cur, newValue: next });
  const env = { NUGET_PACKAGES: tempDir(), DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };

  it('CPM text edit + restore: pure props diff, packages.lock.json pinned, build/*.targets never ran (D-S14a)', async () => {
    const run = await applyOnRepo(files(), [
      { group: 'Lib.A', items: [cpm('Lib.A', '1.0.0', '1.1.0')] },
      { group: 'Lib.Targets', items: [cpm('Lib.Targets', '1.0.0', '1.0.1')] },
    ], { env, prepare: (dir) => must('dotnet', ['restore', 'App/App.csproj'], dir, env).then(() => undefined) });
    expect(run.result.groups.map((g) => g.status)).toEqual(['applied', 'applied']);
    expect(run.read('Directory.Packages.props')).toBe(files()['Directory.Packages.props'].replace('"Lib.A" Version="1.0.0"', '"Lib.A" Version="1.1.0"').replace('"Lib.Targets" Version="1.0.0"', '"Lib.Targets" Version="1.0.1"'));
    expect(JSON.parse(run.read('App/packages.lock.json')).dependencies[TFM]['Lib.A'].resolved).toBe('1.1.0');
    expect(fs.existsSync(sentinel)).toBe(false);
  });

  it('a downgrade (NU1605) is unresolvable with a structured blocker, and the group is reset', async () => {
    const run = await applyOnRepo(files(), [{ group: 'Lib.Parent', items: [cpm('Lib.Parent', '1.0.0', '2.0.0')] }], {
      env, prepare: (dir) => must('dotnet', ['restore', 'App/App.csproj'], dir, env).then(() => undefined),
    });
    expect(run.result.groups[0].status).toBe('unresolvable');
    expect(run.result.groups[0].blockers[0]).toMatchObject({ depName: 'Lib.A', code: 'NU1605' });
    expect(run.patch).toBe('');
  });
});
