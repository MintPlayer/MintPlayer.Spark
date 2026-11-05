import { describe, expect, it } from 'vitest';
import { LinesPipe, sparkLines } from './lines.pipe';

describe('LinesPipe', () => {
  const pipe = new LinesPipe();

  it('splits on \\n, \\r\\n and \\r alike', () => {
    expect(pipe.transform('a\nb\r\nc\rd')).toEqual(['a', 'b', 'c', 'd']);
  });

  it('keeps empty lines, so a blank line between verses stays', () => {
    expect(pipe.transform('verse 1\n\nverse 2')).toEqual(['verse 1', '', 'verse 2']);
  });

  it('gives a single-line value as one line', () => {
    expect(pipe.transform('one line')).toEqual(['one line']);
  });

  it('gives no lines for null or undefined', () => {
    expect(pipe.transform(null)).toEqual([]);
    expect(pipe.transform(undefined)).toEqual([]);
  });

  it('turns a non-string into one line', () => {
    expect(sparkLines(42)).toEqual(['42']);
  });
});
