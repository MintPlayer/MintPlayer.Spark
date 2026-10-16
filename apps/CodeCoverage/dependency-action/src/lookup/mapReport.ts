import type { Candidate, LookupRecord, UpdateType } from '../contracts/types';

/**
 * Renovate `--report-type=file` shape, as captured in S12 (renovate 44.125.2). Only the fields the
 * mapping reads are typed; everything else is ignored.
 */
export interface RenovateUpdate {
  newVersion?: string;
  newValue?: string;
  newDigest?: string;
  updateType?: string;
  releaseTimestamp?: string;
  isBreaking?: boolean;
  isLockfileUpdate?: boolean;
  groupName?: string;
  branchName?: string;
}

export interface RenovateDep {
  depName?: string;
  packageName?: string;
  datasource?: string;
  depType?: string;
  currentValue?: string;
  currentVersion?: string;
  lockedVersion?: string | null;
  currentDigest?: string;
  replaceString?: string;
  sharedVariableName?: string;
  groupName?: string;
  skipReason?: string;
  updates?: RenovateUpdate[];
}

export interface RenovatePackageFile {
  packageFile: string;
  lockFiles?: string[];
  datasource?: string;
  deps: RenovateDep[];
}

export interface RenovateProblem {
  level?: number;
  msg?: string;
  err?: { message?: string };
}

export interface RenovateReport {
  problems?: RenovateProblem[];
  repositories: Record<string, { problems?: RenovateProblem[]; packageFiles?: Record<string, RenovatePackageFile[]> }>;
}

const UPDATE_TYPES = new Set(['major', 'minor', 'patch', 'digest', 'pin', 'pinDigest', 'lockfileUpdate', 'rollback', 'bump', 'replacement']);

const DATASOURCE_ECOSYSTEM: Record<string, string> = {
  npm: 'npm',
  nuget: 'nuget',
  pypi: 'pypi',
  maven: 'maven',
  crate: 'cargo',
  go: 'go',
  packagist: 'composer',
  docker: 'docker',
  rubygems: 'rubygems',
  'dotnet-version': 'platform:dotnet',
  'node-version': 'platform:node',
  'python-version': 'platform:python',
  'java-version': 'platform:java',
  'golang-version': 'platform:go',
  'rust-version': 'platform:rust',
  'gradle-version': 'gradle-wrapper',
  'github-runners': 'github-runners',
};

/**
 * The adapter family. D-S12d: apply adapters key on datasource (+ lockFiles), never on the manager
 * name (pyproject without uv.lock reports as `poetry`). The one exception is the github-* datasources,
 * which mean "an Actions `uses:`" only under the github-actions manager.
 */
const PLATFORM_DEP_NAMES: Record<string, string> = {
  node: 'platform:node',
  nodejs: 'platform:node',
  python: 'platform:python',
  go: 'platform:go',
  golang: 'platform:go',
  java: 'platform:java',
  'java-jdk': 'platform:java',
  'dotnet-sdk': 'platform:dotnet',
};

const PLATFORM_MANAGERS: Record<string, string> = {
  nvm: 'platform:node',
  nodenv: 'platform:node',
  pyenv: 'platform:python',
};

export function ecosystemOf(manager: string, datasource: string, depName = ''): string {
  if (PLATFORM_MANAGERS[manager]) return PLATFORM_MANAGERS[manager];
  if (datasource.startsWith('github-') && datasource !== 'github-runners') {
    // A `uses: owner/repo@ref` names a repository; a bare name under github-releases/-tags is a
    // setup-* `with:` version or an asdf tool (S12: setup-node's node-version is github-releases "node").
    if (manager === 'github-actions' && depName.includes('/')) return 'github-actions';
    if (PLATFORM_DEP_NAMES[depName]) return PLATFORM_DEP_NAMES[depName];
  }
  if (manager === 'asdf' && PLATFORM_DEP_NAMES[depName]) return PLATFORM_DEP_NAMES[depName];
  return DATASOURCE_ECOSYSTEM[datasource] ?? datasource;
}

function str(v: unknown): string | null {
  return typeof v === 'string' && v.length > 0 ? v : null;
}

export function mapCandidate(update: RenovateUpdate, dep: RenovateDep): Candidate {
  const missing: Candidate['missing'] = [];
  const version = str(update.newVersion);
  const value = str(update.newValue);
  const updateType = update.updateType && UPDATE_TYPES.has(update.updateType) ? (update.updateType as UpdateType) : null;
  const releaseTimestamp = str(update.releaseTimestamp);
  if (version === null) missing.push('version');
  if (value === null && !update.newDigest) missing.push('value');
  if (updateType === null) missing.push('updateType');
  if (releaseTimestamp === null) missing.push('releaseTimestamp');
  return {
    version,
    value,
    updateType,
    releaseTimestamp,
    digest: str(update.newDigest),
    isBreaking: update.isBreaking === true,
    isLockfileUpdate: update.isLockfileUpdate === true,
    groupName: str(update.groupName) ?? str(dep.groupName),
    branchName: str(update.branchName),
    missing,
  };
}

/**
 * Maps a Renovate report to lookup records (§13.2 + groupName, D-C1). Every dep becomes a record, with
 * or without candidates (P12: the graph ingests the whole report). A candidate missing a field is kept
 * and marked, never dropped (R1); a missing updateType makes the record a hold.
 */
export function mapReport(report: RenovateReport): LookupRecord[] {
  const records: LookupRecord[] = [];
  for (const repo of Object.values(report.repositories ?? {})) {
    for (const [manager, files] of Object.entries(repo.packageFiles ?? {})) {
      for (const file of files) {
        for (const dep of file.deps ?? []) {
          const depName = str(dep.depName);
          if (!depName) continue;
          const datasource = str(dep.datasource) ?? str(file.datasource) ?? 'unknown';
          const candidates = (dep.updates ?? []).map((u) => mapCandidate(u, dep));
          records.push({
            ecosystem: ecosystemOf(manager, datasource, depName),
            manager,
            datasource,
            packageFile: file.packageFile,
            lockFiles: [...(file.lockFiles ?? [])],
            depName,
            packageName: str(dep.packageName),
            depType: str(dep.depType),
            currentValue: str(dep.currentValue),
            currentVersion: str(dep.currentVersion),
            lockedVersion: str(dep.lockedVersion),
            currentDigest: str(dep.currentDigest),
            replaceString: str(dep.replaceString),
            sharedVariableName: str(dep.sharedVariableName),
            groupName: str(dep.groupName),
            skipReason: str(dep.skipReason),
            hold: candidates.some((c) => c.updateType === null),
            candidates,
          });
        }
      }
    }
  }
  return records;
}

export function reportProblems(report: RenovateReport): Array<{ source: string; message: string }> {
  const out: Array<{ source: string; message: string }> = [];
  const add = (p: RenovateProblem) => {
    if ((p.level ?? 0) < 40) return;
    const message = [p.msg, p.err?.message].filter(Boolean).join(': ');
    if (message && !out.some((o) => o.message === message)) out.push({ source: 'renovate', message });
  };
  for (const p of report.problems ?? []) add(p);
  for (const repo of Object.values(report.repositories ?? {})) for (const p of repo.problems ?? []) add(p);
  return out;
}
