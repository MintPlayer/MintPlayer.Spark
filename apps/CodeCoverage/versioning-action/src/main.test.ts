import type { Exec } from './exec';
import { allVersionsOf, buildEnvironment, failureOf, readInputs, readPlan, verifyHandTags, version, versioningSection, type Dependencies } from './main';

const release = (over: object = {}) => ({
  id: 'Acme.Core', kind: 'nuget', version: '1.2.0', tag: 'Acme.Core/v1.2.0', reasons: ['api changed'], taggedByHand: false, tagOutcome: 'created', ...over,
});

describe('failureOf', () => {
  it('passes a run whose every tag is written or already there', () => {
    expect(failureOf({ outcome: 'versioned', releases: [release(), release({ tagOutcome: 'already-present' }), release({ taggedByHand: true, tagOutcome: undefined })] as any, refused: [], skipped: 0, warnings: [] })).toBeNull();
  });

  it('names refused packages and tags that did not land', () => {
    const failure = failureOf({
      outcome: 'versioned', releases: [release({ tagOutcome: 'conflict' })] as any, refused: [], skipped: 0, warnings: [],
    });
    expect(failure).toContain('Acme.Core/v1.2.0: conflict');

    const refused = failureOf({ outcome: 'plan-refused', releases: [], refused: [{ id: 'Acme.Core', reason: 'major past its line' }], skipped: 0, warnings: [] });
    expect(refused).toContain('plan-refused');
    expect(refused).toContain('Acme.Core: major past its line');
  });
});

describe('verifyHandTags (segment P, P5)', () => {
  const commit = 'a'.repeat(40);
  const promote = { lines: { current: 'promote', workflow: 'release.yml' } };
  const handTagged = { outcome: 'versioned', releases: [release({ taggedByHand: true }), release({ id: 'Acme.Web', tag: 'Acme.Web/v1.0.0' })] as any, refused: [], skipped: 0, warnings: [] };
  const deps = (answer: () => Promise<Response>, asked: unknown[] = []): Dependencies => ({
    exec: (async () => ({ code: 0, stdout: '', stderr: '' })) as unknown as Exec, engineProject: 'x', idToken: async () => 'token',
    fetch: (async (_url: string, init: RequestInit) => { asked.push(JSON.parse(init.body as string)); return answer(); }) as unknown as typeof fetch,
    log: () => {}, warn: () => {},
  });
  const inputs = { ...readInputs({}), url: 'https://coverage.example.invalid' };

  it('asks about the hand tags only, and passes when the app placed them', async () => {
    const asked: unknown[] = [];
    await verifyHandTags(inputs, deps(async () => new Response(JSON.stringify({ refused: [] }), { status: 200 }), asked), promote, commit, handTagged);
    expect(asked).toEqual([{ commit, tags: ['Acme.Core/v1.2.0'] }]);
  });

  it('fails on a tag the app did not place, and on an app that cannot answer', async () => {
    await expect(verifyHandTags(inputs, deps(async () => new Response(JSON.stringify({ refused: ['Acme.Core/v1.2.0'] }), { status: 200 })), promote, commit, handTagged))
      .rejects.toThrow(/Acme.Core\/v1.2.0 was not placed by `\/release`/);
    await expect(verifyHandTags(inputs, deps(async () => new Response('down', { status: 503 })), promote, commit, handTagged))
      .rejects.toThrow(/could not be checked/);
    await expect(verifyHandTags(inputs, deps(async () => { throw new Error('ECONNREFUSED'); }), promote, commit, handTagged))
      .rejects.toThrow(/ECONNREFUSED/);
  });

  it('asks nothing without current: promote or without hand tags', async () => {
    const asked: unknown[] = [];
    const never = deps(async () => { throw new Error('asked'); }, asked);
    await verifyHandTags(inputs, never, { lines: { current: 'backport' } }, commit, handTagged);
    await verifyHandTags(inputs, never, promote, commit, { ...handTagged, releases: [release()] as any });
    expect(asked).toEqual([]);
  });
});

describe('failureOf a plan (#552 D1)', () => {
  const plan = { commit: 'c', fingerprint: 'f', tags: [{ name: 'Acme.Core/v1.2.0' }] };

  it('passes a plan whose every release is planned, and only a plan', () => {
    const planned = { outcome: 'versioned', releases: [release({ tagOutcome: 'planned' })] as any, refused: [], skipped: 0, warnings: [], plan };
    expect(failureOf(planned, true)).toBeNull();
    expect(failureOf(planned)).toContain('Acme.Core/v1.2.0: planned');
    expect(failureOf({ ...planned, plan: undefined }, true)).toContain('no plan');
  });
});

describe('allVersionsOf (#552 D10)', () => {
  it('gives every package its release, else its last reachable tag, and omits one with neither', async () => {
    const exec: Exec = async (_cmd, args) => {
      expect(args).toEqual(['tag', '--list', '--merged', 'HEAD']);
      return { code: 0, stdout: 'Acme.Core/v1.1.0\n@acme/util/v2.0.0\n@acme/util/v2.1.0\nversioning-v1\n', stderr: '' };
    };
    const versions = await allVersionsOf(exec, '.', ['Acme.Core', '@acme/util', 'Acme.New'], [release({ version: '1.2.0', tagOutcome: 'planned' })] as any);
    expect(versions).toEqual({ 'Acme.Core': '1.2.0', '@acme/util': '2.1.0' });
  });
});

describe('readPlan (#552 D3, D5)', () => {
  const output = JSON.stringify({ commit: 'abc', branch: 'main', mode: 'local', response: { outcome: 'versioned', releases: [] } });

  it('reads the plan made for this commit and branch', () => {
    expect(readPlan(output, 'abc', 'main')).toMatchObject({ commit: 'abc', mode: 'local' });
  });

  it('refuses no plan, another step\'s output, and a plan for another commit or branch', () => {
    expect(() => readPlan('', 'abc', 'main')).toThrow(/needs the plan/);
    expect(() => readPlan('{"outcome":"versioned"}', 'abc', 'main')).toThrow(/not the plan/);
    expect(() => readPlan(output, 'def', 'main')).toThrow(/Re-run all jobs/);
    expect(() => readPlan(output, 'abc', 'release/1.x')).toThrow(/Re-run all jobs/);
  });
});

describe('versioningSection', () => {
  it('reads the section and refuses a missing one', () => {
    expect(versioningSection('versioning:\n  mode: local\n  ignore: ["**/fixtures/**"]\n', 'main')).toEqual({ mode: 'local', ignore: ['**/fixtures/**'] });
    expect(() => versioningSection('comment: {}\n', 'main')).toThrow(/no versioning/);
    expect(() => versioningSection(null, 'main')).toThrow(/does not exist/);
  });

  it('keeps an empty lines: switched on (#568 D15)', () => {
    expect(versioningSection('versioning:\n  lines:\n', 'main')).toEqual({ lines: {} });
    expect(versioningSection('versioning:\n  lines:\n    pattern: support/{ecosystem}/{major}\n', 'main')).toEqual({ lines: { pattern: 'support/{ecosystem}/{major}' } });
  });
});

describe('buildEnvironment (#568 D10b)', () => {
  it('hands major-bounded dependency ranges to the pack, and nothing else', () => {
    expect(buildEnvironment({ dependencyRanges: 'major-bounded' })).toEqual({ MintPlayerVersioningDependencyRanges: 'major-bounded' });
    expect(buildEnvironment({ dependencyRanges: 'minimum' })).toEqual({});
    expect(buildEnvironment({})).toEqual({});
  });
});

describe('version', () => {
  const deps = (exec: Exec): Dependencies => ({
    exec, engineProject: 'cli', idToken: async () => 't', log: () => {}, warn: () => {},
  });

  it('versions nothing on a pull request or a tag build', async () => {
    const exec: Exec = async () => { throw new Error('no command may run'); };
    for (const [eventName, ref] of [['pull_request', 'refs/pull/1/merge'], ['push', 'refs/tags/x/v1.0.0']]) {
      const inputs = { ...readInputs({}), eventName, ref };
      await expect(version(inputs, deps(exec))).resolves.toMatchObject({ outcome: 'skipped', released: [] });
      for (const phase of ['plan', 'release'])
        await expect(version({ ...inputs, phase }, deps(exec))).resolves.toMatchObject({ outcome: 'skipped' });
    }
  });

  it('refuses an unknown phase', async () => {
    const exec: Exec = async () => { throw new Error('no command may run'); };
    await expect(version({ ...readInputs({}), phase: 'tag' }, deps(exec))).rejects.toThrow(/phase must be/);
  });
});
