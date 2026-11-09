import * as fs from 'node:fs';
import * as path from 'node:path';
import type { PlatformTable } from '../contracts/types';
import { isValid } from '../contracts/validate';
import { fixture, gitRepo, readFixture, readJsonFixture, tempDir } from '../testing/helpers';
import { realExec } from '../util/exec';
import { runLookup } from './lookup';
import type { RenovateReport } from './mapReport';

const platforms = readJsonFixture<PlatformTable>('platform-feeds/platform-table.json');
const ymlFetcher = async (action: string, ref: string) => {
  const map: Record<string, string> = { v4: 'v4', v7: 'v5', v3: 'v3' };
  const file = fixture(`action-yml/${action.replace('/', '_')}/${map[ref] ?? ref}/action.yml`);
  return fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : null;
};

describe('runLookup (Renovate injected, everything else real)', () => {
  it('writes a schema-valid lookup.json with records, platform records and interface diffs', async () => {
    const { dir, sha } = gitRepo({}, fixture('renovate/docker-actions-platform/input'));
    const out = tempDir();
    const report = readJsonFixture<RenovateReport>('renovate/docker-actions-platform/report.json');
    const { document, file } = await runLookup({
      exec: realExec, repoDir: dir, outDir: out, workDir: tempDir(), expectedSha: sha, platforms, fetchActionYml: ymlFetcher,
      renovate: async () => ({ report, seconds: 21, log: '' }), now: () => new Date('2026-10-16T08:00:00Z'),
    });
    expect(isValid('lookup', JSON.parse(fs.readFileSync(file, 'utf8'))).errors).toBe('');
    expect(path.basename(file)).toBe('lookup.json');
    // 33 deps in the report; 7 platform locations whose only move is a channel move fold into their
    // platform record (R31, foldPlatform.test.ts).
    expect(document.records).toHaveLength(26);
    expect(document.platforms.map((p) => p.sdk).sort()).toEqual(['dotnet', 'go', 'java', 'node', 'python']);
    const dotnet = document.platforms.find((p) => p.sdk === 'dotnet')!;
    expect(dotnet.current.channels).toEqual(['8.0']);
    expect(dotnet.candidates.map((c) => c.channel)).toEqual(['11.0', '10.0', '9.0']);
    // Majors of actions with a captured pair get a diff (checkout v4 -> v7 is served the v5 file here).
    expect(document.interfaceDiffs.map((d) => d.action)).toEqual(expect.arrayContaining(['actions/checkout']));
    expect(document.engine.seconds).toBe(21);
  });

  it('refuses a checkout that is not expected_sha', async () => {
    const { dir } = gitRepo({ a: '1' });
    await expect(
      runLookup({ exec: realExec, repoDir: dir, outDir: tempDir(), workDir: tempDir(), expectedSha: 'f'.repeat(40), platforms, fetchActionYml: ymlFetcher, renovate: async () => ({ report: { repositories: {} }, seconds: 0, log: '' }) }),
    ).rejects.toThrow(/is not expected_sha/);
  });

  it('an sdk the table does not carry becomes a problem, not a failure (#606 D9)', async () => {
    const { dir, sha } = gitRepo({ '.nvmrc': '20\n' });
    const { document } = await runLookup({
      exec: realExec, repoDir: dir, outDir: tempDir(), workDir: tempDir(), expectedSha: sha,
      platforms: { sdks: {}, unstable: [] },
      fetchActionYml: ymlFetcher,
      renovate: async () => ({ report: JSON.parse(readFixture('renovate/npm/report.json')), seconds: 1, log: '' }),
    });
    expect(document.platforms).toEqual([expect.objectContaining({ sdk: 'node', source: 'none', candidates: [] })]);
    expect(document.problems).toEqual(expect.arrayContaining([expect.objectContaining({ source: 'platform:node' })]));
  });
});
