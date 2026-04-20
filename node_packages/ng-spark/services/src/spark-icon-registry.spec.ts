import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach } from 'vitest';
import { SparkIconRegistry } from './spark-icon-registry';
import { SPARK_BUILT_IN_ICONS } from './spark-built-in-icons';

describe('SparkIconRegistry', () => {
  let registry: SparkIconRegistry;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    registry = TestBed.inject(SparkIconRegistry);
  });

  it('seeds the registry with all built-in icons on construction', () => {
    for (const name of Object.keys(SPARK_BUILT_IN_ICONS)) {
      expect(registry.has(name)).toBe(true);
    }
  });

  it('get() returns a SafeHtml for a built-in icon', () => {
    const firstBuiltIn = Object.keys(SPARK_BUILT_IN_ICONS)[0];
    expect(registry.get(firstBuiltIn)).toBeDefined();
  });

  it('has() returns false for an unknown name', () => {
    expect(registry.has('definitely-not-an-icon-name')).toBe(false);
  });

  it('register() adds a new icon that is then visible via get/has', () => {
    registry.register('custom-x', '<svg><circle /></svg>');

    expect(registry.has('custom-x')).toBe(true);
    expect(registry.get('custom-x')).toBeDefined();
  });

  it('register() overwrites an existing icon with the new svg', () => {
    const firstBuiltIn = Object.keys(SPARK_BUILT_IN_ICONS)[0];
    const before = registry.get(firstBuiltIn);

    registry.register(firstBuiltIn, '<svg><rect /></svg>');

    const after = registry.get(firstBuiltIn);
    expect(after).not.toBe(before); // new SafeHtml instance
  });
});
