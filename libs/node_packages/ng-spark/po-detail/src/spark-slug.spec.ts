import { describe, expect, it } from 'vitest';
import { sparkSlug } from './spark-slug';

describe('sparkSlug', () => {
  it.each([
    ['A Sky Full of Stars', 'a-sky-full-of-stars'],
    ['Beyoncé - Halo!', 'beyonce-halo'],
    ['  --Crème brûlée--  ', 'creme-brulee'],
    ['AC/DC', 'ac-dc'],
    ['2 Unlimited', '2-unlimited'],
    ['!!!', ''],
    [null, ''],
    [undefined, ''],
  ])('%s → %s', (text, slug) => {
    expect(sparkSlug(text)).toBe(slug);
  });
});
