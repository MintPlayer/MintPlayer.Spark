import * as fs from 'node:fs';
import * as path from 'node:path';
import { copyTree, diffPairs, fixture, readFixture, readJsonFixture, tempDir, writeTree, type LinePair } from '../testing/helpers';
import { directoryFiles } from '../util/repoFiles';
import { applyPlatform, nextTfmList } from './applyPlatform';
import { channelOf, compareChannels, rewriteTfm, rewriteVersionValue } from './channels';
import { buildPlatformRecord, candidatesFromEndOfLife, candidatesFromReleasesIndex, type EndOfLifeCycle, type ReleasesIndexEntry } from './feeds';
import { scanLocations } from './locations';

const NOW = new Date('2026-10-16T08:00:00Z');

describe('channels', () => {
  it.each([
    ['dotnet', '10.0.100', '10.0'],
    ['dotnet', '11.0.100-rc.1.26425.128', '11.0'],
    ['node', 'v20.11.0', '20'],
    ['node', '22.x', '22'],
    ['python', '3.11-slim', '3.11'],
    ['java', 'temurin-17.0.8+7', '17'],
    ['java', '1.8', '8'],
    ['go', '1.21.5', '1.21'],
    ['go', 'go1.22', '1.22'],
    ['node', 'lts/*', null],
  ] as const)('%s %s -> %s', (sdk, v, ch) => expect(channelOf(sdk, v)).toBe(ch));

  it('compares numerically', () => {
    expect(compareChannels('10.0', '9.0')).toBeGreaterThan(0);
    expect(compareChannels('3.9', '3.13')).toBeLessThan(0);
  });

  it.each([
    ['node', '20', '20', '22', null, '22'],
    ['node', '20.x', '20', '22', null, '22.x'],
    ['node', 'v20', '20', '22', null, 'v22'],
    ['node', '20.11.0', '20', '22', '22.21.0', '22.21.0'],
    ['node', '20.11.0-alpine', '20', '22', '22.21.0', '22.21.0-alpine'],
    ['dotnet', '10.0.x', '10.0', '11.0', null, '11.0.x'],
    ['dotnet', '10.0-alpine', '10.0', '11.0', null, '11.0-alpine'],
    ['python', '3.12-slim', '3.12', '3.13', null, '3.13-slim'],
    ['node', '20.11.0', '20', '22', null, null],
    ['node', '18', '20', '22', null, null],
  ] as const)('rewrite %s %s (%s -> %s)', (sdk, value, from, to, toVersion, expected) => {
    expect(rewriteVersionValue(sdk, value, from, to, toVersion)).toBe(expected);
  });

  it('rewrites a TFM keeping its platform suffix', () => {
    expect(rewriteTfm('net10.0-windows', '10.0', '11.0')).toBe('net11.0-windows');
    expect(rewriteTfm('net9.0', '10.0', '11.0')).toBeNull();
  });
});

describe('platform feeds (real captures, test/fixtures/platform-feeds)', () => {
  it('releases-index.json: channels, the go-live 11.0 is a preview, eol phases', () => {
    const c = candidatesFromReleasesIndex(readJsonFixture<{ 'releases-index': ReleasesIndexEntry[] }>('platform-feeds/releases-index.json'));
    const eleven = c.find((x) => x.channel === '11.0')!;
    expect(eleven).toMatchObject({ preview: true, latestVersion: '11.0.0-rc.2', latestSdk: '11.0.100-rc.2.26504.105', lts: false });
    expect(c.find((x) => x.channel === '10.0')).toMatchObject({ preview: false, lts: true });
    expect(c.every((x) => Number(x.channel.split('.')[0]) >= 5)).toBe(true);
  });

  it('endoflife.date: node cycles, java temurin, go', () => {
    const node = candidatesFromEndOfLife('node', readJsonFixture<EndOfLifeCycle[]>('platform-feeds/nodejs.json'));
    expect(node.find((x) => x.channel === '26')).toMatchObject({ latestVersion: '26.12.0', preview: false });
    const go = candidatesFromEndOfLife('go', readJsonFixture<EndOfLifeCycle[]>('platform-feeds/go.json'));
    expect(go.every((x) => /^1\.\d+$/.test(x.channel))).toBe(true);
    const java = candidatesFromEndOfLife('java', readJsonFixture<EndOfLifeCycle[]>('platform-feeds/eclipse-temurin.json'));
    expect(java.find((x) => x.channel === '25')?.lts).toBe(true);
  });

  it('a record lists only newer, non-EOL channels, previews flagged', () => {
    const feed = candidatesFromReleasesIndex(readJsonFixture<{ 'releases-index': ReleasesIndexEntry[] }>('platform-feeds/releases-index.json'));
    const loc = { sdk: 'dotnet' as const, file: 'a.csproj', kind: 'tfm' as const, value: 'net10.0', channel: '10.0', computed: false, reason: null, line: 0, start: 0, end: 7 };
    const rec = buildPlatformRecord('dotnet', [loc], feed, NOW);
    expect(rec.current).toEqual({ channels: ['10.0'], preview: false });
    expect(rec.candidates.map((c) => c.channel)).toEqual(['11.0']);
    expect(rec.candidates[0].preview).toBe(true);
    expect(buildPlatformRecord('dotnet', [loc], null, NOW)).toMatchObject({ source: 'none', candidates: [] });
  });
});

describe('location scanning on file trees', () => {
  it('reports every location of the S12 docker/actions/platform sample', async () => {
    const found = await scanLocations(directoryFiles(fixture('renovate/docker-actions-platform/input')));
    const by = (sdk: string, kind: string) => found.filter((f) => f.sdk === sdk && f.kind === kind).map((f) => `${f.file}=${f.value}${f.computed ? ' (computed)' : ''}`);
    expect(by('dotnet', 'global.json')).toEqual(['global.json=8.0.100']);
    expect(by('dotnet', 'docker')).toEqual(['Dockerfile=8.0.100', 'Dockerfile=8.0']);
    expect(by('dotnet', 'setup-action')).toEqual(['.github/workflows/ci.yml=8.0.100']);
    expect(by('node', 'version-file').sort()).toEqual(['.node-version=20.11.0', '.nvmrc=20.11.0']);
    expect(by('node', 'docker')).toEqual(['Dockerfile=20.11.0-alpine']);
    expect(by('node', 'setup-action')).toEqual(['.github/workflows/ci.yml=20.11.0']);
    expect(by('node', 'tool-versions')).toEqual(['.tool-versions=20.11.0']);
    expect(by('python', 'docker')).toEqual(['Dockerfile=3.11-slim (computed)']);
    expect(by('python', 'setup-action')).toEqual(['.github/workflows/ci.yml=3.11.4']);
    expect(by('java', 'tool-versions')).toEqual(['.tool-versions=temurin-17.0.8+7']);
    expect(by('java', 'setup-action')).toEqual(['.github/workflows/ci.yml=17']);
    expect(by('go', 'setup-action')).toEqual(['.github/workflows/ci.yml=1.21.0']);
  });

  it('TFM in Directory.Build.props vs per csproj, multi-targeting, computed and conditioned TFMs', async () => {
    const root = tempDir();
    writeTree(root, {
      'Directory.Build.props': '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n',
      'src/Multi/Multi.csproj': '<Project>\n  <PropertyGroup>\n    <TargetFrameworks>net10.0;net48</TargetFrameworks>\n  </PropertyGroup>\n</Project>\n',
      'src/Computed/Computed.csproj': '<Project>\n  <PropertyGroup>\n    <TargetFramework>$(NetCurrent)</TargetFramework>\n  </PropertyGroup>\n</Project>\n',
      'src/Cond/Cond.csproj': '<Project>\n  <PropertyGroup>\n    <TargetFramework Condition="\'$(X)\' == \'1\'">net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n',
      'src/Ref/Ref.csproj': '<Project>\n  <ItemGroup>\n    <PackageReference Include="Serilog">\n      <Version>3.0.1</Version>\n    </PackageReference>\n  </ItemGroup>\n</Project>\n',
    });
    const found = await scanLocations(directoryFiles(root), ['dotnet']);
    const tfm = found.filter((f) => f.kind === 'tfm').map((f) => [f.file, f.value, f.channel, f.computed]);
    expect(tfm).toEqual([
      ['Directory.Build.props', 'net10.0', '10.0', false],
      ['src/Computed/Computed.csproj', '$(NetCurrent)', null, true],
      ['src/Cond/Cond.csproj', 'net10.0', '10.0', true],
      ['src/Multi/Multi.csproj', 'net10.0;net48', '10.0', false],
    ]);
    expect(found.filter((f) => f.kind === 'package-version')).toEqual([]);
  });
});

async function applyOn(files: Record<string, string>, plan: Parameters<typeof applyPlatform>[1]) {
  const root = tempDir();
  writeTree(root, files);
  const result = await applyPlatform(directoryFiles(root), plan);
  const read = (f: string) => fs.readFileSync(path.join(root, f), 'utf8');
  return { result, read };
}

describe('applyPlatform', () => {
  const dotnet = { sdk: 'dotnet' as const, fromChannel: '10.0', toChannel: '11.0', toVersion: '11.0.0', sdkVersion: '11.0.100' };

  it('strategy replace (default): every location on the channel, nothing else', async () => {
    const { result, read } = await applyOn(
      {
        'Directory.Build.props': '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n',
        'old/Old.csproj': '<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>\n',
        'global.json': '{\n  "sdk": {\n    "version": "10.0.100",\n    "rollForward": "latestFeature"\n  }\n}\n',
        Dockerfile: 'FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine\n',
        '.github/workflows/ci.yml': 'jobs:\n  b:\n    steps:\n      - uses: actions/setup-dotnet@v5\n        with:\n          dotnet-version: 10.0.x\n',
      },
      dotnet,
    );
    expect(result.edited).toEqual(['.github/workflows/ci.yml', 'Directory.Build.props', 'Dockerfile', 'global.json']);
    expect(read('Directory.Build.props')).toContain('<TargetFramework>net11.0</TargetFramework>');
    expect(read('old/Old.csproj')).toContain('net9.0');
    expect(read('global.json')).toBe('{\n  "sdk": {\n    "version": "11.0.100",\n    "rollForward": "latestFeature"\n  }\n}\n');
    expect(read('Dockerfile')).toBe('FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine\n');
    expect(read('.github/workflows/ci.yml')).toContain('dotnet-version: 11.0.x');
  });

  it('strategy add + keepPrevious on a multi-target project; TargetFramework becomes TargetFrameworks', async () => {
    expect(nextTfmList('net10.0;net48', { ...dotnet, strategy: 'add' })).toBe('net11.0;net10.0;net48');
    expect(nextTfmList('net9.0;net10.0', { ...dotnet, strategy: 'add', keepPrevious: 1 })).toBe('net11.0;net10.0');
    expect(nextTfmList('net9.0;net10.0', { ...dotnet, strategy: 'add', keepPrevious: 2 })).toBe('net11.0;net10.0;net9.0');
    expect(nextTfmList('net10.0;net48', dotnet)).toBe('net11.0;net48');
    const { read } = await applyOn({ 'A.csproj': '<Project>\r\n  <PropertyGroup>\r\n    <TargetFramework>net10.0</TargetFramework>\r\n  </PropertyGroup>\r\n</Project>\r\n' }, { ...dotnet, strategy: 'add' });
    expect(read('A.csproj')).toBe('<Project>\r\n  <PropertyGroup>\r\n    <TargetFrameworks>net11.0;net10.0</TargetFrameworks>\r\n  </PropertyGroup>\r\n</Project>\r\n');
  });

  it('a computed or conditioned TFM is reported, never edited (R8)', async () => {
    const files = {
      'C.csproj': '<Project><PropertyGroup><TargetFramework>$(NetCurrent)</TargetFramework></PropertyGroup></Project>\n',
      'D.csproj': '<Project><PropertyGroup><TargetFramework Condition="true">net10.0</TargetFramework></PropertyGroup></Project>\n',
    };
    const { result, read } = await applyOn(files, dotnet);
    expect(result.edited).toEqual([]);
    expect(result.reported.map((r) => r.file).sort()).toEqual(['C.csproj', 'D.csproj']);
    expect(read('C.csproj')).toBe(files['C.csproj']);
  });

  it('lockPackageMajor moves the project version major only when opted in', async () => {
    const files = { 'Directory.Build.props': '<Project><PropertyGroup>\n<TargetFramework>net10.0</TargetFramework>\n<Version>10.0.0-preview.84</Version>\n</PropertyGroup></Project>\n' };
    expect((await applyOn(files, dotnet)).read('Directory.Build.props')).toContain('<Version>10.0.0-preview.84</Version>');
    expect((await applyOn(files, { ...dotnet, lockPackageMajor: true })).read('Directory.Build.props')).toContain('<Version>11.0.0-preview.84</Version>');
  });

  it('honours locations / exclude overrides', async () => {
    const files = {
      'a/A.csproj': '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n',
      'b/B.csproj': '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n',
    };
    expect((await applyOn(files, { ...dotnet, exclude: ['b/**'] })).result.edited).toEqual(['a/A.csproj']);
    expect((await applyOn(files, { ...dotnet, locations: ['b/*.csproj'] })).result.edited).toEqual(['b/B.csproj']);
  });

  it('node: version files, setup-node, Docker; ranges and @types/node are reported', async () => {
    const { result, read } = await applyOn(
      {
        '.nvmrc': '20\n',
        '.node-version': '20.11.0\n',
        'package.json': '{\n  "engines": { "node": ">=20" },\n  "devDependencies": { "@types/node": "^20.11.0" }\n}\n',
        'web/package.json': '{\n  "engines": {\n    "node": "^18 || ^20"\n  }\n}\n',
        Dockerfile: 'FROM node:20-alpine\n',
        '.github/workflows/ci.yml': 'jobs:\n  b:\n    steps:\n      - uses: actions/setup-node@v5\n        with:\n          node-version: \'20.x\'\n',
      },
      { sdk: 'node', fromChannel: '20', toChannel: '22', toVersion: '22.21.0' },
    );
    expect(read('.nvmrc')).toBe('22\n');
    expect(read('.node-version')).toBe('22.21.0\n');
    expect(read('package.json')).toContain('"node": ">=22"');
    expect(read('package.json')).toContain('"@types/node": "^20.11.0"');
    expect(read('Dockerfile')).toBe('FROM node:22-alpine\n');
    expect(read('.github/workflows/ci.yml')).toContain("node-version: '22.x'");
    expect(result.reported.map((r) => r.file).sort()).toEqual(['package.json', 'web/package.json']);
  });

  it('python, java and go: version files, setup-*, images and go.mod', async () => {
    const py = await applyOn({ '.python-version': '3.12\n', Dockerfile: 'FROM python:3.12-slim\n', 'pyproject.toml': 'requires-python = ">=3.12"\n' }, { sdk: 'python', fromChannel: '3.12', toChannel: '3.13' });
    expect(py.read('.python-version')).toBe('3.13\n');
    expect(py.read('Dockerfile')).toBe('FROM python:3.13-slim\n');
    expect(py.result.reported).toEqual([expect.objectContaining({ file: 'pyproject.toml' })]);
    const java = await applyOn({ 'pom.xml': '<properties>\n  <maven.compiler.release>21</maven.compiler.release>\n</properties>\n', Dockerfile: 'FROM eclipse-temurin:21-jre\n' }, { sdk: 'java', fromChannel: '21', toChannel: '25' });
    expect(java.read('pom.xml')).toContain('<maven.compiler.release>25</maven.compiler.release>');
    expect(java.read('Dockerfile')).toBe('FROM eclipse-temurin:25-jre\n');
    const go = await applyOn({ 'go.mod': 'module x\n\ngo 1.24\n\ntoolchain go1.24.3\n', Dockerfile: 'FROM golang:1.24-alpine\n' }, { sdk: 'go', fromChannel: '1.24', toChannel: '1.25', toVersion: '1.25.2' });
    expect(go.read('go.mod')).toBe('module x\n\ngo 1.25\n\ntoolchain go1.25.2\n');
    expect(go.read('Dockerfile')).toBe('FROM golang:1.25-alpine\n');
  });
});

/**
 * S19 golden: the dotnet adapter's dry run on this repo BEFORE 9808c1a3b ("Retarget the whole solution
 * to .NET 11") against that hand-made commit. Every edit we make is in the commit; every commit line we
 * do not make is in an explained category (SOURCE.md).
 */
describe('S19 golden: net10 -> net11 on this repository', () => {
  const OWN = /<TargetFrameworks?>|<Version>\d|"version": "|^FROM mcr\.microsoft\.com\/dotnet\/|dotnet-version:/;

  it('matches the hand-made migration commit, differences explained', async () => {
    const root = tempDir();
    copyTree(fixture('platform/s19-net11/before'), root);
    const files = directoryFiles(root);
    const before = new Map<string, string>();
    for (const f of await files.list()) before.set(f, fs.readFileSync(path.join(root, f), 'utf8'));
    const result = await applyPlatform(files, { sdk: 'dotnet', fromChannel: '10.0', toChannel: '11.0', sdkVersion: '11.0.100-rc.1.26425.128', lockPackageMajor: true });

    const commit = readFixture('platform/s19-net11/commit.diff');
    const ours: Array<LinePair & { file: string }> = [];
    for (const [f, text] of before) {
      const after = fs.readFileSync(path.join(root, f), 'utf8');
      const a = text.split(/\r?\n/);
      const b = after.split(/\r?\n/);
      expect(b.length).toBe(a.length);
      a.forEach((l, i) => {
        if (l !== b[i]) ours.push({ file: f, minus: l, plus: b[i] });
      });
    }
    const theirs = [...before.keys()].flatMap((f) => {
      try {
        return diffPairsLoose(commit, f).map((p) => ({ file: f, ...p }));
      } catch {
        return [];
      }
    });
    const key = (p: { file: string; minus: string; plus: string }) => `${p.file}\n${p.minus.replace(/^\ufeff/, '')}\n${p.plus.replace(/^\ufeff/, '')}`;
    const theirKeys = new Set(theirs.map(key));
    // 1. Nothing we change is outside the human commit.
    expect(ours.filter((p) => !theirKeys.has(key(p)))).toEqual([]);
    // 2. Every platform line of the human commit, we change too.
    const ourKeys = new Set(ours.map(key));
    const missing = theirs.filter((p) => OWN.test(p.minus.trim()) && !ourKeys.has(key(p)));
    expect(missing).toEqual([]);
    // 3. The rest is the explained categories: PackageReference bumps/removals (locked packages, a
    //    cleanup) and workflow prose.
    const rest = theirs.filter((p) => !ourKeys.has(key(p)));
    for (const p of rest) expect(/PackageReference|^\s*#|net1[01]\.0 project/.test(p.minus) || p.minus === '').toBe(true);
    expect(result.edited.length).toBeGreaterThan(30);
  });
});

/** Like diffPairs, but tolerates hunks whose -/+ counts differ (removed PackageReference lines). */
function diffPairsLoose(diff: string, file: string): LinePair[] {
  const sections = diff.split(/^diff --git /m).filter(Boolean);
  const section = sections.find((s) => s.startsWith(`a/${file} b/${file}`));
  if (!section) throw new Error('no section');
  const out: LinePair[] = [];
  let minus: string[] = [];
  let plus: string[] = [];
  const flush = () => {
    const n = Math.max(minus.length, plus.length);
    for (let i = 0; i < n; i++) out.push({ minus: minus[i] ?? '', plus: plus[i] ?? '' });
    minus = [];
    plus = [];
  };
  for (const line of section.split('\n')) {
    if (line.startsWith('---') || line.startsWith('+++')) continue;
    if (line.startsWith('-')) minus.push(line.slice(1).replace(/\r$/, ''));
    else if (line.startsWith('+')) plus.push(line.slice(1).replace(/\r$/, ''));
    else flush();
  }
  flush();
  return out;
}

void diffPairs;
