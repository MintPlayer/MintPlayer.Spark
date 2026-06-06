import { describe, expect, it } from 'vitest';
import { IconNamePipe } from './icon-name.pipe';

describe('IconNamePipe', () => {
  const pipe = new IconNamePipe();

  it('strips the bi- prefix when present', () => {
    expect(pipe.transform('bi-house', 'bi-question')).toBe('house');
  });

  it('returns the value unchanged when no bi- prefix', () => {
    expect(pipe.transform('house', 'question')).toBe('house');
  });

  it('uses the fallback when value is undefined', () => {
    expect(pipe.transform(undefined, 'bi-question')).toBe('question');
  });

  it('uses the fallback when value is an empty string', () => {
    expect(pipe.transform('', 'bi-question')).toBe('question');
  });

  it('strips bi- from the fallback too', () => {
    expect(pipe.transform(undefined, 'bi-house')).toBe('house');
  });
});
