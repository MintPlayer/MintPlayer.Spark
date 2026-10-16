import * as fs from 'node:fs';
import * as path from 'node:path';
import { parse } from 'yaml';

/**
 * T5 invariants on the shipped template callers (R12, R16, §13.11.2). The C# DependencyWorkflowGuardTests
 * read the same files from disk; this is the runner-side half.
 */
const DIR = path.resolve(__dirname, '../templates');
const TEMPLATES = ['dependency-lookup.yml', 'dependency-apply.yml', 'angular-migrate.yml', 'nx-migrate.yml'];
const ACTION_REF = /^MintPlayer\/MintPlayer\.Spark\/apps\/CodeCoverage\/dependency-action@(dependency-update-v1|[0-9a-f]{40})$/;

interface Step {
  uses?: string;
  run?: string;
  if?: string;
  with?: Record<string, unknown>;
  env?: Record<string, unknown>;
}
interface Workflow {
  on: Record<string, unknown>;
  permissions: Record<string, string>;
  jobs: Record<string, { permissions?: unknown; steps: Step[]; 'timeout-minutes'?: number; environment?: unknown; 'runs-on': string }>;
}

const raw = (f: string) => fs.readFileSync(path.join(DIR, f), 'utf8');
const load = (f: string) => parse(raw(f)) as Workflow;
/** The file without comment lines: what GitHub executes. */
const code = (f: string) => raw(f).split('\n').filter((l) => !/^\s*#/.test(l)).map((l) => l.replace(/\s+#.*$/, '')).join('\n');
const steps = (wf: Workflow) => Object.values(wf.jobs).flatMap((j) => j.steps);

describe.each(TEMPLATES)('%s', (file) => {
  const wf = load(file);

  it('is dispatch-only (no pull_request_target, no push trigger)', () => {
    expect(Object.keys(wf.on)).toEqual(['workflow_dispatch']);
  });

  it('has exactly `permissions: contents: read` at the top, and no job widens it', () => {
    expect(wf.permissions).toEqual({ contents: 'read' });
    for (const job of Object.values(wf.jobs)) expect(job.permissions).toBeUndefined();
  });

  it('never asks for id-token, write access, an environment or a self-hosted runner', () => {
    expect(code(file)).not.toMatch(/id-token|:\s*write\b|environment:/);
    for (const job of Object.values(wf.jobs)) {
      expect(job['runs-on']).toBe('ubuntu-latest');
      expect(job['timeout-minutes']).toBeGreaterThan(0);
    }
  });

  it('checks out with persist-credentials: false', () => {
    const checkouts = steps(wf).filter((s) => s.uses?.startsWith('actions/checkout@'));
    expect(checkouts).toHaveLength(1);
    expect(checkouts[0].with?.['persist-credentials']).toBe(false);
  });

  it('calls the versioned action (dependency-update-v1 or a pinned SHA)', () => {
    const calls = steps(wf).filter((s) => s.uses?.includes('dependency-action'));
    expect(calls).toHaveLength(1);
    expect(calls[0].uses).toMatch(ACTION_REF);
  });

  it('uploads its artifact only on success', () => {
    const uploads = steps(wf).filter((s) => s.uses?.startsWith('actions/upload-artifact@'));
    expect(uploads).toHaveLength(1);
    expect(uploads[0].if).toBe('success()');
    expect(uploads[0].with?.name).toMatch(/\$\{\{ inputs\.run_id \}\}$/);
  });

  it('never pushes or commits', () => {
    expect(code(file)).not.toMatch(/git\s+(push|commit)|gh\s+pr\b/);
  });

  it('takes the bot inputs it needs', () => {
    const inputs = Object.keys((wf.on.workflow_dispatch as { inputs: Record<string, unknown> }).inputs);
    expect(inputs).toContain('run_id');
    expect(inputs.some((i) => i === 'expected_sha' || i === 'base_sha')).toBe(true);
  });
});

describe('secrets (D-S14b)', () => {
  it('no template passes a secret by default', () => {
    for (const f of TEMPLATES) expect(code(f)).not.toMatch(/secrets\./);
  });

  it('the apply template documents the safe list and forbids Gradle feed secrets', () => {
    const text = raw('dependency-apply.yml');
    expect(text).toMatch(/npm, pnpm, yarn, NuGet \(restore only\), Composer, Cargo, Go and Maven/);
    expect(text).toMatch(/NEVER pass feed secrets when the repository uses Gradle/);
  });

  it('the migrate templates carry no secrets and say why', () => {
    for (const f of ['angular-migrate.yml', 'nx-migrate.yml']) expect(raw(f)).toMatch(/NO secrets/);
  });

  it('the apply checkout keeps HEAD^ (expected_sha) available', () => {
    const checkout = steps(load('dependency-apply.yml')).find((s) => s.uses?.startsWith('actions/checkout@'))!;
    expect(checkout.with?.['fetch-depth']).toBe(2);
  });
});
