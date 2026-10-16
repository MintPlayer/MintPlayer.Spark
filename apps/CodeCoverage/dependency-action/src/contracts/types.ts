/**
 * TypeScript mirror of apps/CodeCoverage/contracts/dependency-update/*.schema.json.
 *
 * The schemas are the contract (the C# bot reads the same files). `types.test.ts` fails when a
 * schema's property set and the key lists below drift apart, which is the staleness check for these
 * hand-written types.
 */

export const CONTRACT_VERSION = 1 as const;

export type UpdateType =
  | 'major'
  | 'minor'
  | 'patch'
  | 'digest'
  | 'pin'
  | 'pinDigest'
  | 'lockfileUpdate'
  | 'rollback'
  | 'bump'
  | 'replacement'
  | null;

export interface Candidate {
  version: string | null;
  value: string | null;
  updateType: UpdateType;
  releaseTimestamp: string | null;
  digest: string | null;
  isBreaking: boolean;
  isLockfileUpdate: boolean;
  groupName: string | null;
  branchName: string | null;
  missing: Array<'version' | 'value' | 'updateType' | 'releaseTimestamp'>;
}

export interface LookupRecord {
  ecosystem: string;
  manager: string;
  datasource: string;
  packageFile: string;
  lockFiles: string[];
  depName: string;
  packageName: string | null;
  depType: string | null;
  currentValue: string | null;
  currentVersion: string | null;
  lockedVersion: string | null;
  currentDigest: string | null;
  replaceString: string | null;
  sharedVariableName: string | null;
  groupName: string | null;
  skipReason: string | null;
  hold: boolean;
  candidates: Candidate[];
}

export type InterfaceBlockerKind = 'removed-input-used' | 'new-required-input' | 'missing-output-read' | 'runtime-changed';

export interface InterfaceDiff {
  action: string;
  from: string;
  to: string;
  packageFiles: string[];
  runtime: { from: string | null; to: string | null };
  addedInputs: string[];
  removedInputs: string[];
  newRequiredInputs: string[];
  removedOutputs: string[];
  blockers: Array<{ kind: InterfaceBlockerKind; name: string; file: string }>;
  verdict: 'compatible' | 'incompatible' | 'unknown';
}

export type Sdk = 'dotnet' | 'node' | 'python' | 'java' | 'go';

export type PlatformLocationKind =
  | 'tfm'
  | 'global.json'
  | 'dotnet-tools'
  | 'docker'
  | 'setup-action'
  | 'version-file'
  | 'engines'
  | 'types-node'
  | 'tool-versions'
  | 'go.mod'
  | 'toolchain'
  | 'requires-python'
  | 'maven-release'
  | 'package-version';

export interface PlatformLocation {
  file: string;
  kind: PlatformLocationKind;
  value: string;
  channel: string | null;
  computed: boolean;
  reason: string | null;
}

export interface PlatformCandidate {
  channel: string;
  latestVersion: string | null;
  latestSdk?: string | null;
  releaseTimestamp: string | null;
  preview: boolean;
  eol: string | boolean | null;
  lts: boolean | null;
}

export interface PlatformRecord {
  contractVersion: typeof CONTRACT_VERSION;
  kind: 'platform';
  sdk: Sdk;
  source: 'releases-index' | 'endoflife.date' | 'none';
  current: { channels: string[]; preview: boolean };
  locations: PlatformLocation[];
  candidates: PlatformCandidate[];
}

export interface LookupDocument {
  contractVersion: typeof CONTRACT_VERSION;
  kind: 'lookup';
  expectedSha: string;
  generatedAt: string;
  engine: { name: 'renovate'; image: string; seconds?: number | null };
  records: LookupRecord[];
  platforms: PlatformRecord[];
  interfaceDiffs: InterfaceDiff[];
  problems: Array<{ source: string; message: string }>;
}

export interface PlanItem {
  depName: string;
  packageName?: string | null;
  ecosystem?: string;
  datasource: string;
  manager: string;
  depType?: string | null;
  packageFile: string;
  lockFiles: string[];
  currentValue: string;
  newValue: string;
  newVersion: string;
  currentDigest?: string | null;
  newDigest?: string | null;
  replaceString?: string | null;
  sharedVariableName?: string | null;
  updateType: string;
}

export interface PlatformPlan {
  sdk: Sdk;
  fromChannel: string;
  toChannel: string;
  toVersion?: string | null;
  sdkVersion?: string | null;
  strategy?: 'replace' | 'add';
  keepPrevious?: number;
  lockPackageMajor?: boolean;
  locations?: string[];
  exclude?: string[];
}

export interface PlanGroup {
  group: string;
  items: PlanItem[];
  platform?: PlatformPlan;
}

export interface Plan {
  contractVersion: typeof CONTRACT_VERSION;
  kind: 'plan';
  planId: string;
  expectedSha: string;
  options?: { feedSecrets?: boolean; consistencyCheck?: boolean };
  groups: PlanGroup[];
}

export interface Blocker {
  depName: string | null;
  version?: string | null;
  code: string;
  message: string;
}

export type GroupStatus = 'applied' | 'unresolvable' | 'failed';

export interface GroupResult {
  group: string;
  status: GroupStatus;
  items: Array<{ depName: string; packageFile: string; newVersion: string; adapter: string | null }>;
  files: string[];
  blockers: Blocker[];
  message: string | null;
  platform?: { edited: string[]; reported: Array<{ file: string; value: string; reason: string }> };
}

export interface ConsistencyCheck {
  adapter: string;
  directory: string;
  ok: boolean;
  message?: string | null;
}

export interface ApplyResult {
  contractVersion: typeof CONTRACT_VERSION;
  kind: 'apply-result';
  planId: string;
  expectedSha: string;
  groups: GroupResult[];
  consistency: { status: 'passed' | 'failed' | 'skipped'; checks: ConsistencyCheck[]; bisected: string[] };
  patch: { file: 'dependency.patch'; bytes: number; sha256: string };
}

export interface MigrateResult {
  contractVersion: typeof CONTRACT_VERSION;
  kind: 'migrate-result';
  tool: 'angular' | 'nx';
  baseSha: string;
  packages: Array<{ name: string; from: string; to: string }>;
  completed: true;
  steps: Array<{ name: string; exitCode: 0; seconds: number }>;
  extraBumps: Array<{ depName: string; packageFile: string; from: string | null; to: string | null }>;
  patch: { file: 'migrate.patch'; bytes: number; sha256: string };
}

/** Key lists checked against the schemas by types.test.ts (staleness check). */
export const KEYS = {
  lookup: ['contractVersion', 'kind', 'expectedSha', 'generatedAt', 'engine', 'records', 'platforms', 'interfaceDiffs', 'problems'],
  record: [
    'ecosystem', 'manager', 'datasource', 'packageFile', 'lockFiles', 'depName', 'packageName', 'depType', 'currentValue',
    'currentVersion', 'lockedVersion', 'currentDigest', 'replaceString', 'sharedVariableName', 'groupName', 'skipReason', 'hold', 'candidates',
  ],
  candidate: ['version', 'value', 'updateType', 'releaseTimestamp', 'digest', 'isBreaking', 'isLockfileUpdate', 'groupName', 'branchName', 'missing'],
  interfaceDiff: ['action', 'from', 'to', 'packageFiles', 'runtime', 'addedInputs', 'removedInputs', 'newRequiredInputs', 'removedOutputs', 'blockers', 'verdict'],
  platform: ['contractVersion', 'kind', 'sdk', 'source', 'current', 'locations', 'candidates'],
  plan: ['contractVersion', 'kind', 'planId', 'expectedSha', 'options', 'groups'],
  planItem: [
    'depName', 'packageName', 'ecosystem', 'datasource', 'manager', 'depType', 'packageFile', 'lockFiles', 'currentValue', 'newValue',
    'newVersion', 'currentDigest', 'newDigest', 'replaceString', 'sharedVariableName', 'updateType',
  ],
  platformPlan: ['sdk', 'fromChannel', 'toChannel', 'toVersion', 'sdkVersion', 'strategy', 'keepPrevious', 'lockPackageMajor', 'locations', 'exclude'],
  result: ['contractVersion', 'kind', 'planId', 'expectedSha', 'groups', 'consistency', 'patch'],
  groupResult: ['group', 'status', 'items', 'files', 'blockers', 'message', 'platform'],
  migrate: ['contractVersion', 'kind', 'tool', 'baseSha', 'packages', 'completed', 'steps', 'extraBumps', 'patch'],
} as const satisfies {
  lookup: readonly (keyof LookupDocument)[];
  record: readonly (keyof LookupRecord)[];
  candidate: readonly (keyof Candidate)[];
  interfaceDiff: readonly (keyof InterfaceDiff)[];
  platform: readonly (keyof PlatformRecord)[];
  plan: readonly (keyof Plan)[];
  planItem: readonly (keyof PlanItem)[];
  platformPlan: readonly (keyof PlatformPlan)[];
  result: readonly (keyof ApplyResult)[];
  groupResult: readonly (keyof GroupResult)[];
  migrate: readonly (keyof MigrateResult)[];
};
