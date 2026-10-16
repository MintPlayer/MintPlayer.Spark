import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { isValid } from './contracts/validate';
import { commitAll, gitRepo, tempDir, writeTree } from './testing/helpers';

/**
 * Runs the COMMITTED bundle (dist/index.js, what `runs.main` executes) as a child process. The unit
 * tests import src/, so they all pass against a bundle built from the wrong entry point or never
 * rebuilt; only this catches it. The apply case needs no package manager: a Dockerfile tag is a text
 * edit (replace-string adapter).
 */
const DIST = path.resolve(__dirname, '../dist/index.js');

function runBundle(env: Record<string, string>) {
  const runnerTemp = tempDir();
  const outputFile = path.join(runnerTemp, 'github-output');
  fs.writeFileSync(outputFile, '');
  const r = spawnSync(process.execPath, [DIST], {
    env: { PATH: process.env.PATH ?? '', HOME: process.env.HOME ?? '', USERPROFILE: process.env.USERPROFILE ?? '', SystemRoot: process.env.SystemRoot ?? '', RUNNER_TEMP: runnerTemp, GITHUB_OUTPUT: outputFile, ...env },
    encoding: 'utf8',
    timeout: 60_000,
  });
  return { ...r, runnerTemp, outputs: fs.readFileSync(outputFile, 'utf8') };
}

describe('dist/index.js', () => {
  it('apply: reads the plan from the dispatch ref and writes result.json + dependency.patch', () => {
    const { dir, sha } = gitRepo({ Dockerfile: 'FROM node:20.11.0-alpine AS build\n' });
    const plan = {
      contractVersion: 1, kind: 'plan', planId: 'bundle', expectedSha: sha,
      groups: [{
        group: 'node',
        items: [{
          depName: 'node', datasource: 'docker', manager: 'dockerfile', packageFile: 'Dockerfile', lockFiles: [], currentValue: '20.11.0-alpine',
          newValue: '20.11.1-alpine', newVersion: '20.11.1', replaceString: 'node:20.11.0-alpine', updateType: 'patch',
        }],
      }],
    };
    writeTree(dir, { '.mintplayer/dependency-plan.json': JSON.stringify(plan) });
    commitAll(dir, 'plan');
    const r = runBundle({ 'INPUT_MODE': 'apply', 'INPUT_EXPECTED-SHA': sha, GITHUB_WORKSPACE: dir });
    expect(r.status).toBe(0);
    const outDir = path.join(r.runnerTemp, 'dependency-update');
    expect(r.outputs).toContain('output-dir');
    const result = JSON.parse(fs.readFileSync(path.join(outDir, 'result.json'), 'utf8'));
    expect(isValid('result', result).errors).toBe('');
    expect(result.groups[0].status).toBe('applied');
    expect(fs.readFileSync(path.join(outDir, 'dependency.patch'), 'utf8')).toContain('+FROM node:20.11.1-alpine AS build');
  });

  it('fails the step (exit 1, ::error::) on a bad mode', () => {
    const r = runBundle({ 'INPUT_MODE': 'push', GITHUB_WORKSPACE: tempDir() });
    expect(r.status).toBe(1);
    expect(r.stdout).toContain('::error::');
    expect(r.stdout).toContain('mode must be lookup, apply or migrate');
  });
});
