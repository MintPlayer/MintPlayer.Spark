import * as fs from 'node:fs';
import * as path from 'node:path';
import type { Plan } from '../contracts/types';
import { isValid } from '../contracts/validate';
import { commitAll, gitRepo, gitSync, item, tempDir, writeTree } from '../testing/helpers';
import { realExec } from '../util/exec';
import { PLAN_PATH } from '../util/git';
import { readPlan, runApply } from './apply';

function planFor(sha: string, groups: Plan['groups']): Plan {
  return { contractVersion: 1, kind: 'plan', planId: 'plan-1', expectedSha: sha, groups };
}

/** expected_sha + one commit adding the plan (R14). */
function dispatchRef(files: Record<string, string>, groups: (sha: string) => Plan['groups']) {
  const { dir, sha } = gitRepo(files);
  writeTree(dir, { [PLAN_PATH]: JSON.stringify(planFor(sha, groups(sha)), null, 2) });
  commitAll(dir, 'plan');
  return { dir, sha };
}

const dockerItem = item({
  depName: 'node', datasource: 'docker', manager: 'dockerfile', packageFile: 'Dockerfile', currentValue: '20.11.0-alpine', newValue: '20.11.1-alpine', replaceString: 'node:20.11.0-alpine',
});

describe('readPlan (R14 provenance, runner side)', () => {
  it('reads and validates the plan on expected_sha + the plan commit', async () => {
    const { dir, sha } = dispatchRef({ Dockerfile: 'FROM node:20.11.0-alpine\n' }, () => [{ group: 'node', items: [dockerItem] }]);
    const plan = await readPlan(realExec, dir, sha);
    expect(plan.groups[0].group).toBe('node');
  });

  it('refuses when HEAD^ is not expected_sha', async () => {
    const { dir, sha } = dispatchRef({ Dockerfile: 'FROM node:20.11.0-alpine\n' }, () => []);
    writeTree(dir, { 'other.txt': 'x' });
    commitAll(dir, 'someone pushed');
    await expect(readPlan(realExec, dir, sha)).rejects.toThrow(/HEAD\^ is .* not expected_sha/);
  });

  it('refuses a plan commit that touches anything else (e.g. .github)', async () => {
    const { dir, sha } = gitRepo({ Dockerfile: 'FROM node:20.11.0-alpine\n' });
    writeTree(dir, { [PLAN_PATH]: JSON.stringify(planFor(sha, [])), '.github/workflows/x.yml': 'on: push\n' });
    commitAll(dir, 'plan + workflow');
    await expect(readPlan(realExec, dir, sha)).rejects.toThrow(/must change \.mintplayer\/dependency-plan\.json only/);
  });

  it('refuses a plan whose expectedSha differs from the input', async () => {
    const { dir, sha } = gitRepo({ a: '1' });
    writeTree(dir, { [PLAN_PATH]: JSON.stringify(planFor('f'.repeat(40), [])) });
    commitAll(dir, 'plan');
    await expect(readPlan(realExec, dir, sha)).rejects.toThrow(/plan\.expectedSha/);
  });

  it('refuses a plan that breaks the contract', async () => {
    const { dir, sha } = gitRepo({ a: '1' });
    writeTree(dir, { [PLAN_PATH]: JSON.stringify({ ...planFor(sha, []), groups: [{ group: 'g', items: [{ ...dockerItem, packageFile: '../escape' }] }] }) });
    commitAll(dir, 'plan');
    await expect(readPlan(realExec, dir, sha)).rejects.toThrow(/plan does not match its contract/);
  });
});

describe('runApply', () => {
  it('writes a schema-valid result.json and a git diff --binary patch without the plan file', async () => {
    const { dir, sha } = dispatchRef({ Dockerfile: 'FROM node:20.11.0-alpine AS build\nRUN true\n' }, () => [
      { group: 'node', items: [dockerItem] },
      { group: 'missing', items: [item({ depName: 'rails', datasource: 'rubygems', packageFile: 'Gemfile', currentValue: '7.0.0', newValue: '7.1.0' })] },
    ]);
    const out = tempDir();
    const { result, resultFile, patchFile } = await runApply({ exec: realExec, repoDir: dir, outDir: out, expectedSha: sha, feedSecrets: false });
    expect(isValid('result', JSON.parse(fs.readFileSync(resultFile, 'utf8'))).errors).toBe('');
    expect(result.groups.map((g) => g.status)).toEqual(['applied', 'failed']);
    const patch = fs.readFileSync(patchFile, 'utf8');
    expect(patch).toContain('-FROM node:20.11.0-alpine AS build');
    expect(patch).toContain('+FROM node:20.11.1-alpine AS build');
    expect(patch).not.toContain('dependency-plan.json');
    expect(result.patch.bytes).toBe(fs.statSync(patchFile).size);
    // The patch applies to expected_sha, which is what the git writer does with it.
    const check = gitRepo({ Dockerfile: 'FROM node:20.11.0-alpine AS build\nRUN true\n' });
    fs.copyFileSync(patchFile, path.join(check.dir, 'p.patch'));
    gitSync(check.dir, 'apply', '--check', 'p.patch');
  });

  it('includes a new untracked lockfile in the patch', async () => {
    const { dir, sha } = dispatchRef({ 'go.mod': 'module x\n\ngo 1.22\n\nrequire example.com/m v1.0.0\n' }, () => [
      { group: 'm', items: [item({ depName: 'example.com/m', datasource: 'go', manager: 'gomod', packageFile: 'go.mod', currentValue: 'v1.0.0', newValue: 'v1.1.0' })] },
    ]);
    const out = tempDir();
    const exec: typeof realExec = async (cmd, args, o) => {
      if (cmd === 'go' && args[0] === 'mod' && args[1] === 'tidy') {
        fs.writeFileSync(path.join(o.cwd, 'go.sum'), 'example.com/m v1.1.0 h1:abc=\n');
        return { code: 0, stdout: '', stderr: '' };
      }
      if (cmd === 'go') return { code: 0, stdout: 'go version go1.25', stderr: '' };
      return realExec(cmd, args, o);
    };
    const { result, patchFile } = await runApply({ exec, repoDir: dir, outDir: out, expectedSha: sha, feedSecrets: false, orchestrator: { consistencyCheck: false } });
    expect(result.groups[0]).toMatchObject({ status: 'applied', files: ['go.mod', 'go.sum'] });
    expect(fs.readFileSync(patchFile, 'utf8')).toContain('new file mode');
  });
});
