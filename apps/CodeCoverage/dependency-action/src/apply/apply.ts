import { promises as fs } from 'node:fs';
import * as path from 'node:path';
import type { ApplyResult, Plan } from '../contracts/types';
import { CONTRACT_VERSION } from '../contracts/types';
import { assertValid } from '../contracts/validate';
import type { Exec } from '../util/exec';
import { PLAN_PATH, diffBinary, git, revParse, sha256 } from '../util/git';
import { applyPlan, type OrchestratorOptions } from './orchestrator';

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
 * R14: the dispatch ref is expected_sha + ONE commit that adds only the plan file. Checked here as
 * well as bot-side, so a ref somebody else pushed to is refused before any tool runs.
 */
export async function readPlan(exec: Exec, repoDir: string, expectedSha: string): Promise<Plan> {
  const parent = await revParse(exec, repoDir, 'HEAD^');
  if (parent !== expectedSha) throw new Error(`HEAD^ is ${parent}, not expected_sha ${expectedSha}: the dispatch ref must be expected_sha + the plan commit`);
  const touched = (await git(exec, repoDir, ['-c', 'core.quotepath=false', 'diff', '--name-only', 'HEAD^', 'HEAD'])).split('\n').map((s) => s.trim()).filter(Boolean);
  if (touched.length !== 1 || touched[0] !== PLAN_PATH) {
    throw new Error(`the plan commit must change ${PLAN_PATH} only; it changes: ${touched.join(', ') || '(nothing)'}`);
  }
  const plan = assertValid<Plan>('plan', JSON.parse(await fs.readFile(path.join(repoDir, PLAN_PATH), 'utf8')));
  if (plan.expectedSha !== expectedSha) throw new Error(`plan.expectedSha ${plan.expectedSha} is not the expected_sha input ${expectedSha}`);
  return plan;
}

/**
 * mode=apply: read the plan from the checked-out dispatch ref, apply its groups (edit -> pin -> relock
 * -> verify, reset on failure), check the whole tree, and write result.json + dependency.patch (a
 * `git diff --binary` against expected_sha, plan file excluded). Never builds (D-S14a), never pushes.
 */
export async function runApply(o: ApplyOptions): Promise<{ result: ApplyResult; resultFile: string; patchFile: string }> {
  const plan = await readPlan(o.exec, o.repoDir, o.expectedSha);
  const { groups, consistency } = await applyPlan(plan, {
    root: o.repoDir,
    exec: o.exec,
    feedSecrets: o.feedSecrets || plan.options?.feedSecrets === true,
    consistencyCheck: true,
    log: o.log,
    ...o.orchestrator,
  });
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
