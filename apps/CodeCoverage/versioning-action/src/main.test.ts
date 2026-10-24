import type { Exec } from './exec';
import { failureOf, readInputs, version, versioningSection, type Dependencies } from './main';

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

describe('versioningSection', () => {
  it('reads the section and refuses a missing one', () => {
    expect(versioningSection('versioning:\n  mode: local\n  ignore: ["**/fixtures/**"]\n', 'main')).toEqual({ mode: 'local', ignore: ['**/fixtures/**'] });
    expect(() => versioningSection('comment: {}\n', 'main')).toThrow(/no versioning/);
    expect(() => versioningSection(null, 'main')).toThrow(/does not exist/);
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
    }
  });
});
