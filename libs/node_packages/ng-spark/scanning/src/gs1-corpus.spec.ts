import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { gs1ParseDigitalLink, gs1ParseScan } from './index';

// The GS1 parsing corpus (libs/gs1/corpus/gs1-corpus.json, built by tools/gs1/build-corpus.mjs): every case carries the
// verdict of GS1's own Barcode Syntax Engine. An input it accepts must give the same elements in the same order, one it
// rejects must be rejected. MintPlayer.Spark.Gs1.Tests holds the C# twin to the same corpus.
interface CorpusCase {
  id: number;
  kind: 'scan' | 'elementString' | 'bracketed' | 'digitalLink';
  input: string;
  source: string;
  valid: boolean;
  elements?: [string, string][];
  engineError?: string;
}

const corpusPath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../../gs1/corpus/gs1-corpus.json');
const corpus: { cases: CorpusCase[] } = JSON.parse(readFileSync(corpusPath, 'utf8'));

const describeElements = (elements: [string, string][]) => 'accepted ' + elements.map(([ai, value]) => `(${ai})${value}`).join('');
const show = (input: string) => JSON.stringify(input).replaceAll('\\u001d', '<GS>');

describe('GS1 parsing corpus', () => {
  it('covers every input kind', () => {
    expect(corpus.cases.length).toBeGreaterThanOrEqual(500);
    expect([...new Set(corpus.cases.map((c) => c.kind))].sort()).toEqual(['bracketed', 'digitalLink', 'elementString', 'scan']);
  });

  const rows = corpus.cases.map((c): [number, string, string, CorpusCase] => [c.id, c.kind, show(c.input), c]);
  it.each(rows)('case %i: %s %s', (_id, _kind, _input, c) => {
    const result = c.kind === 'digitalLink' ? gs1ParseDigitalLink(c.input) : gs1ParseScan(c.input);
    const actual = result.error === '' ? describeElements(result.elements.map((e): [string, string] => [e.ai, e.value])) : 'rejected';
    const expected = c.valid ? describeElements(c.elements ?? []) : 'rejected';
    expect(actual, `${c.source}; engine: ${c.engineError ?? 'valid'}; ours: ${result.error || 'valid'} ${result.errorAi}`).toBe(expected);
  });
});
