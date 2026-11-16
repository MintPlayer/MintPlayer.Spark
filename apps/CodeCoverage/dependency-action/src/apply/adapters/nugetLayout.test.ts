import * as fs from 'node:fs';
import * as path from 'node:path';
import type { PlanItem, TfmVersion } from '../../contracts/types';
import { EditRefused, parseText, serializeText } from '../../core/lineEdit';
import { scanLocations } from '../../platform/locations';
import { applyPlatform } from '../../platform/applyPlatform';
import { directoryFiles } from '../../util/repoFiles';
import { ctxFor, item, tempDir, writeTree } from '../../testing/helpers';
import { nugetAdapter } from './nuget';
import { applyLayout } from './nugetLayout';

/** multi-target-dependency-split-PRD P4: the split, an added and a dropped TFM, per-TFM patches, as one edit (D-c, D-d). */

const FSG = 'Microsoft.Extensions.FileSystemGlobbing';
const plan = (layout: TfmVersion[], depName = FSG, packageFile = 'Lib/Lib.csproj'): PlanItem =>
  item({ depName, datasource: 'nuget', manager: 'nuget', packageFile, currentValue: '10.0.5', newValue: layout.at(-1)!.version, newVersion: layout.at(-1)!.version, layout });

function rewrite(text: string, it: PlanItem): string {
  const doc = parseText(Buffer.from(text), 'x');
  applyLayout(doc, 'x', it);
  return serializeText(doc).toString('utf8');
}

const unconditioned = [
  '<Project Sdk="Microsoft.NET.Sdk">',
  '  <PropertyGroup>',
  '    <TargetFrameworks>net10.0;net11.0</TargetFrameworks>',
  '  </PropertyGroup>',
  '',
  '  <ItemGroup>',
  `    <PackageReference Include="${FSG}" Version="10.0.5" />`,
  '    <PackageReference Include="Other" Version="1.0.0" />',
  '  </ItemGroup>',
  '</Project>',
  '',
].join('\r\n');

const split = [
  '<Project Sdk="Microsoft.NET.Sdk">',
  '  <PropertyGroup>',
  '    <TargetFrameworks>net10.0;net11.0</TargetFrameworks>',
  '  </PropertyGroup>',
  '',
  '  <ItemGroup>',
  '    <PackageReference Include="Other" Version="1.0.0" />',
  '  </ItemGroup>',
  `  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">`,
  `    <PackageReference Include="${FSG}" Version="10.0.15" />`,
  '  </ItemGroup>',
  `  <ItemGroup Condition="'$(TargetFramework)' == 'net11.0'">`,
  `    <PackageReference Include="${FSG}" Version="11.0.0" />`,
  '  </ItemGroup>',
  '</Project>',
  '',
].join('\r\n');

describe('applyLayout', () => {
  const both = [{ targetFramework: 'net10.0', version: '10.0.15' }, { targetFramework: 'net11.0', version: '11.0.0' }];

  it('splits an unconditioned reference into one canonical ItemGroup per TFM, after its own group, CRLF kept', () => {
    expect(rewrite(unconditioned, plan(both))).toBe(split);
  });

  it('removes an ItemGroup the reference leaves empty', () => {
    const alone = unconditioned.replace('    <PackageReference Include="Other" Version="1.0.0" />\r\n', '');
    const out = rewrite(alone, plan(both));
    expect(out).not.toContain('<ItemGroup>\r\n');
    expect(out).toContain(`  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">\r\n    <PackageReference Include="${FSG}" Version="10.0.15" />\r\n  </ItemGroup>\r\n`);
    expect(out.indexOf('</PropertyGroup>')).toBeLessThan(out.indexOf("'net10.0'\">"));
  });

  it('moves existing canonical groups in place, adds a new TFM and drops a gone one (D-d)', () => {
    const next = rewrite(split, plan([{ targetFramework: 'net11.0', version: '11.0.1' }, { targetFramework: 'net12.0', version: '12.0.0' }]));
    expect(next).not.toContain("'net10.0'");
    expect(next).toContain(`  <ItemGroup Condition="'$(TargetFramework)' == 'net11.0'">\r\n    <PackageReference Include="${FSG}" Version="11.0.1" />\r\n  </ItemGroup>\r\n`);
    expect(next).toContain(`  <ItemGroup Condition="'$(TargetFramework)' == 'net12.0'">\r\n    <PackageReference Include="${FSG}" Version="12.0.0" />\r\n  </ItemGroup>\r\n</Project>`);
    expect(next).toContain('<PackageReference Include="Other" Version="1.0.0" />');
  });

  it('shares the canonical groups between packages, and keeps attributes and children', () => {
    const text = unconditioned.replace('<PackageReference Include="Other" Version="1.0.0" />', '<PackageReference Include="Other" Version="1.0.0">\r\n      <PrivateAssets>all</PrivateAssets>\r\n    </PackageReference>');
    const once = rewrite(text, plan(both));
    const twice = rewrite(once, plan([{ targetFramework: 'net10.0', version: '1.0.1' }, { targetFramework: 'net11.0', version: '2.0.0' }], 'Other'));
    expect(twice.match(/<ItemGroup Condition/g)).toHaveLength(2);
    expect(twice).toContain(`'net11.0'">\r\n    <PackageReference Include="${FSG}" Version="11.0.0" />\r\n    <PackageReference Include="Other" Version="2.0.0">\r\n      <PrivateAssets>all</PrivateAssets>\r\n    </PackageReference>\r\n  </ItemGroup>`);
  });

  it('rewrites a PackageVersion in Directory.Packages.props the same way (P6, S7)', () => {
    const props = '<Project>\n  <ItemGroup>\n    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.5" />\n  </ItemGroup>\n</Project>\n';
    const out = rewrite(props, plan(both, 'Microsoft.Extensions.Hosting', 'Directory.Packages.props'));
    expect(out).toBe(
      "<Project>\n  <ItemGroup Condition=\"'$(TargetFramework)' == 'net10.0'\">\n    <PackageVersion Include=\"Microsoft.Extensions.Hosting\" Version=\"10.0.15\" />\n  </ItemGroup>\n" +
        "  <ItemGroup Condition=\"'$(TargetFramework)' == 'net11.0'\">\n    <PackageVersion Include=\"Microsoft.Extensions.Hosting\" Version=\"11.0.0\" />\n  </ItemGroup>\n</Project>\n",
    );
  });

  it.each([
    ['a condition the bot does not write', `<ItemGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net11.0'))">`, '<ItemGroup>'],
    ['a Condition of its own', 'Version="10.0.5" />', `Version="10.0.5" Condition="'$(TargetFramework)' == 'net10.0'" />`],
    ['a property version', 'Version="10.0.5"', 'Version="$(GlobbingVersion)"'],
  ])('refuses %s', (_, to, from) => {
    const text = from === '<ItemGroup>' ? unconditioned.replace('  <ItemGroup>', `  ${to}`) : unconditioned.replace(to, from);
    expect(() => rewrite(text, plan(both))).toThrow(EditRefused);
  });
});

describe('nuget adapter with a layout', () => {
  it('edits through the layout and verifies each TFM against its own planned version', async () => {
    const root = tempDir('dep-layout-');
    writeTree(root, { 'Lib/Lib.csproj': unconditioned });
    const it2 = plan([{ targetFramework: 'net10.0', version: '10.0.15' }, { targetFramework: 'net11.0', version: '11.0.0' }]);
    const ctx = ctxFor(root);
    await nugetAdapter.edit(ctx, it2);
    expect(fs.readFileSync(path.join(root, 'Lib/Lib.csproj'), 'utf8')).toBe(split);
    const lock = (net10: string) => JSON.stringify({ version: 1, dependencies: { 'net10.0': { [FSG]: { type: 'Direct', resolved: net10 } }, 'net11.0': { [FSG]: { type: 'Direct', resolved: '11.0.0' } } } });
    writeTree(root, { 'Lib/packages.lock.json': lock('10.0.15') });
    expect((await nugetAdapter.verify!(ctx, [it2])).ok).toBe(true);
    writeTree(root, { 'Lib/packages.lock.json': lock('11.0.0') });
    const miss = await nugetAdapter.verify!(ctx, [it2]);
    expect(miss.ok).toBe(false);
    expect(miss.output).toContain('resolved 11.0.0 (planned 10.0.15)');
  });
});

describe('the TFM list and the canonical conditions (P3/P4, §7.0)', () => {
  const dotnet = { sdk: 'dotnet' as const, fromChannel: '11.0', toChannel: '12.0', toVersion: '12.0.0', sdkVersion: '12.0.100' };

  it('writes the bot\'s TFM list for a multi-target project, and moves the rest by strategy', async () => {
    const root = tempDir();
    writeTree(root, {
      'Lib/Lib.csproj': split.replace('net10.0;net11.0', 'net8.0;net10.0;net11.0'),
      'App/App.csproj': '<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>\n',
    });
    const result = await applyPlatform(directoryFiles(root), { ...dotnet, targetFrameworks: [{ file: 'Lib/Lib.csproj', value: 'net10.0;net11.0;net12.0' }] });
    expect(result.edited).toEqual(['App/App.csproj', 'Lib/Lib.csproj']);
    expect(fs.readFileSync(path.join(root, 'Lib/Lib.csproj'), 'utf8')).toContain('<TargetFrameworks>net10.0;net11.0;net12.0</TargetFrameworks>');
    expect(fs.readFileSync(path.join(root, 'App/App.csproj'), 'utf8')).toContain('<TargetFramework>net12.0</TargetFramework>');
    expect(result.reported).toEqual([]);
  });

  it('drops an end-of-life TFM inside the channel too (M6)', async () => {
    const root = tempDir();
    writeTree(root, { 'Lib/Lib.csproj': '<Project><PropertyGroup><TargetFrameworks>net8.0;net11.0</TargetFrameworks></PropertyGroup></Project>\n' });
    const result = await applyPlatform(directoryFiles(root), { ...dotnet, toChannel: '11.0', toVersion: '11.0.1', targetFrameworks: [{ file: 'Lib/Lib.csproj', value: 'net11.0' }] });
    expect(result.edited).toEqual(['Lib/Lib.csproj']);
  });

  it('no longer reports a multi-target project\'s canonical ItemGroups as computed TFMs; a single-target one still is', async () => {
    const root = tempDir();
    writeTree(root, {
      'Lib/Lib.csproj': split,
      'One/One.csproj': `<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>\n<ItemGroup Condition="'$(TargetFramework)' == 'net11.0'">\n</ItemGroup></Project>\n`,
    });
    const computed = (await scanLocations(directoryFiles(root), ['dotnet'])).filter((f) => f.computed).map((f) => f.file);
    expect(computed).toEqual(['One/One.csproj']);
  });
});
