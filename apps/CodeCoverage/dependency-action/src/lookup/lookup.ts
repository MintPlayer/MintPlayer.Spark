import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import type { LookupDocument, PlatformRecord, Sdk } from '../contracts/types';
import { CONTRACT_VERSION } from '../contracts/types';
import { assertValid } from '../contracts/validate';
import { buildPlatformRecord, fetchCandidates, type Fetcher } from '../platform/feeds';
import { scanLocations, type Found } from '../platform/locations';
import type { Exec } from '../util/exec';
import { revParse } from '../util/git';
import { directoryFiles, isWorkflowFile } from '../util/repoFiles';
import { findUsages, interfaceDiffsFor, type ActionUsage, type ActionYmlFetcher } from './interfaceDiff';
import { foldPlatformLocations } from './foldPlatform';
import { mapReport, reportProblems } from './mapReport';
import { RENOVATE_IMAGE, runRenovate, type RenovateRun } from './renovate';

export interface LookupOptions {
  exec: Exec;
  repoDir: string;
  outDir: string;
  workDir: string;
  expectedSha: string;
  fetcher: Fetcher;
  fetchActionYml: ActionYmlFetcher;
  githubTokenEnv?: string;
  hostRulesEnv?: string;
  /** Injected in tests; defaults to the pinned Docker image. */
  renovate?: () => Promise<RenovateRun>;
  now?: () => Date;
}

const SDKS: Sdk[] = ['dotnet', 'node', 'python', 'java', 'go'];

export async function platformRecords(repoDir: string, fetcher: Fetcher, problems: LookupDocument['problems'], now = new Date()): Promise<PlatformRecord[]> {
  return (await scanPlatforms(repoDir, fetcher, problems, now)).platforms;
}

async function scanPlatforms(repoDir: string, fetcher: Fetcher, problems: LookupDocument['problems'], now: Date): Promise<{ platforms: PlatformRecord[]; found: Found[] }> {
  const found = await scanLocations(directoryFiles(repoDir), SDKS);
  const bySdk = new Map<Sdk, Found[]>();
  for (const f of found) bySdk.set(f.sdk, [...(bySdk.get(f.sdk) ?? []), f]);
  const out: PlatformRecord[] = [];
  for (const sdk of SDKS) {
    const locations = bySdk.get(sdk);
    if (!locations?.length) continue;
    let feed = null;
    try {
      feed = await fetchCandidates(sdk, fetcher);
    } catch (err) {
      problems.push({ source: `platform:${sdk}`, message: String((err as Error).message ?? err) });
    }
    out.push(buildPlatformRecord(sdk, locations, feed, now));
  }
  return { platforms: out, found };
}

export async function workflowUsages(repoDir: string): Promise<Map<string, ActionUsage[]>> {
  const files = directoryFiles(repoDir);
  const map = new Map<string, ActionUsage[]>();
  for (const file of (await files.list()).filter(isWorkflowFile)) {
    map.set(file, findUsages(file, await files.read(file)));
  }
  return map;
}

/**
 * mode=lookup: Renovate (pinned by digest) -> records, plus platform records and Actions interface
 * diffs. Runs on the default-branch checkout at expected_sha and executes no repository code (S12).
 */
export async function runLookup(o: LookupOptions): Promise<{ document: LookupDocument; file: string }> {
  const head = await revParse(o.exec, o.repoDir, 'HEAD');
  if (head !== o.expectedSha) {
    throw new Error(`checked-out HEAD ${head} is not expected_sha ${o.expectedSha}`);
  }
  const run = o.renovate
    ? await o.renovate()
    : await runRenovate({ exec: o.exec, repoDir: o.repoDir, workDir: o.workDir, githubTokenEnv: o.githubTokenEnv, hostRulesEnv: o.hostRulesEnv });
  const problems = reportProblems(run.report);
  const { platforms, found } = await scanPlatforms(o.repoDir, o.fetcher, problems, o.now?.() ?? new Date());
  // R31: the platform record owns its locations; their channel moves are never offered separately.
  const { records } = foldPlatformLocations(mapReport(run.report), found, platforms);
  const interfaceDiffs = await interfaceDiffsFor(records, await workflowUsages(o.repoDir), o.fetchActionYml);
  const document: LookupDocument = {
    contractVersion: CONTRACT_VERSION,
    kind: 'lookup',
    expectedSha: o.expectedSha,
    generatedAt: (o.now?.() ?? new Date()).toISOString(),
    engine: { name: 'renovate', image: RENOVATE_IMAGE, seconds: run.seconds },
    records,
    platforms,
    interfaceDiffs,
    problems,
  };
  assertValid('lookup', document);
  await fs.mkdir(o.outDir, { recursive: true });
  const file = path.join(o.outDir, 'lookup.json');
  await fs.writeFile(file, JSON.stringify(document, null, 2) + '\n');
  return { document, file };
}
