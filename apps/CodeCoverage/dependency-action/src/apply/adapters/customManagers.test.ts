import * as fs from 'node:fs';
import * as path from 'node:path';
import type { PlanItem } from '../../contracts/types';
import { EditRefused } from '../../core/lineEdit';
import { mapReport, type RenovateReport } from '../../lookup/mapReport';
import { copyTree, ctxFor, fixture, item, readJsonFixture, tempDir } from '../../testing/helpers';
import { adapterFor } from '../registry';

/**
 * dependency-companion-pins-PRD S1 + S3 on a real lookup report (test/fixtures/renovate/custom-managers, SOURCE.md):
 * a renovate.json regex `customManagers` dependency is mapped and written by the existing replace-string adapter
 * (option B needs no new code in the action), and an `ARG`-expanded FROM is refused, never guessed.
 */
const records = mapReport(readJsonFixture<RenovateReport>('renovate/custom-managers/report.json'));
const playwright = (manager: string, packageFile: string) =>
  records.find((r) => r.manager === manager && r.packageFile === packageFile && r.depName === 'mcr.microsoft.com/playwright')!;

/** The plan item the planner makes of a record and its candidate with `newValue`. */
function planned(manager: string, packageFile: string, newValue: string): PlanItem {
  const r = playwright(manager, packageFile);
  expect(r.candidates.map((c) => c.value)).toContain(newValue);
  return item({ depName: r.depName, datasource: r.datasource, manager: r.manager, packageFile: r.packageFile, currentValue: r.currentValue!, newValue, replaceString: r.replaceString });
}

function sample(): string {
  const root = tempDir();
  copyTree(fixture('renovate/custom-managers/input'), root);
  return root;
}

const read = (root: string, file: string) => fs.readFileSync(path.join(root, file), 'utf8');

describe('Renovate customManagers (S1)', () => {
  it('maps a regex-manager dependency as a docker record with its replaceString and undecorated versions', () => {
    const core = playwright('regex', 'chromium/Dockerfile');
    expect(core).toMatchObject({ ecosystem: 'docker', datasource: 'docker', currentValue: '1.63.0', replaceString: 'playwright-core@1.63.0' });
    expect(core.candidates.map((c) => c.value)).toEqual(['1.64.1', '1.65.1']);
    expect(playwright('regex', 'docker-compose.yml').replaceString).toBe('seotester-chromium:1.63.0');
  });

  it('the image and its regex followers share one dependency name, so a name-keyed group holds all three', () => {
    const names = new Set(records.filter((r) => r.manager === 'regex' || r.packageFile === 'chromium/Dockerfile').map((r) => r.depName));
    expect([...names]).toEqual(['mcr.microsoft.com/playwright']);
  });

  it('writes every location of the group: the FROM tag, playwright-core and the own image tag', async () => {
    const root = sample();
    const group = [
      planned('dockerfile', 'chromium/Dockerfile', 'v1.64.1-noble'),
      planned('regex', 'chromium/Dockerfile', '1.64.1'),
      planned('regex', 'docker-compose.yml', '1.64.1'),
    ];
    const ctx = ctxFor(root, undefined, group);
    for (const i of group) await adapterFor(i)!.edit(ctx, i);

    expect(read(root, 'chromium/Dockerfile')).toBe(read(fixture('renovate/custom-managers/input'), 'chromium/Dockerfile')
      .replace('v1.63.0-noble', 'v1.64.1-noble').replace('playwright-core@1.63.0', 'playwright-core@1.64.1'));
    expect(read(root, 'docker-compose.yml')).toContain('ghcr.io/mintplayer/seotester-chromium:1.64.1');
  });
});

describe('a pin of another ecosystem than its file (F2, F4)', () => {
  it.each(['follow', 'regex'])('a %s record of a NuGet leader is written as text, never by the NuGet adapter', async (manager) => {
    const root = sample();
    const i = item({ depName: 'Microsoft.Playwright', datasource: 'nuget', manager, packageFile: 'chromium/Dockerfile', currentValue: '1.63.0', newValue: '1.64.1', replaceString: 'playwright-core@1.63.0' });
    const adapter = adapterFor(i)!;
    expect(adapter.id).toBe('replace-string');
    await adapter.edit(ctxFor(root, undefined, [i]), i);
    expect(read(root, 'chromium/Dockerfile')).toContain('playwright-core@1.64.1');
  });
});

describe('an ARG-expanded FROM (S3)', () => {
  it('is reported with the expanded text, which the file does not contain', () => {
    const arg = playwright('dockerfile', 'argstage/Dockerfile');
    expect(arg.replaceString).toBe('mcr.microsoft.com/playwright:v1.63.0-noble');
    expect(read(fixture('renovate/custom-managers/input'), 'argstage/Dockerfile')).not.toContain(arg.replaceString);
  });

  it('is refused rather than guessed, and the file stays as it was', async () => {
    const root = sample();
    const before = read(root, 'argstage/Dockerfile');
    const i = planned('dockerfile', 'argstage/Dockerfile', 'v1.64.1-noble');
    await expect(adapterFor(i)!.edit(ctxFor(root, undefined, [i]), i)).rejects.toBeInstanceOf(EditRefused);
    expect(read(root, 'argstage/Dockerfile')).toBe(before);
  });
});
