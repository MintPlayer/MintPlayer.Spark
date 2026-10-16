import * as fs from 'node:fs';
import * as path from 'node:path';
import type { LookupDocument } from '../contracts/types';
import { isValid } from '../contracts/validate';
import { CONTRACTS, FIXTURES, readJsonFixture } from '../testing/helpers';
import { ecosystemOf, mapReport, reportProblems, type RenovateReport } from './mapReport';

const SAMPLES = fs.readdirSync(path.join(FIXTURES, 'renovate'));
const report = (s: string) => readJsonFixture<RenovateReport>(`renovate/${s}/report.json`);
const golden = (s: string) => JSON.parse(fs.readFileSync(path.join(CONTRACTS, 'fixtures/valid', `lookup.${s}.json`), 'utf8')) as LookupDocument;

function asLookup(records: ReturnType<typeof mapReport>): LookupDocument {
  return {
    contractVersion: 1, kind: 'lookup', expectedSha: '0'.repeat(40), generatedAt: 'x',
    engine: { name: 'renovate', image: `renovate/renovate:44.125.2@sha256:${'a'.repeat(64)}` }, records, platforms: [], interfaceDiffs: [], problems: [],
  };
}

describe('mapReport on the real S12 reports', () => {
  it.each(['npm', 'nuget', 'docker-actions-platform', 'maven', 'go'])('%s matches the golden records (shared contract fixture)', (sample) => {
    expect(mapReport(report(sample))).toEqual(golden(sample).records);
  });

  it.each(SAMPLES)('%s maps every dep to a schema-valid record', (sample) => {
    const r = report(sample);
    const records = mapReport(r);
    const deps = Object.values(r.repositories.local.packageFiles ?? {}).flat().flatMap((f) => f.deps).filter((d) => d.depName);
    expect(records).toHaveLength(deps.length);
    expect(isValid('lookup', asLookup(records)).errors).toBe('');
  });

  it('maps this repo (504 deps) without dropping one', () => {
    expect(mapReport(report('mintplayer-spark'))).toHaveLength(504);
  });

  it('keeps a candidate with no updateType and makes the record a hold (R1)', () => {
    const r = report('npm');
    const dep = Object.values(r.repositories.local.packageFiles!).flat().flatMap((f) => f.deps).find((d) => d.updates?.length)!;
    delete dep.updates![0].updateType;
    const rec = mapReport(r).find((x) => x.depName === dep.depName)!;
    expect(rec.hold).toBe(true);
    expect(rec.candidates[0].updateType).toBeNull();
    expect(rec.candidates[0].missing).toContain('updateType');
  });

  it('marks a missing releaseTimestamp without holding (D-S12a: the bot counts from first-seen)', () => {
    const recs = mapReport(report('docker-actions-platform'));
    const ubuntu = recs.find((r) => r.depName === 'ubuntu' && r.datasource === 'docker')!;
    expect(ubuntu.hold).toBe(false);
    const missing = recs.flatMap((r) => r.candidates).filter((c) => c.releaseTimestamp === null);
    expect(missing.length).toBeGreaterThan(0);
    for (const c of missing) expect(c.missing).toContain('releaseTimestamp');
  });

  it('passes a Docker digest and a Go pseudo-version through as strings, never as versions', () => {
    const docker = mapReport(report('docker-actions-platform')).find((r) => r.depName === 'python' && r.datasource === 'docker' && r.currentDigest)!;
    expect(docker.candidates.some((c) => c.updateType === 'digest' && c.digest?.startsWith('sha256:'))).toBe(true);
    const go = mapReport(report('go')).find((r) => r.depName === 'golang.org/x/exp')!;
    expect(go.candidates[0].updateType).toBe('digest');
    expect(go.candidates[0].value).toMatch(/^v0\.0\.0-\d{14}-[0-9a-f]{12}$/);
  });

  it('keeps 0.x minor labels as Renovate reports them (the bot applies the R17 floor)', () => {
    const rand = mapReport(report('cargo-locked')).find((r) => r.depName === 'rand')!;
    expect(rand.candidates[0]).toMatchObject({ updateType: 'minor', value: '0.10' });
  });

  it('carries lockFiles repo-relative and the manager next to the datasource (D-S12d)', () => {
    const recs = mapReport(report('mintplayer-spark'));
    const ws = recs.find((r) => r.manager === 'npm' && r.packageFile === 'apps/CodeCoverage/CodeCoverage/ClientApp/package.json')!;
    expect(ws.lockFiles).toEqual(['package-lock.json']);
    const uv = mapReport(report('uv'));
    expect(uv.find((r) => r.manager === 'poetry')?.ecosystem).toBe('pypi');
  });

  it('copies sharedVariableName for Maven properties and Gradle catalog refs', () => {
    expect(mapReport(report('maven')).find((r) => r.depName === 'com.google.guava:guava')?.sharedVariableName).toBe('guava.version');
    expect(mapReport(report('gradle')).find((r) => r.packageFile === 'gradle/libs.versions.toml')?.sharedVariableName).toBe('jackson');
  });

  it('reads groupName from the update first, then the dep (D-C1)', () => {
    const r = report('npm');
    const deps = Object.values(r.repositories.local.packageFiles!).flat().flatMap((f) => f.deps).filter((d) => d.updates?.length);
    deps[0].groupName = 'dep-level';
    deps[0].updates![0].groupName = 'update-level';
    deps[1].groupName = 'only-dep';
    const recs = mapReport(r);
    expect(recs.find((x) => x.depName === deps[0].depName)!.candidates[0].groupName).toBe('update-level');
    expect(recs.find((x) => x.depName === deps[0].depName)!.groupName).toBe('dep-level');
    expect(recs.find((x) => x.depName === deps[1].depName)!.candidates[0].groupName).toBe('only-dep');
  });

  it('surfaces Renovate problems (level >= 40) once each', () => {
    const problems = reportProblems(report('npm'));
    expect(problems).toHaveLength(1);
    expect(problems[0].message).toMatch(/does not support local presets/);
  });
});

describe('ecosystemOf', () => {
  it.each([
    ['github-actions', 'github-tags', 'actions/checkout', 'github-actions'],
    ['github-actions', 'github-releases', 'node', 'platform:node'],
    ['github-actions', 'dotnet-version', 'dotnet-sdk', 'platform:dotnet'],
    ['github-actions', 'github-runners', 'ubuntu', 'github-runners'],
    ['nvm', 'node-version', 'node', 'platform:node'],
    ['pyenv', 'docker', 'python', 'platform:python'],
    ['asdf', 'github-tags', 'golang', 'platform:go'],
    ['composer', 'github-tags', 'php', 'github-tags'],
    ['cargo', 'crate', 'serde', 'cargo'],
    ['gradle', 'maven', 'junit:junit', 'maven'],
    ['bundler', 'rubygems', 'rails', 'rubygems'],
    ['weird', 'some-new-datasource', 'x', 'some-new-datasource'],
  ])('%s/%s/%s -> %s', (manager, datasource, depName, expected) => {
    expect(ecosystemOf(manager, datasource, depName)).toBe(expected);
  });
});
