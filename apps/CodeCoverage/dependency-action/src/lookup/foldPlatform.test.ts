import * as fs from 'node:fs';
import * as path from 'node:path';
import type { LookupDocument, PlatformRecord } from '../contracts/types';
import { applyPlatform } from '../platform/applyPlatform';
import { copyTree, fixture, gitRepo, readJsonFixture, tempDir } from '../testing/helpers';
import { realExec } from '../util/exec';
import { directoryFiles } from '../util/repoFiles';
import { runLookup } from './lookup';
import type { RenovateReport } from './mapReport';

const FEEDS: Record<string, string> = {
  'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json': 'releases-index.json',
  'https://endoflife.date/api/nodejs.json': 'nodejs.json',
  'https://endoflife.date/api/python.json': 'python.json',
  'https://endoflife.date/api/go.json': 'go.json',
  'https://endoflife.date/api/eclipse-temurin.json': 'eclipse-temurin.json',
};
const fetcher = async (url: string) => readJsonFixture(`platform-feeds/${FEEDS[url]}`);

async function lookup(name: string): Promise<LookupDocument> {
  const { dir, sha } = gitRepo({}, fixture(`renovate/${name}/input`));
  const report = readJsonFixture<RenovateReport>(`renovate/${name}/report.json`);
  const { document } = await runLookup({
    exec: realExec, repoDir: dir, outDir: tempDir(), workDir: tempDir(), expectedSha: sha, fetcher,
    fetchActionYml: async () => null,
    renovate: async () => ({ report, seconds: 1, log: '' }), now: () => new Date('2026-10-16T08:00:00Z'),
  });
  return document;
}

const key = (r: LookupDocument['records'][number]) => `${r.packageFile} ${r.depName} ${r.currentValue}`;

describe('R31 (S18-D1): the platform record owns its locations', () => {
  it('S18: setup-node / setup-dotnet with: values are only offered through platform:node / platform:dotnet', async () => {
    const doc = await lookup('s18-dependency-updater-test');

    // No separate node / dotnet-sdk records: the two duplicates of the live rolling PR.
    expect(doc.records.filter((r) => r.ecosystem.startsWith('platform:'))).toEqual([]);
    expect(doc.records.map((r) => r.depName).sort()).toEqual(['Newtonsoft.Json', 'Serilog', 'actions/checkout', 'actions/setup-dotnet', 'actions/setup-node']);

    // Exactly one platform record per sdk, each listing the folded with: location.
    expect(doc.platforms.map((p) => p.sdk).sort()).toEqual(['dotnet', 'node']);
    const node = doc.platforms.find((p) => p.sdk === 'node')!;
    const dotnet = doc.platforms.find((p) => p.sdk === 'dotnet')!;
    expect(node.current.channels).toEqual(['20']);
    expect(node.locations).toEqual([expect.objectContaining({ file: '.github/workflows/ci.yml', kind: 'setup-action', value: '20' })]);
    expect(node.candidates[0].channel).toBe('26');
    expect(dotnet.current.channels).toEqual(['8.0']);
    expect(dotnet.locations.map((l) => `${l.kind}=${l.value}`).sort()).toEqual(['setup-action=8.0.x', 'tfm=net8.0']);
    expect(dotnet.candidates.map((c) => c.channel)).toContain('10.0');
  });

  it('approving the platform group moves the setup-* with: values with the rest of the platform', async () => {
    const doc = await lookup('s18-dependency-updater-test');
    const root = tempDir();
    copyTree(fixture('renovate/s18-dependency-updater-test/input'), root);
    const target = (p: PlatformRecord, channel: string) => p.candidates.find((c) => c.channel === channel)!;

    const node = target(doc.platforms.find((p) => p.sdk === 'node')!, '26');
    const n = await applyPlatform(directoryFiles(root), { sdk: 'node', fromChannel: '20', toChannel: '26', toVersion: node.latestVersion });
    expect(n).toEqual({ edited: ['.github/workflows/ci.yml'], reported: [] });

    const dotnet = target(doc.platforms.find((p) => p.sdk === 'dotnet')!, '10.0');
    const d = await applyPlatform(directoryFiles(root), {
      sdk: 'dotnet', fromChannel: '8.0', toChannel: '10.0', toVersion: dotnet.latestVersion, sdkVersion: dotnet.latestSdk ?? null,
    });
    expect(d).toEqual({ edited: ['.github/workflows/ci.yml', 'src/Lib/Lib.csproj'], reported: [] });

    const ci = fs.readFileSync(path.join(root, '.github/workflows/ci.yml'), 'utf8');
    expect(ci).toContain('          node-version: 26\n');
    expect(ci).toContain('          dotnet-version: 10.0.x\n');
    expect(ci).toContain('      - uses: actions/setup-node@v4\n'); // the action's own major is its own record
    expect(fs.readFileSync(path.join(root, 'src/Lib/Lib.csproj'), 'utf8')).toContain('<TargetFramework>net10.0</TargetFramework>');
  });

  it('S12: channel moves fold, in-channel updates and unrecognised locations stay their own records', async () => {
    const doc = await lookup('docker-actions-platform');
    const byKey = new Map(doc.records.map((r) => [key(r), r]));
    const values = (k: string) => byKey.get(k)?.candidates.map((c) => c.value);

    // A location the platform adapter does not scan (a workflow `container:` image) keeps every candidate.
    expect(values('.github/workflows/ci.yml node 18.19.0')).toEqual(['18.20.8', '26.12.0']);
    // Locations the adapter only reports keep theirs too: a full-version mcr tag, a digest-pinned image.
    expect(values('Dockerfile mcr.microsoft.com/dotnet/sdk 8.0.100')).toEqual(['8.0.426', '10.0.402']);
    expect(values('Dockerfile python 3.11-slim')).toEqual(['3.15-slim', '3.11-slim']);
    // Not a platform image at all.
    expect(values('Dockerfile ubuntu 22.04')).toEqual(['26.04']);

    // Platform locations keep only the in-channel update the platform plan never makes...
    for (const k of ['.github/workflows/ci.yml node 20.11.0', '.nvmrc node 20.11.0', '.node-version node 20.11.0', '.tool-versions node 20.11.0']) {
      expect(values(k)).toEqual(['20.20.2']);
    }
    expect(values('Dockerfile node 20.11.0-alpine')).toEqual(['20.20.2-alpine']);
    expect(values('.github/workflows/ci.yml dotnet-sdk 8.0.100')).toEqual(['8.0.426']);
    expect(values('global.json dotnet-sdk 8.0.100')).toEqual(['8.0.426']);
    expect(values('.tool-versions java 17.0.8+7')).toEqual(['17.0.20+8']);
    // ...and a location whose only move is a channel move is folded away entirely.
    for (const k of ['Dockerfile mcr.microsoft.com/dotnet/aspnet 8.0', '.python-version python 3.11.4', '.tool-versions python 3.11.4', '.github/workflows/ci.yml python 3.11.4', '.github/workflows/ci.yml java-jdk 17', '.tool-versions golang 1.21.0', '.github/workflows/ci.yml go 1.21.0']) {
      expect(byKey.has(k), k).toBe(false);
    }
    // No candidate on a platform location crosses a channel any more.
    for (const r of doc.records.filter((r) => r.ecosystem.startsWith('platform:'))) {
      expect(r.candidates.every((c) => c.updateType !== 'major'), key(r)).toBe(true);
    }
  });

  it('a platform location below the platform\'s current channel stays its own record (the plan never moves it)', async () => {
    const { dir, sha } = gitRepo({
      '.nvmrc': '22\n',
      '.github/workflows/ci.yml': 'jobs:\n  b:\n    steps:\n      - uses: actions/setup-node@v4\n        with:\n          node-version: 20\n',
    });
    const record = (packageFile: string, currentValue: string, manager: string, datasource: string) => ({
      [manager]: [{ packageFile, deps: [{ depName: 'node', datasource, currentValue, updates: [{ newVersion: '24.22.0', newValue: '24', updateType: 'major', releaseTimestamp: '2026-10-15T01:57:24.000Z' }] }] }],
    });
    const report: RenovateReport = {
      repositories: { local: { packageFiles: { ...record('.github/workflows/ci.yml', '20', 'github-actions', 'github-releases'), ...record('.nvmrc', '22', 'nvm', 'node-version') } } },
    };
    const { document } = await runLookup({
      exec: realExec, repoDir: dir, outDir: tempDir(), workDir: tempDir(), expectedSha: sha, fetcher, fetchActionYml: async () => null,
      renovate: async () => ({ report, seconds: 1, log: '' }), now: () => new Date('2026-10-16T08:00:00Z'),
    });
    expect(document.platforms[0].current.channels).toEqual(['22', '20']);
    expect(document.records.map(key)).toEqual(['.github/workflows/ci.yml node 20']);
  });
});
