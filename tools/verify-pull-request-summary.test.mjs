// The summary job the merge queue's rulesets require (#714, docs/merge_queue_required_check_renames_PRD.md Dec3, Dec12).
//
// The rulesets require one job of ours by name: `pull-request`, the job that is green only when every other job is. Every
// other job is enforced through it, so two mistakes weaken GitHub's protection without anything going red:
// - renaming the summary job: the rulesets still ask for `pull-request`, and every queued pull request waits for a check
//   that never reports (#714 was this, for two test jobs);
// - adding a job without adding it to the summary job's `needs`: GitHub no longer enforces that job at all.
// The CodeCoverage App never reads or edits our workflows, so only this test catches either.
//
// Run: npm run test:tools (node --test over tools/*.test.mjs); CI runs it in pull-request.yml.
import { strict as assert } from 'node:assert';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const WORKFLOWS = path.join(REPO, '.github', 'workflows');
const FILE = 'pull-request.yml';
const SUMMARY = 'pull-request';

const parse = (file) => YAML.parse(readFileSync(path.join(WORKFLOWS, file), 'utf8'));
const jobs = parse(FILE).jobs ?? {};
const summary = jobs[SUMMARY] ?? {};

/**
 * The name a job's check run carries: its `name`, else its id. A job that calls a reusable workflow reports no check
 * of its own; its called jobs report as "<name> / <job>".
 */
const checkName = (id, job) => (job.uses ? null : String(job.name ?? id));

/** The events a workflow runs on, whichever of YAML's three shapes `on` takes. */
const triggers = (on) => (typeof on === 'string' ? [on] : Array.isArray(on) ? on : Object.keys(on ?? {}));

test('the summary job reports as `pull-request` on every run', () => {
  assert.ok(jobs[SUMMARY], `${FILE} has a job with the id \`${SUMMARY}\``);
  assert.equal(checkName(SUMMARY, summary), SUMMARY,
    'the rulesets require the check by this name: renamed, every queued pull request waits for it (#714)');
  assert.equal(String(summary.if ?? '').replace(/^\$\{\{\s*|\s*\}\}$/g, '').trim(), 'always()',
    'it runs when a job it needs failed or was skipped, so it reports red instead of not at all');
  assert.equal(summary.strategy, undefined, 'a matrix would add its values to the check name');
});

test('the summary job needs every other job in the workflow', () => {
  const needs = [].concat(summary.needs ?? []).map(String).sort();
  const others = Object.keys(jobs).filter((id) => id !== SUMMARY).sort();
  assert.deepEqual(needs, others,
    'a job left out of `needs` is not enforced by GitHub: the rulesets require only the summary job');
});

test('the summary job fails when a job it needs failed, was cancelled or was skipped', () => {
  const script = (summary.steps ?? []).map((step) => String(step.run ?? '')).join('\n');
  for (const result of ['failure', 'cancelled', 'skipped'])
    assert.match(script, new RegExp(`contains\\(needs\\.\\*\\.result, '${result}'\\)`), `it checks for '${result}'`);
  assert.match(script, /exit 1/, 'and then exits non-zero');
});

test('no other pull-request workflow reports a check named `pull-request`', () => {
  const clashes = [];
  for (const file of readdirSync(WORKFLOWS).filter((f) => /\.ya?ml$/.test(f))) {
    const workflow = parse(file);
    if (!triggers(workflow.on).some((t) => t === 'pull_request' || t === 'pull_request_target')) continue;
    for (const [id, job] of Object.entries(workflow.jobs ?? {}))
      if (checkName(id, job) === SUMMARY && !(file === FILE && id === SUMMARY)) clashes.push(`${file}: ${id}`);
  }
  assert.deepEqual(clashes, [], 'a second check with the summary job\'s name could satisfy the rulesets in its place');
});
