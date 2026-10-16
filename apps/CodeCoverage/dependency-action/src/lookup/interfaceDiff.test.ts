import type { LookupRecord } from '../contracts/types';
import { readFixture } from '../testing/helpers';
import { diffInterface, findUsages, interfaceDiffsFor, parseActionYml } from './interfaceDiff';

const yml = (action: string, tag: string) => readFixture(`action-yml/${action.replace('/', '_')}/${tag}/action.yml`);
const iface = (action: string, tag: string) => parseActionYml(yml(action, tag));
const CI = readFixture('renovate/docker-actions-platform/input/.github/workflows/ci.yml');

describe('action.yml interface diff on the real S12 pairs (shared with the C# GitHubWorkflowValidator)', () => {
  it('checkout v4 -> v5: only the node runtime changes', () => {
    const d = diffInterface('actions/checkout', 'v4', 'v5', iface('actions/checkout', 'v4'), iface('actions/checkout', 'v5'), findUsages('ci.yml', CI).filter((u) => u.action === 'actions/checkout'));
    expect(d.runtime).toEqual({ from: 'node20', to: 'node24' });
    expect(d.addedInputs).toEqual([]);
    expect(d.removedInputs).toEqual([]);
    expect(d.removedOutputs).toEqual([]);
    expect(d.blockers.every((b) => b.kind === 'runtime-changed')).toBe(true);
    expect(d.verdict).toBe('compatible');
  });

  it('upload-artifact v3 -> v4: inputs added, nothing removed (its real break is invisible here)', () => {
    const d = diffInterface('actions/upload-artifact', 'v3', 'v4', iface('actions/upload-artifact', 'v3'), iface('actions/upload-artifact', 'v4'), findUsages('ci.yml', CI).filter((u) => u.action === 'actions/upload-artifact'));
    expect(d.runtime).toEqual({ from: 'node16', to: 'node20' });
    expect(d.addedInputs).toEqual(expect.arrayContaining(['compression-level', 'overwrite']));
    expect(d.removedInputs).toEqual([]);
    expect(d.verdict).toBe('compatible');
  });

  it('setup-node v4 -> v5: adds package-manager-cache, removes nothing', () => {
    const d = diffInterface('actions/setup-node', 'v4', 'v5', iface('actions/setup-node', 'v4'), iface('actions/setup-node', 'v5'), findUsages('ci.yml', CI).filter((u) => u.action === 'actions/setup-node'));
    expect(d.addedInputs).toContain('package-manager-cache');
    expect(d.removedInputs).toEqual([]);
    expect(d.newRequiredInputs).toEqual([]);
    expect(d.verdict).toBe('compatible');
  });

  it('a removed input the repo passes, a new required input, and a missing output it reads are blockers', () => {
    const before = parseActionYml('inputs:\n  path: {}\n  old: {}\noutputs:\n  dir: {}\nruns:\n  using: node20\n');
    const after = parseActionYml('inputs:\n  path: {}\n  token:\n    required: true\noutputs: {}\nruns:\n  using: node20\n');
    const wf = 'jobs:\n  b:\n    steps:\n      - id: up\n        uses: o/r@v1\n        with:\n          old: x\n      - run: echo ${{ steps.up.outputs.dir }}\n';
    const d = diffInterface('o/r', 'v1', 'v2', before, after, findUsages('ci.yml', wf));
    expect(d.blockers.map((b) => `${b.kind}:${b.name}`).sort()).toEqual(['missing-output-read:dir', 'new-required-input:token', 'removed-input-used:old']);
    expect(d.verdict).toBe('incompatible');
  });

  it('an action.yml that cannot be read gives verdict unknown', () => {
    expect(diffInterface('o/r', 'v1', 'v2', null, iface('actions/checkout', 'v5'), []).verdict).toBe('unknown');
  });
});

describe('findUsages', () => {
  it('lists remote uses: steps with their with-keys, skipping docker:// and local actions', () => {
    const usages = findUsages('ci.yml', CI);
    expect(usages.map((u) => u.action)).not.toContain('docker://alpine:3.18.0');
    expect(usages.find((u) => u.action === 'actions/setup-dotnet')?.withKeys).toEqual(['dotnet-version']);
    expect(usages.filter((u) => u.action === 'actions/checkout')).toHaveLength(2);
  });

  it('reads composite actions (runs.steps)', () => {
    const usages = findUsages('action.yml', "runs:\n  using: composite\n  steps:\n    - uses: actions/cache@v4\n      with:\n        path: x\n");
    expect(usages).toEqual([{ file: 'action.yml', action: 'actions/cache', ref: 'v4', withKeys: ['path'], outputsRead: [] }]);
  });
});

describe('interfaceDiffsFor', () => {
  it('diffs every major Actions candidate once, fetching both refs', async () => {
    const record: LookupRecord = {
      ecosystem: 'github-actions', manager: 'github-actions', datasource: 'github-tags', packageFile: '.github/workflows/ci.yml', lockFiles: [],
      depName: 'actions/checkout', packageName: 'actions/checkout', depType: 'action', currentValue: 'v4', currentVersion: 'v4.3.0', lockedVersion: null,
      currentDigest: null, replaceString: 'actions/checkout@v4', sharedVariableName: null, groupName: null, skipReason: null, hold: false,
      candidates: [
        { version: 'v5.0.0', value: 'v5', updateType: 'major', releaseTimestamp: null, digest: null, isBreaking: true, isLockfileUpdate: false, groupName: null, branchName: null, missing: ['releaseTimestamp'] },
        { version: 'v4.3.1', value: 'v4', updateType: 'minor', releaseTimestamp: null, digest: null, isBreaking: false, isLockfileUpdate: false, groupName: null, branchName: null, missing: ['releaseTimestamp'] },
      ],
    };
    const fetched: string[] = [];
    const diffs = await interfaceDiffsFor([record, record], new Map([['ci.yml', findUsages('ci.yml', CI)]]), async (a, ref) => {
      fetched.push(`${a}@${ref}`);
      return yml('actions/checkout', ref);
    });
    expect(fetched.sort()).toEqual(['actions/checkout@v4', 'actions/checkout@v5']);
    expect(diffs).toHaveLength(1);
    expect(diffs[0]).toMatchObject({ action: 'actions/checkout', from: 'v4', to: 'v5', verdict: 'compatible', packageFiles: ['ci.yml'] });
  });
});
