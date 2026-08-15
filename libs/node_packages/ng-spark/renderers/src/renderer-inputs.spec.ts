import { Component, input } from '@angular/core';
import { describe, expect, it } from 'vitest';

import { rendererValue, withDeclaredInputs } from './renderer-inputs';
import { PersistentObject, PersistentObjectAttribute } from '@mintplayer/ng-spark/models';

@Component({ selector: 'spec-value-only', standalone: true, template: '' })
class ValueOnlyComponent {
  value = input<any>();
}

@Component({ selector: 'spec-full-bag', standalone: true, template: '' })
class FullBagComponent {
  value = input<any>();
  attribute = input<any>();
  options = input<Record<string, any>>();
  item = input<any>();
}

describe('withDeclaredInputs', () => {
  const bag = { value: 1, attribute: { name: 'A' }, options: undefined, item: { id: 'x' } };

  it('drops entries the component does not declare', () => {
    expect(withDeclaredInputs(ValueOnlyComponent, bag)).toEqual({ value: 1 });
  });

  it('keeps every declared entry, including undefined values', () => {
    const filtered = withDeclaredInputs(FullBagComponent, bag);
    expect(filtered).toEqual(bag);
    expect(Object.keys(filtered)).toContain('options');
  });

  it('stays consistent across repeated calls for the same type', () => {
    withDeclaredInputs(ValueOnlyComponent, bag);
    expect(withDeclaredInputs(ValueOnlyComponent, { value: 2, extra: 3 })).toEqual({ value: 2 });
  });

  it('returns an empty bag for a non-component type', () => {
    class NotAComponent {}
    expect(withDeclaredInputs(NotAComponent, bag)).toEqual({});
  });
});

describe('rendererValue', () => {
  const nested: PersistentObject = { id: 'c/1', type: 'CoverageSummary', attributes: [] } as any;

  it('prefers the flat value when present', () => {
    const attr = { name: 'A', value: 42, object: nested } as PersistentObjectAttribute;
    expect(rendererValue(attr)).toBe(42);
  });

  it('falls back to the nested object for a single AsDetail', () => {
    const attr = { name: 'A', value: null, object: nested } as PersistentObjectAttribute;
    expect(rendererValue(attr)).toBe(nested);
  });

  it('falls back to the nested objects array for an AsDetail array', () => {
    const attr = { name: 'A', value: null, object: null, objects: [nested] } as PersistentObjectAttribute;
    expect(rendererValue(attr)).toEqual([nested]);
  });

  it('returns undefined for an undefined attribute', () => {
    expect(rendererValue(undefined)).toBeUndefined();
  });

  it('keeps falsy-but-set flat values only when non-nullish', () => {
    expect(rendererValue({ name: 'A', value: 0 } as PersistentObjectAttribute)).toBe(0);
    expect(rendererValue({ name: 'A', value: false } as PersistentObjectAttribute)).toBe(false);
  });
});
