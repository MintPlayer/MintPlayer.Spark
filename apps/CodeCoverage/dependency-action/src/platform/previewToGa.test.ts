import * as fs from 'node:fs';
import * as path from 'node:path';
import type { PlatformTable } from '../contracts/types';
import { isValid } from '../contracts/validate';
import { runLookup } from '../lookup/lookup';
import type { RenovateReport } from '../lookup/mapReport';
import { copyTree, fixture, gitRepo, readJsonFixture, tempDir, writeTree } from '../testing/helpers';
import { realExec } from '../util/exec';
import { directoryFiles } from '../util/repoFiles';
import { applyPlatform, inChannelMoveChanges } from './applyPlatform';
import { isNewerVersion } from './channels';
import { scanLocations } from './locations';

/**
 * #617 (docs/code-coverage/sdk-updates-PRD.md §12): a repository on the 11.0 RC the day 11.0 goes GA. The platform table
 * is the single source for the move inside the channel (D29), the `dotnet-quality: preview` goes with it (D31).
 */
const RC_SDK = '11.0.100-rc.2.26504.105';
const table = readJsonFixture<PlatformTable>('platform-feeds/platform-table.json');
const gaTable: PlatformTable = {
  ...table,
  sdks: {
    ...table.sdks,
    dotnet: table.sdks.dotnet!.map((c) =>
      c.channel === '11.0' ? { ...c, latestVersion: '11.0.0', latestSdk: '11.0.100', releaseTimestamp: '2026-11-10', preview: false, lts: false } : c,
    ),
  },
};
const report = readJsonFixture<RenovateReport>('renovate/dotnet-preview-to-ga/report.json');

async function lookupWith(platforms: PlatformTable) {
  const { dir, sha } = gitRepo({}, fixture('renovate/dotnet-preview-to-ga/input'));
  const out = tempDir();
  const result = await runLookup({
    exec: realExec, repoDir: dir, outDir: out, workDir: tempDir(), expectedSha: sha, platforms,
    fetchActionYml: async () => null, renovate: async () => ({ report, seconds: 3, log: '' }), now: () => new Date('2026-11-11T08:00:00Z'),
  });
  return { ...result, json: JSON.parse(fs.readFileSync(result.file, 'utf8')) };
}

describe('#617 isNewerVersion', () => {
  it.each([
    ['11.0.100', '11.0.100-rc.2.26504.105', true],
    ['11.0.100-rc.3.26522.148', '11.0.100-rc.2.26504.105', true],
    ['11.0.100-rc.2.26504.105', '11.0.100-rc.3.26522.148', false],
    ['11.0.101', '11.0.100', true],
    ['11.0.100', '11.0.100', false],
    ['11.0.100-rc.2.26504.105', '11.0.100', false],
    ['10.0.403', '10.0.402', true],
  ])('%s newer than %s: %s', (next, current, expected) => {
    expect(isNewerVersion(next, current)).toBe(expected);
  });
});

describe('#617 the dotnet-quality scan', () => {
  it('records a quality next to its version, and a lone quality as computed', async () => {
    const root = tempDir();
    writeTree(root, {
      '.github/workflows/a.yml': 'jobs:\n  a:\n    steps:\n      - uses: actions/setup-dotnet@v6\n        with:\n          dotnet-version: 11.0.x\n          dotnet-quality: "preview"\n      - uses: actions/setup-dotnet@v6\n        with:\n          dotnet-quality: preview\n',
    });
    const quality = (await scanLocations(directoryFiles(root), ['dotnet'])).filter((f) => f.kind === 'dotnet-quality');
    expect(quality.map((f) => ({ line: f.line + 1, value: f.value, channel: f.channel, computed: f.computed }))).toEqual([
      { line: 7, value: 'preview', channel: '11.0', computed: false },
      { line: 10, value: 'preview', channel: null, computed: true },
    ]);
  });
});

describe('#617 the lookup on the day of the release (D29)', () => {
  it('red before #617: an RC table offers nothing on the channel the repository is on', async () => {
    const { document } = await lookupWith(table);
    const dotnet = document.platforms.find((p) => p.sdk === 'dotnet')!;
    expect(dotnet.current.channels).toEqual(['11.0']);
    expect(dotnet.candidates).toEqual([]);
  });

  it('a GA row for the current channel is the candidate, and Renovate\'s in-channel SDK records fold into it', async () => {
    const { document, json } = await lookupWith(gaTable);
    expect(isValid('lookup', json).errors).toBe('');
    const dotnet = document.platforms.find((p) => p.sdk === 'dotnet')!;
    expect(dotnet.candidates).toEqual([expect.objectContaining({ channel: '11.0', latestVersion: '11.0.0', latestSdk: '11.0.100', preview: false })]);
    expect(dotnet.locations.map((l) => `${l.file}:${l.kind}:${l.value}`)).toEqual(expect.arrayContaining([
      `global.json:global.json:${RC_SDK}`,
      `.github/workflows/ci.yml:setup-action:${RC_SDK}`,
      '.github/workflows/ci.yml:dotnet-quality:preview',
    ]));
    // D29/D34: the setup-dotnet pin Renovate offered 11.0.100 and the global.json it offered nothing are both the
    // platform group's now; the RC package is still Renovate's record (it rides, D33).
    expect(document.records.filter((r) => r.depName === 'dotnet-sdk')).toEqual([]);
    expect(document.records.map((r) => r.depName)).toContain('Microsoft.AspNetCore.OpenApi');
  });
});

describe('#617 applying the in-channel plan (D29, D31)', () => {
  const plan = { sdk: 'dotnet' as const, fromChannel: '11.0', toChannel: '11.0', toVersion: '11.0.0', sdkVersion: '11.0.100' };

  it('moves every RC pin to the release, drops the preview quality, leaves the TFM and the floating pin', async () => {
    const root = tempDir();
    copyTree(fixture('renovate/dotnet-preview-to-ga/input'), root);
    const files = directoryFiles(root);
    expect(inChannelMoveChanges(await scanLocations(files, ['dotnet']), plan)).toBe(true);
    const result = await applyPlatform(files, plan);
    const read = (f: string) => fs.readFileSync(path.join(root, f), 'utf8');
    expect(result.edited).toEqual(['.github/workflows/ci.yml', 'global.json']);
    expect(result.reported).toEqual([]);
    expect(read('global.json')).toBe('{\n  "sdk": {\n    "version": "11.0.100",\n    "rollForward": "latestMinor"\n  }\n}\n');
    const ci = read('.github/workflows/ci.yml');
    expect(ci).toContain('          dotnet-version: 11.0.100\n      - run: dotnet build');
    expect(ci).toContain('          dotnet-version: 11.0.x\n      - run: dotnet pack');
    expect(ci).not.toContain('dotnet-quality');
    expect(read('src/Web/Web.csproj')).toContain('<TargetFramework>net11.0</TargetFramework>');
  });

  it('a repository already on the release changes nothing, and a table behind the repository moves nothing back', async () => {
    const root = tempDir();
    writeTree(root, { 'global.json': '{ "sdk": { "version": "11.0.100" } }\n' });
    const files = directoryFiles(root);
    expect(inChannelMoveChanges(await scanLocations(files, ['dotnet']), plan)).toBe(false);
    const behind = { ...plan, toVersion: '11.0.0-rc.2', sdkVersion: RC_SDK };
    expect(inChannelMoveChanges(await scanLocations(files, ['dotnet']), behind)).toBe(false);
    expect((await applyPlatform(files, behind)).edited).toEqual([]);
  });

  // SpaServices #121 (2026-11-13): an Arcade-style `tools.dotnet` (the SDK the build scripts install) stayed on the RC
  // while `sdk.version` moved. It is a global.json SDK pin like `sdk.version`, and moves with it.
  it('moves an Arcade tools.dotnet SDK with sdk.version, and leaves the runtimes', async () => {
    const spaServices = (sdk: string) =>
      `{\n\t"sdk": {\n\t\t"version": "${sdk}",\n\t\t"rollForward": "latestFeature"\n\t},\n\t"tools": {\n\t\t"dotnet": "${sdk}",\n` +
      '\t\t"runtimes": {\n\t\t\t"dotnet/x64": [\n\t\t\t\t"$(MicrosoftNETCoreAppInternalPackageVersion)"\n\t\t\t],\n' +
      '\t\t\t"dotnet/x86": [\n\t\t\t\t"$(MicrosoftNETCoreAppInternalPackageVersion)"\n\t\t\t]\n\t\t}\n\t}\n}\n';
    const root = tempDir();
    writeTree(root, { 'global.json': spaServices('11.0.100-rc.1.26425.128') });
    const files = directoryFiles(root);
    const found = (await scanLocations(files, ['dotnet'])).map((f) => `${f.kind}:${f.line + 1}:${f.value}:${f.channel}`);
    expect(found).toEqual(['global.json:3:11.0.100-rc.1.26425.128:11.0', 'global.json:7:11.0.100-rc.1.26425.128:11.0']);
    const result = await applyPlatform(files, plan);
    expect(result.edited).toEqual(['global.json']);
    expect(fs.readFileSync(path.join(root, 'global.json'), 'utf8')).toBe(spaServices('11.0.100'));
  });

  it('a move to another preview keeps the quality', async () => {
    const root = tempDir();
    copyTree(fixture('renovate/dotnet-preview-to-ga/input'), root);
    const result = await applyPlatform(directoryFiles(root), { ...plan, toVersion: '11.0.0-rc.3', sdkVersion: '11.0.100-rc.3.26522.148' });
    expect(result.edited).toEqual(['.github/workflows/ci.yml', 'global.json']);
    expect(fs.readFileSync(path.join(root, '.github/workflows/ci.yml'), 'utf8')).toContain('dotnet-quality: preview');
  });
});
