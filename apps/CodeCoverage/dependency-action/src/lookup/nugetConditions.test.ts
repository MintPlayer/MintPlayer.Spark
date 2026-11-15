import type { LookupRecord } from '../contracts/types';
import { directoryFiles } from '../util/repoFiles';
import { conditionTfms } from '../core/msbuildItems';
import { fixture, readJsonFixture, tempDir, writeTree } from '../testing/helpers';
import { mapReport, type RenovateReport } from './mapReport';
import { annotateConditions } from './nugetConditions';

const SAMPLE = 'renovate/nuget-conditions';

async function annotated() {
  const records = mapReport(readJsonFixture<RenovateReport>(`${SAMPLE}/report.json`));
  await annotateConditions(records, directoryFiles(fixture(`${SAMPLE}/input`)));
  return records;
}

describe('annotateConditions on the real S1 capture (multi-target-dependency-split-PRD D-a)', () => {
  it('gives each conditioned PackageReference its TFM, joined by (depName, currentValue) whatever Renovate\'s order', async () => {
    const split = (await annotated()).filter((r) => r.packageFile === 'Split/Split.csproj');
    const view = split.map((r) => `${r.depName}@${r.currentValue} ${r.targetFrameworks?.join(';') ?? '-'}`).sort();
    expect(view).toEqual([
      'Microsoft.Extensions.DependencyInjection@10.0.5 net10.0',
      'Microsoft.Extensions.DependencyInjection@10.0.5 net11.0',
      'Microsoft.Extensions.FileSystemGlobbing@10.0.5 net10.0',
      'Microsoft.Extensions.FileSystemGlobbing@11.0.0 net11.0',
      'Microsoft.Extensions.Options@10.0.5 net11.0',
    ]);
    expect(split.find((r) => r.depName === 'Microsoft.Extensions.FileSystemGlobbing' && r.currentValue === '11.0.0')!.condition).toBe("'$(TargetFramework)' == 'net11.0'");
  });

  it('maps a conditioned PackageVersion in Directory.Packages.props over the TFMs of the projects under it (S7)', async () => {
    const cpm = (await annotated()).filter((r) => r.packageFile === 'Cpm/Directory.Packages.props');
    expect(cpm.map((r) => `${r.currentValue} ${r.targetFrameworks?.join(';')}`).sort()).toEqual(['10.0.5 net10.0', '11.0.0 net11.0']);
  });

  it('leaves an unconditioned or versionless reference without the fields, so other records stay byte-equal', async () => {
    const versionless = (await annotated()).find((r) => r.packageFile === 'Cpm/Cpm.csproj')!;
    expect('condition' in versionless).toBe(false);
    expect('targetFrameworks' in versionless).toBe(false);
  });

  it('keeps a condition it cannot map, without targetFrameworks (D-f)', async () => {
    const root = tempDir();
    writeTree(root, {
      'Lib/Lib.csproj': [
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <PropertyGroup><TargetFrameworks>net10.0;net11.0</TargetFrameworks></PropertyGroup>',
        '  <ItemGroup Condition="\'$(TargetFramework)\' == \'net10.0\' Or \'$(Configuration)\' == \'Debug\'">',
        '    <PackageReference Include="A" Version="1.0.0" />',
        '  </ItemGroup>',
        '  <ItemGroup>',
        '    <PackageReference Include="B" Version="1.0.0" Condition="\'$(TargetFramework)\' != \'net10.0\'" />',
        '  </ItemGroup>',
        '</Project>',
      ].join('\n'),
    });
    const base = { ecosystem: 'nuget', manager: 'nuget', datasource: 'nuget', packageFile: 'Lib/Lib.csproj', lockFiles: [], packageName: null, depType: null, currentVersion: null,
      lockedVersion: null, currentDigest: null, replaceString: null, sharedVariableName: null, groupName: null, skipReason: null, hold: false, candidates: [] };
    const records: LookupRecord[] = [{ ...base, depName: 'A', currentValue: '1.0.0' }, { ...base, depName: 'B', currentValue: '1.0.0' }];
    await annotateConditions(records, directoryFiles(root));
    expect(records[0]).toMatchObject({ condition: "'$(TargetFramework)' == 'net10.0' Or '$(Configuration)' == 'Debug'" });
    expect(records[0].targetFrameworks).toBeUndefined();
    expect(records[1]).toMatchObject({ condition: "'$(TargetFramework)' != 'net10.0'", targetFrameworks: ['net11.0'] });
  });
});

describe('conditionTfms (S3)', () => {
  const project = ['netstandard2.0', 'net10.0', 'net11.0'];
  it.each([
    ["'$(TargetFramework)' == 'net10.0'", ['net10.0']],
    ["'$(TargetFramework)'=='NET11.0'", ['net11.0']],
    ["'net10.0' == '$(TargetFramework)'", ['net10.0']],
    ["'$(TargetFramework)' != 'net10.0'", ['netstandard2.0', 'net11.0']],
    ["$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net10.0'))", ['net10.0', 'net11.0']],
  ])('%s -> %j', (condition, expected) => {
    expect(conditionTfms(condition, project)).toEqual(expected);
  });

  it.each([
    "'$(TargetFramework)' == 'net10.0' Or '$(TargetFramework)' == 'net11.0'",
    "'$(TargetFrameworkIdentifier)' == '.NETCoreApp'",
    "$(TargetFramework.StartsWith('net1'))",
    "$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'netstandard2.0'))",
  ])('%s maps to nothing', (condition) => {
    expect(conditionTfms(condition, project)).toBeNull();
  });
});
