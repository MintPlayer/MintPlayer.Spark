import * as fs from 'node:fs';
import * as path from 'node:path';
import type { PlanItem } from '../../contracts/types';
import { EditRefused } from '../../core/lineEdit';
import { changedPairs, ctxFor, fakeExec, gitRepo, item, tempDir, writeTree } from '../../testing/helpers';
import { applyGroup } from '../orchestrator';
import { adapterFor } from '../registry';

/**
 * docs/refused_property_and_packagemanager_edits_PRD.md: the two updates every SpaServices dependency PR (#121-#123) listed
 * under "Not applied". The records are the real lookup's (run 204431316-104-1, GitHub run 43467453083); the files are
 * SpaServices master's lines.
 */

const CSPROJ = 'MintPlayer.AspNetCore.NodeServices/MintPlayer.AspNetCore.NodeServices.csproj';
const csproj = (version: string) =>
  '<Project Sdk="Microsoft.NET.Sdk">\r\n  <ItemGroup>\r\n' +
  `    <PackageReference Include="MintPlayer.FolderHasher.Targets" Version="${version}" PrivateAssets="all" />\r\n` +
  '  </ItemGroup>\r\n</Project>\r\n';
const buildProps = (value: string) =>
  '<Project>\r\n  <PropertyGroup>\r\n    <LangVersion>preview</LangVersion>\r\n' +
  `    <FolderHasherTargetsVersion>${value}</FolderHasherTargetsVersion>\r\n  </PropertyGroup>\r\n</Project>\r\n`;

const folderHasher = (partial: Partial<PlanItem> = {}): PlanItem =>
  item({
    depName: 'MintPlayer.FolderHasher.Targets', datasource: 'nuget', manager: 'nuget', packageFile: CSPROJ,
    currentValue: '11.0.0-rc.1', newValue: '11.0.0-rc.2', sharedVariableName: 'FolderHasherTargetsVersion', ...partial,
  });

async function edit(root: string, i: PlanItem): Promise<void> {
  const a = adapterFor(i)!;
  await a.edit(ctxFor(root, undefined, [i]), i);
}

const read = (root: string, f: string) => fs.readFileSync(path.join(root, f), 'utf8');

describe('D1: a NuGet version held in an MSBuild property edits the property', () => {
  it('SpaServices: the property in the root Directory.Build.props moves, the csproj stays', async () => {
    const root = tempDir();
    writeTree(root, { [CSPROJ]: csproj('$(FolderHasherTargetsVersion)'), 'Directory.Build.props': buildProps('11.0.0-rc.1') });
    await edit(root, folderHasher());
    expect(changedPairs(buildProps('11.0.0-rc.1'), read(root, 'Directory.Build.props'))).toEqual([
      { minus: '    <FolderHasherTargetsVersion>11.0.0-rc.1</FolderHasherTargetsVersion>', plus: '    <FolderHasherTargetsVersion>11.0.0-rc.2</FolderHasherTargetsVersion>' },
    ]);
    expect(read(root, 'Directory.Build.props')).toBe(buildProps('11.0.0-rc.2'));
    expect(read(root, CSPROJ)).toBe(csproj('$(FolderHasherTargetsVersion)'));
  });

  it('a property defined in the project itself, with a Condition, moves there', async () => {
    const root = tempDir();
    const own = csproj('$(FolderHasherTargetsVersion)').replace('<ItemGroup>',
      '<PropertyGroup>\r\n    <FolderHasherTargetsVersion Condition="\'$(X)\' == \'\'">11.0.0-rc.1</FolderHasherTargetsVersion>\r\n  </PropertyGroup>\r\n  <ItemGroup>');
    writeTree(root, { [CSPROJ]: own });
    await edit(root, folderHasher());
    expect(read(root, CSPROJ)).toBe(own.replace('>11.0.0-rc.1<', '>11.0.0-rc.2<'));
  });

  it('a Directory.Packages.props in the project directory holds it', async () => {
    const root = tempDir();
    writeTree(root, {
      [CSPROJ]: csproj('$(FolderHasherTargetsVersion)'),
      'MintPlayer.AspNetCore.NodeServices/Directory.Packages.props': buildProps('11.0.0-rc.1'),
    });
    await edit(root, folderHasher());
    expect(read(root, 'MintPlayer.AspNetCore.NodeServices/Directory.Packages.props')).toBe(buildProps('11.0.0-rc.2'));
  });

  it('a Version attribute with the literal still wins (no property lookup)', async () => {
    const root = tempDir();
    writeTree(root, { [CSPROJ]: csproj('11.0.0-rc.1'), 'Directory.Build.props': buildProps('11.0.0-rc.1') });
    await edit(root, folderHasher());
    expect(read(root, CSPROJ)).toBe(csproj('11.0.0-rc.2'));
    expect(read(root, 'Directory.Build.props')).toBe(buildProps('11.0.0-rc.1'));
  });

  it.each([
    ['no definition anywhere', { [CSPROJ]: csproj('$(FolderHasherTargetsVersion)') }, /no MintPlayer\.FolderHasher\.Targets version 11\.0\.0-rc\.1/],
    ['the definition holds another value', { [CSPROJ]: csproj('$(FolderHasherTargetsVersion)'), 'Directory.Build.props': buildProps('10.0.0') }, /no MintPlayer\.FolderHasher\.Targets version/],
    ['two definitions (an override in the project)', {
      [CSPROJ]: csproj('$(FolderHasherTargetsVersion)').replace('<ItemGroup>', '<PropertyGroup><FolderHasherTargetsVersion>11.0.0-rc.1</FolderHasherTargetsVersion></PropertyGroup>\r\n  <ItemGroup>'),
      'Directory.Build.props': buildProps('11.0.0-rc.1'),
    }, /ambiguous/],
    ['a definition only in a sibling directory', { [CSPROJ]: csproj('$(FolderHasherTargetsVersion)'), 'Other/Directory.Build.props': buildProps('11.0.0-rc.1') }, /no MintPlayer/],
  ])('refuses: %s', async (_n, files, message) => {
    const root = tempDir();
    writeTree(root, files);
    const before = Object.fromEntries(Object.keys(files).map((f) => [f, read(root, f)]));
    await expect(edit(root, folderHasher())).rejects.toThrow(EditRefused);
    await expect(edit(root, folderHasher())).rejects.toThrow(message);
    for (const [f, text] of Object.entries(before)) expect(read(root, f)).toBe(text);
  });

  it('a record without sharedVariableName never edits a props file it does not name', async () => {
    const root = tempDir();
    writeTree(root, { [CSPROJ]: csproj('$(FolderHasherTargetsVersion)'), 'Directory.Build.props': buildProps('11.0.0-rc.1') });
    await expect(edit(root, folderHasher({ sharedVariableName: null }))).rejects.toThrow(EditRefused);
    expect(read(root, 'Directory.Build.props')).toBe(buildProps('11.0.0-rc.1'));
  });

  it('D2: files() lists the property files, so the snapshot covers them', async () => {
    const root = tempDir();
    writeTree(root, {
      [CSPROJ]: csproj('$(FolderHasherTargetsVersion)'),
      'Directory.Build.props': buildProps('11.0.0-rc.1'),
      'MintPlayer.AspNetCore.NodeServices/Directory.Build.targets': '<Project />\n',
      'Other/Directory.Build.props': '<Project />\n',
    });
    const i = folderHasher();
    const files = await adapterFor(i)!.files(ctxFor(root, undefined, [i]), i);
    expect(files).toEqual(expect.arrayContaining([CSPROJ, 'Directory.Build.props', 'MintPlayer.AspNetCore.NodeServices/Directory.Build.targets']));
    expect(files).not.toContain('Other/Directory.Build.props');
  });
});

const XSRF = 'Demo/Xsrf/ClientApp/package.json';
const packageJson = (pm: string) =>
  `{\n  "name": "xsrf",\n  "private": true,\n  "dependencies": {\n    "npm-run-all": "4.1.5"\n  },\n  "packageManager": "${pm}"\n}\n`;
const npmRecord = (partial: Partial<PlanItem> = {}): PlanItem =>
  item({
    depName: 'npm', datasource: 'npm', manager: 'npm', depType: 'packageManager', packageFile: XSRF,
    lockFiles: ['Demo/Xsrf/ClientApp/package-lock.json'], currentValue: '10.9.2', newValue: '10.9.3', ...partial,
  });

describe('D4/D5: a packageManager pin edits the field, and runs no package manager', () => {
  it('SpaServices: "packageManager": "npm@10.9.2" moves to 10.9.3, nothing else', async () => {
    const root = tempDir();
    writeTree(root, { [XSRF]: packageJson('npm@10.9.2') });
    const i = npmRecord();
    expect(adapterFor(i)!.id).toBe('package-manager');
    await edit(root, i);
    expect(read(root, XSRF)).toBe(packageJson('npm@10.9.3'));
  });

  it('pnpm and yarn pins (whatever the lockfile) take the same adapter', async () => {
    const root = tempDir();
    writeTree(root, { 'package.json': packageJson('pnpm@9.1.0') });
    const i = npmRecord({ depName: 'pnpm', packageFile: 'package.json', lockFiles: ['pnpm-lock.yaml'], currentValue: '9.1.0', newValue: '9.15.0' });
    expect(adapterFor(i)!.id).toBe('package-manager');
    expect(adapterFor({ ...i, depName: 'yarn', lockFiles: ['yarn.lock'] })!.id).toBe('package-manager');
    await edit(root, i);
    expect(read(root, 'package.json')).toBe(packageJson('pnpm@9.15.0'));
  });

  it.each([
    ['an integrity hash it cannot recompute', packageJson('pnpm@9.1.0+sha512.0123abcd'), 'pnpm', '9.1.0', /integrity hash/],
    ['another manager in the field', packageJson('yarn@10.9.2'), 'npm', '10.9.2', /no line matches/],
    ['another version in the field', packageJson('npm@10.9.1'), 'npm', '10.9.2', /no line matches/],
    ['a dependency named like the manager (not the field)', packageJson('npm@1.0.0').replace('"npm-run-all": "4.1.5"', '"npm": "10.9.2"'), 'npm', '10.9.2', /no line matches/],
  ])('refuses: %s', async (_n, text, depName, cur, message) => {
    const root = tempDir();
    writeTree(root, { 'package.json': text });
    await expect(edit(root, npmRecord({ depName, packageFile: 'package.json', currentValue: cur }))).rejects.toThrow(message);
    expect(read(root, 'package.json')).toBe(text);
  });

  it('the group applies with only package.json changed and no tool run (npm would add npm to the lockfile)', async () => {
    const lock = '{\n  "name": "xsrf",\n  "lockfileVersion": 3,\n  "packages": {}\n}\n';
    const { dir, sha } = gitRepo({ [XSRF]: packageJson('npm@10.9.2'), 'Demo/Xsrf/ClientApp/package-lock.json': lock });
    const { exec, calls } = fakeExec(() => ({ code: 1, stderr: 'no tool may run for a packageManager pin' }));
    const result = await applyGroup({ root: dir, exec, feedSecrets: false, consistencyCheck: true, toolAvailable: async () => false }, { group: 'npm', items: [npmRecord()] });
    expect(result.message).toBeNull();
    expect(result.status).toBe('applied');
    expect(result.files).toEqual([XSRF]);
    expect(calls.filter((c) => c.cmd !== 'git')).toEqual([]);
    expect(sha).toMatch(/^[0-9a-f]{40}$/);
    expect(read(dir, 'Demo/Xsrf/ClientApp/package-lock.json')).toBe(lock);
  });
});
