import * as fs from 'node:fs';
import * as path from 'node:path';
import type { PlanItem } from '../../contracts/types';
import { EditRefused } from '../../core/lineEdit';
import { changedPairs, copyTree, ctxFor, diffPairs, fixture, item, onlyLinesChanged, readFixture, tempDir } from '../../testing/helpers';
import type { Adapter } from '../adapter';
import { adapterFor } from '../registry';

/**
 * R13 criterion 1 / D-S13c: the manifest change our pure edit makes is EXACTLY the manifest change of
 * the real S13 capture (the `-`/`+` pairs of that file), and every other byte (EOLs, BOM, final
 * newline, comments, odd spacing) is untouched. Base manifests: test/fixtures/apply/<eco>/base.
 */
interface Case {
  name: string;
  eco: string;
  diff: string;
  file: string;
  items: PlanItem[];
  adapter: string;
}

const npmItem = (depName: string, cur: string, next: string, lock: string, manager = 'npm'): PlanItem =>
  item({ depName, datasource: 'npm', manager, depType: 'dependencies', packageFile: 'package.json', lockFiles: [lock], currentValue: cur, newValue: next });

const CASES: Case[] = [
  { name: 'npm range (hybrid pin)', eco: 'npm', diff: 'hybrid-textedit-pin-lock', file: 'package.json', adapter: 'npm', items: [npmItem('express', '^4.18.2', '^4.19.2', 'package-lock.json')] },
  { name: 'npm exact', eco: 'npm', diff: 'textedit-lockonly-exact', file: 'package.json', adapter: 'npm', items: [npmItem('chalk', '4.1.0', '4.1.2', 'package-lock.json')] },
  { name: 'pnpm', eco: 'pnpm', diff: 'textedit-plus-update', file: 'package.json', adapter: 'pnpm', items: [npmItem('express', '^4.18.2', '^4.19.2', 'pnpm-lock.yaml', 'npm')] },
  { name: 'yarn berry', eco: 'yarn', diff: 'textedit-set-resolution', file: 'package.json', adapter: 'yarn', items: [npmItem('express', '^4.18.2', '^4.19.2', 'yarn.lock', 'npm')] },
  {
    name: 'NuGet csproj attribute (CRLF)', eco: 'nuget', diff: 'csproj-textedit-restore', file: 'App.csproj', adapter: 'nuget',
    items: [item({ depName: 'Newtonsoft.Json', datasource: 'nuget', manager: 'nuget', packageFile: 'App.csproj', currentValue: '13.0.1', newValue: '13.0.3' })],
  },
  {
    name: 'NuGet CPM Directory.Packages.props', eco: 'nuget', diff: 'cpm-textedit-restore', file: 'Directory.Packages.props', adapter: 'nuget',
    items: [item({ depName: 'Newtonsoft.Json', datasource: 'nuget', manager: 'nuget', packageFile: 'Directory.Packages.props', currentValue: '13.0.1', newValue: '13.0.3' })],
  },
  {
    name: 'dotnet-tools.json', eco: 'nuget', diff: 'tools-textedit', file: '.config/dotnet-tools.json', adapter: 'nuget',
    items: [item({ depName: 'dotnet-ef', datasource: 'nuget', manager: 'nuget', packageFile: '.config/dotnet-tools.json', currentValue: '8.0.0', newValue: '8.0.1' })],
  },
  {
    name: 'pip requirements', eco: 'pip', diff: 'requirements-textedit', file: 'requirements.txt', adapter: 'pip',
    items: [item({ depName: 'requests', datasource: 'pypi', manager: 'pip_requirements', packageFile: 'requirements.txt', currentValue: '==2.28.2', newValue: '==2.31.0', newVersion: '2.31.0' })],
  },
  {
    name: 'uv pyproject', eco: 'uv', diff: 'textedit-lock', file: 'pyproject.toml', adapter: 'uv',
    items: [item({ depName: 'requests', datasource: 'pypi', manager: 'pep621', packageFile: 'pyproject.toml', lockFiles: ['uv.lock'], currentValue: '==2.28.2', newValue: '==2.31.0', newVersion: '2.31.0' })],
  },
  {
    name: 'Poetry', eco: 'poetry', diff: 'textedit-lock', file: 'pyproject.toml', adapter: 'poetry',
    items: [item({ depName: 'requests', datasource: 'pypi', manager: 'poetry', packageFile: 'pyproject.toml', lockFiles: ['poetry.lock'], currentValue: '2.28.2', newValue: '2.31.0' })],
  },
  {
    name: 'Composer exact', eco: 'composer', diff: 'textedit-update-pkg', file: 'composer.json', adapter: 'composer',
    items: [item({ depName: 'monolog/monolog', datasource: 'packagist', manager: 'composer', packageFile: 'composer.json', lockFiles: ['composer.lock'], currentValue: '2.9.1', newValue: '2.9.2' })],
  },
  {
    name: 'Composer range', eco: 'composer', diff: 'textedit-range-with', file: 'composer.json', adapter: 'composer',
    items: [item({ depName: 'guzzlehttp/guzzle', datasource: 'packagist', manager: 'composer', packageFile: 'composer.json', lockFiles: ['composer.lock'], currentValue: '^7.5', newValue: '^7.8', newVersion: '7.8.0' })],
  },
  {
    name: 'Cargo inline table', eco: 'cargo', diff: 'lockfirst-precise-then-textedit', file: 'Cargo.toml', adapter: 'cargo',
    items: [item({ depName: 'serde', datasource: 'crate', manager: 'cargo', packageFile: 'Cargo.toml', lockFiles: ['Cargo.lock'], currentValue: '1.0.190', newValue: '1.0.200' })],
  },
  {
    name: 'Cargo plain', eco: 'cargo', diff: 'textedit-update-precise', file: 'Cargo.toml', adapter: 'cargo',
    items: [item({ depName: 'itoa', datasource: 'crate', manager: 'cargo', packageFile: 'Cargo.toml', lockFiles: ['Cargo.lock'], currentValue: '1.0.9', newValue: '1.0.10' })],
  },
  {
    name: 'Go require block (comment kept)', eco: 'go', diff: 'textedit-tidy', file: 'go.mod', adapter: 'go',
    items: [item({ depName: 'github.com/google/uuid', datasource: 'go', manager: 'gomod', packageFile: 'go.mod', currentValue: 'v1.3.0', newValue: 'v1.4.0', newVersion: 'v1.4.0' })],
  },
  {
    name: 'Maven property', eco: 'maven', diff: 'native-set-property', file: 'pom.xml', adapter: 'maven',
    items: [item({ depName: 'com.fasterxml.jackson.core:jackson-databind', datasource: 'maven', manager: 'maven', packageFile: 'pom.xml', currentValue: '2.15.2', newValue: '2.16.1', sharedVariableName: 'jackson.version' })],
  },
  {
    name: 'Maven inline <version>', eco: 'maven', diff: 'textedit-inline', file: 'pom.xml', adapter: 'maven',
    items: [item({ depName: 'com.google.guava:guava', datasource: 'maven', manager: 'maven', packageFile: 'pom.xml', currentValue: '32.1.2-jre', newValue: '33.0.0-jre' })],
  },
  {
    name: 'Gradle catalog inline version', eco: 'gradle', diff: 'textedit-catalog-write-locks', file: 'gradle/libs.versions.toml', adapter: 'gradle',
    items: [item({ depName: 'com.google.guava:guava', datasource: 'maven', manager: 'gradle', packageFile: 'gradle/libs.versions.toml', currentValue: '32.1.2-jre', newValue: '33.0.0-jre' })],
  },
  {
    name: 'Gradle catalog [versions] ref', eco: 'gradle', diff: 'textedit-versionref-update-locks', file: 'gradle/libs.versions.toml', adapter: 'gradle',
    items: [item({ depName: 'com.fasterxml.jackson.core:jackson-databind', datasource: 'maven', manager: 'gradle', packageFile: 'gradle/libs.versions.toml', currentValue: '2.15.2', newValue: '2.16.1', sharedVariableName: 'jackson' })],
  },
  {
    name: 'Dockerfile FROM tag (stage name + comment kept)', eco: 'docker', diff: 'from-tag-textedit', file: 'Dockerfile', adapter: 'replace-string',
    items: [item({ depName: 'node', datasource: 'docker', manager: 'dockerfile', packageFile: 'Dockerfile', currentValue: '20.11.0-alpine', newValue: '20.11.1-alpine', replaceString: 'node:20.11.0-alpine' })],
  },
  {
    name: 'Dockerfile FROM tag@digest', eco: 'docker', diff: 'from-tag-digest-textedit', file: 'Dockerfile', adapter: 'replace-string',
    items: [item({
      depName: 'mcr.microsoft.com/dotnet/aspnet', datasource: 'docker', manager: 'dockerfile', packageFile: 'Dockerfile', currentValue: '8.0.1', newValue: '8.0.2',
      currentDigest: `sha256:${'0'.repeat(64)}`, newDigest: 'sha256:af8751ab2accc4928c1e516652f686aa20f6c934f14b2fbc3c80926400980a94',
      replaceString: `mcr.microsoft.com/dotnet/aspnet:8.0.1@sha256:${'0'.repeat(64)}`,
    })],
  },
  {
    name: 'Actions uses: SHA + # vX and plain tag', eco: 'actions', diff: 'uses-sha-and-tag-textedit', file: '.github/workflows/ci.yml', adapter: 'replace-string',
    items: [
      item({
        depName: 'actions/checkout', datasource: 'github-tags', manager: 'github-actions', packageFile: '.github/workflows/ci.yml', currentValue: 'v4.1.1', newValue: 'v4.1.7',
        currentDigest: 'b4ffde65f46336ab88eb53be808477a3936bae11', newDigest: '692973e3d937129bcbf40652eb9f2f61becf3332',
        replaceString: 'actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11 # v4.1.1',
      }),
      item({ depName: 'actions/setup-node', datasource: 'github-tags', manager: 'github-actions', packageFile: '.github/workflows/ci.yml', currentValue: 'v4.0.0', newValue: 'v4.0.2', replaceString: 'actions/setup-node@v4.0.0' }),
    ],
  },
];

async function runEdits(c: { eco: string; items: PlanItem[] }): Promise<{ root: string; adapters: Adapter[] }> {
  const root = tempDir();
  copyTree(fixture(`apply/${c.eco}/base`), root);
  const ctx = ctxFor(root, undefined, c.items);
  const adapters: Adapter[] = [];
  for (const i of c.items) {
    const a = adapterFor(i)!;
    adapters.push(a);
    await a.edit(ctx, i);
  }
  return { root, adapters };
}

describe('pure manifest edits equal the real S13 manifest changes', () => {
  it.each(CASES)('$name', async (c) => {
    const before = fs.readFileSync(fixture(`apply/${c.eco}/base/${c.file}`));
    const { root, adapters } = await runEdits(c);
    expect(adapters.map((a) => a.id)).toEqual(c.items.map(() => c.adapter));
    const after = fs.readFileSync(path.join(root, c.file));
    expect(changedPairs(before.toString('utf8'), after.toString('utf8'))).toEqual(diffPairs(readFixture(`apply/${c.eco}/${c.diff}.diff`), c.file));
    expect(onlyLinesChanged(before, after)).toBe(true);
  });

  it('keeps the CRLF line endings of the csproj byte for byte', async () => {
    const c = CASES.find((x) => x.diff === 'csproj-textedit-restore')!;
    const before = fs.readFileSync(fixture('apply/nuget/base/App.csproj'));
    expect(before.includes(Buffer.from('\r\n'))).toBe(true);
    const { root } = await runEdits(c);
    const after = fs.readFileSync(path.join(root, 'App.csproj'));
    expect(after.toString('latin1')).toBe(before.toString('latin1').replace('Version="13.0.1"', 'Version="13.0.3"'));
  });

  it('NuGet child <Version> element: one line changes, where `dotnet add package` (S13 native) rewrote two unrelated lines', async () => {
    const i = item({ depName: 'Serilog', datasource: 'nuget', manager: 'nuget', packageFile: 'App.csproj', currentValue: '3.0.1', newValue: '3.1.1' });
    const before = fs.readFileSync(fixture('apply/nuget/base/App.csproj'), 'utf8');
    const { root } = await runEdits({ eco: 'nuget', items: [i] });
    const ours = changedPairs(before, fs.readFileSync(path.join(root, 'App.csproj'), 'utf8'));
    expect(ours).toEqual([{ minus: '      <Version>3.0.1</Version>', plus: '      <Version>3.1.1</Version>' }]);
    const native = diffPairs(readFixture('apply/nuget/csproj-native-add-child-element.diff'), 'App.csproj');
    expect(native.length).toBeGreaterThan(ours.length);
  });
});

describe('edits refuse instead of guessing', () => {
  it.each([
    ['a version that is not there', npmItem('express', '^9.9.9', '^9.9.10', 'package-lock.json'), 'npm'],
    ['a dependency in another block than depType', { ...npmItem('typescript', '~5.3.3', '~5.4.0', 'package-lock.json'), depType: 'dependencies' }, 'npm'],
    ['a NuGet id that is not referenced', item({ depName: 'Nope', datasource: 'nuget', manager: 'nuget', packageFile: 'App.csproj', currentValue: '1.0.0', newValue: '1.0.1' }), 'nuget'],
    ['a digest-pinned image without newDigest', item({ depName: 'mcr.microsoft.com/dotnet/aspnet', datasource: 'docker', manager: 'dockerfile', packageFile: 'Dockerfile', currentValue: '8.0.1', newValue: '8.0.2', currentDigest: `sha256:${'0'.repeat(64)}`, replaceString: `mcr.microsoft.com/dotnet/aspnet:8.0.1@sha256:${'0'.repeat(64)}` }), 'docker'],
  ])('%s', async (_n, i, eco) => {
    const root = tempDir();
    copyTree(fixture(`apply/${eco}/base`), root);
    const a = adapterFor(i)!;
    const ctx = ctxFor(root, async () => ({ code: 1, stdout: '', stderr: 'offline' }), [i]);
    await expect(a.edit(ctx, i)).rejects.toBeInstanceOf(EditRefused);
  });

  it('two identical FROM lines are edited together only when the group plans both', async () => {
    const root = tempDir();
    fs.writeFileSync(path.join(root, 'Dockerfile'), 'FROM node:20 AS a\nFROM node:20 AS b\n');
    const one = item({ depName: 'node', datasource: 'docker', manager: 'dockerfile', packageFile: 'Dockerfile', currentValue: '20', newValue: '22', replaceString: 'node:20' });
    const a = adapterFor(one)!;
    await expect(a.edit(ctxFor(root, undefined, [one]), one)).rejects.toThrow(/occurs 2x, the group plans 1/);
    const ctx = ctxFor(root, undefined, [one, { ...one }]);
    await a.edit(ctx, one);
    await a.edit(ctx, { ...one });
    expect(fs.readFileSync(path.join(root, 'Dockerfile'), 'utf8')).toBe('FROM node:22 AS a\nFROM node:22 AS b\n');
  });
});
