import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import type { ApplyResult, Plan } from '../contracts/types';
import { CONTRACT_VERSION } from '../contracts/types';
import { assertValid } from '../contracts/validate';
import type { Exec } from '../util/exec';
import { PLAN_PATH, diffBinary, git, revParse, sha256 } from '../util/git';
import { applyPlan, type OrchestratorOptions, type SdkPreset } from './orchestrator';
import { GLOBAL_JSON, presetRefusal } from './sdkPreset';

export interface ApplyOptions {
  exec: Exec;
  repoDir: string;
  outDir: string;
  expectedSha: string;
  feedSecrets: boolean;
  log?: (message: string) => void;
  orchestrator?: Partial<OrchestratorOptions>;
}

/**
 * R14: the dispatch ref is expected_sha + ONE commit that adds the plan file and, #658, at most moves the
 * root global.json's sdk.version to the plan's SDK (so the workflow's setup-dotnet installed it). Checked
 * here as well as bot-side, so a ref somebody else pushed to is refused before any tool runs.
 */
export async function readDispatch(exec: Exec, repoDir: string, expectedSha: string): Promise<{ plan: Plan; sdkPreset: SdkPreset | null }> {
  const parent = await revParse(exec, repoDir, 'HEAD^');
  if (parent !== expectedSha) throw new Error(`HEAD^ is ${parent}, not expected_sha ${expectedSha}: the dispatch ref must be expected_sha + the plan commit`);
  const touched = (await git(exec, repoDir, ['-c', 'core.quotepath=false', 'diff', '--name-only', 'HEAD^', 'HEAD'])).split('\n').map((s) => s.trim()).filter(Boolean);
  if (!touched.includes(PLAN_PATH) || touched.some((p) => p !== PLAN_PATH && p !== GLOBAL_JSON)) {
    throw new Error(`the plan commit must change ${PLAN_PATH} only (and the root ${GLOBAL_JSON}'s sdk.version); it changes: ${touched.join(', ') || '(nothing)'}`);
  }
  const plan = assertValid<Plan>('plan', JSON.parse(await fs.readFile(path.join(repoDir, PLAN_PATH), 'utf8')));
  if (plan.expectedSha !== expectedSha) throw new Error(`plan.expectedSha ${plan.expectedSha} is not the expected_sha input ${expectedSha}`);
  if (!touched.includes(GLOBAL_JSON)) return { plan, sdkPreset: null };
  const before = await git(exec, repoDir, ['show', `HEAD^:${GLOBAL_JSON}`]).catch(() => null);
  if (before === null) throw new Error(`the plan commit may only modify an existing ${GLOBAL_JSON}`);
  const after = await git(exec, repoDir, ['show', `HEAD:${GLOBAL_JSON}`]);
  const refusal = presetRefusal(plan, before, after);
  if (refusal) throw new Error(`the plan commit's ${GLOBAL_JSON} is not the bot's SDK pre-set: ${refusal}`);
  return { plan, sdkPreset: { file: GLOBAL_JSON, original: before } };
}

export async function readPlan(exec: Exec, repoDir: string, expectedSha: string): Promise<Plan> {
  return (await readDispatch(exec, repoDir, expectedSha)).plan;
}

/**
 * mode=apply: read the plan from the checked-out dispatch ref, apply its groups (edit -> pin -> relock
 * -> verify, reset on failure), check the whole tree, and write result.json + dependency.patch (a
 * `git diff --binary` against expected_sha, plan file excluded). Never builds (D-S14a), never pushes.
 */
export async function runApply(o: ApplyOptions): Promise<{ result: ApplyResult; resultFile: string; patchFile: string }> {
  const { plan, sdkPreset } = await readDispatch(o.exec, o.repoDir, o.expectedSha);
  const { groups, consistency } = await applyPlan(plan, {
    root: o.repoDir,
    exec: o.exec,
    feedSecrets: o.feedSecrets || plan.options?.feedSecrets === true,
    consistencyCheck: true,
    log: o.log,
    sdkPreset: sdkPreset ?? undefined,
    ...o.orchestrator,
  });
  // #658: the pre-set SDK is the platform group's edit; without that group in the patch, global.json stays as planned on.
  if (sdkPreset) {
    const owner = plan.groups.find((g) => g.platform?.sdk === 'dotnet' && !!g.platform.sdkVersion)?.group;
    if (groups.find((g) => g.group === owner)?.status !== 'applied') await fs.writeFile(path.join(o.repoDir, sdkPreset.file), sdkPreset.original);
  }
  await fs.mkdir(o.outDir, { recursive: true });
  const patchFile = path.join(o.outDir, 'dependency.patch');
  const patch = await diffBinary(o.exec, o.repoDir, o.expectedSha, patchFile);
  const result: ApplyResult = {
    contractVersion: CONTRACT_VERSION,
    kind: 'apply-result',
    planId: plan.planId,
    expectedSha: o.expectedSha,
    groups,
    consistency,
    patch: { file: 'dependency.patch', bytes: patch.length, sha256: sha256(patch) },
  };
  assertValid('result', result);
  const resultFile = path.join(o.outDir, 'result.json');
  await fs.writeFile(resultFile, JSON.stringify(result, null, 2) + '\n');
  return { result, resultFile, patchFile };
}
