import { setResultOutputs, numberInput } from './main';
import type { UploadStatus } from './status';

const outputs = new Map<string, string>();
const inputs = new Map<string, string>();

vi.mock('@actions/core', () => ({
  setOutput: (name: string, value: unknown) => outputs.set(name, String(value)),
  getInput: (name: string) => inputs.get(name) ?? '',
  warning: () => {},
  info: () => {},
  debug: () => {},
  setFailed: () => {},
}));

beforeEach(() => {
  outputs.clear();
  inputs.clear();
});

/**
 * The action's outputs are its entire public contract: every gate, badge and PR comment a consumer
 * writes reads these strings. `main.ts` was at 0% coverage, and the outputs are where a silent
 * change hurts most — a workflow consuming `patch-rate` cannot tell "no data" from "zero" unless
 * this function is careful, and nothing was checking that it is.
 */
describe('setResultOutputs', () => {
  const minimal: UploadStatus = { buildId: 'b1', state: 'Complete', status: 'Passed' };

  it('emits every declared output even when the server sent almost nothing', () => {
    setResultOutputs(minimal);

    // A consumer reading an output that was never set gets undefined and usually crashes; a
    // consumer reading '' can branch. So absence must be spelled, not omitted.
    for (const name of [
      'state', 'build-status', 'finalize-reason', 'commit-url',
      'lines-covered', 'lines-coverable', 'line-rate',
      'branches-covered', 'branches-total', 'branch-rate', 'files-count',
      'baseline-sha', 'baseline-line-rate',
      'base-resolution', 'resolved-base-sha',
      'projection-line-rate', 'projection-complete', 'projection-incomplete-reasons',
      'patch-lines-covered', 'patch-lines-coverable', 'patch-rate', 'patch-diff-truncated',
      'assembly-line-rate', 'assembly-completeness', 'assembly-base-sha',
    ]) {
      expect(outputs.has(name), `missing output: ${name}`).toBe(true);
    }

    expect(outputs.get('state')).toBe('Complete');
    expect(outputs.get('build-status')).toBe('Passed');
  });

  /**
   * The distinction the header comment in main.ts calls out explicitly: a gate that wants to
   * require patch coverage must treat empty as "abstain", never as zero. Emitting '0.0' when no
   * diff base was available would fail every such gate on a first upload.
   */
  it('reports absent patch coverage as empty, never as zero', () => {
    setResultOutputs(minimal);

    expect(outputs.get('patch-rate')).toBe('');
    expect(outputs.get('patch-lines-covered')).toBe('');
    expect(outputs.get('patch-diff-truncated')).toBe('');
  });

  /**
   * `0/0` is not 100%. A build that measured nothing must not report a perfect score, or a ratchet
   * comparing against it would lock the project at an unreachable target forever.
   */
  it('reports a zero-line measurement as no data rather than 100%', () => {
    setResultOutputs({ ...minimal, coverage: { linesCovered: 0, linesCoverable: 0 } as never });

    expect(outputs.get('line-rate')).toBe('');
  });

  it('computes rates to one decimal from the covered/coverable pair', () => {
    setResultOutputs({
      ...minimal,
      coverage: { linesCovered: 1, linesCoverable: 3, branchesCovered: 1, branchesTotal: 2, filesCount: 7 } as never,
    });

    expect(outputs.get('line-rate')).toBe('33.3');
    expect(outputs.get('branch-rate')).toBe('50.0');
    expect(outputs.get('files-count')).toBe('7');
  });

  /**
   * A first upload has no baseline by definition, and a ratchet with nothing to compare against
   * must pass. So these stay empty rather than defaulting to the current build's own numbers,
   * which would make every first upload trivially "no regression".
   */
  it('leaves baseline outputs empty on a first upload', () => {
    setResultOutputs({ ...minimal, coverage: { linesCovered: 5, linesCoverable: 10 } as never });

    expect(outputs.get('baseline-sha')).toBe('');
    expect(outputs.get('baseline-line-rate')).toBe('');
    expect(outputs.get('line-rate')).toBe('50.0');
  });

  it('joins incomplete reasons so a workflow can read them as one string', () => {
    setResultOutputs({
      ...minimal,
      projection: { complete: false, incompleteReasons: ['missing-base', 'pruned'] } as never,
    });

    expect(outputs.get('projection-incomplete-reasons')).toBe('missing-base,pruned');
    expect(outputs.get('projection-complete')).toBe('false');
  });

  /**
   * `complete: false` must survive as the string "false", not collapse to ''. A `?? ''` on a
   * boolean is the classic version of this bug, and it turns "we know it is incomplete" into
   * "we do not know", which a gate reads as abstain.
   */
  it('distinguishes a false flag from an absent one', () => {
    setResultOutputs({ ...minimal, projection: { complete: false } as never });
    expect(outputs.get('projection-complete')).toBe('false');

    outputs.clear();
    setResultOutputs(minimal);
    expect(outputs.get('projection-complete')).toBe('');
  });
});

describe('numberInput', () => {
  it('falls back when the input is absent or blank', () => {
    expect(numberInput('timeout', 42)).toBe(42);

    inputs.set('timeout', '   ');
    expect(numberInput('timeout', 42)).toBe(42);
  });

  it('reads a positive number', () => {
    inputs.set('timeout', ' 90 ');
    expect(numberInput('timeout', 42)).toBe(90);
  });

  /**
   * Rejected loudly rather than silently falling back: a workflow that wrote `timeout: 0` meant
   * something, and quietly substituting the default would make the action wait ten minutes on a
   * run the author expected to fail fast.
   */
  it.each(['0', '-5', 'soon', 'NaN', 'Infinity'])('rejects %s instead of falling back', raw => {
    inputs.set('timeout', raw);

    expect(() => numberInput('timeout', 42)).toThrow(/positive number/);
  });
});
