import { InjectionToken } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideSpark, sparkFeature, withConfig } from './provide-spark';
import { SPARK_CONFIG, defaultSparkConfig } from './spark-config';

const LOG = new InjectionToken<string[]>('log');
const logged = (kind: string, order: number, multi = false) =>
  sparkFeature(kind, order, [{ provide: LOG, useValue: kind, multi: true }], { multi });

/** `docs/ng_spark_provide_spark_PRD.md` §5: order independence, duplicates, requires, default config. */
describe('provideSpark', () => {
  beforeEach(() => TestBed.resetTestingModule());

  const log = (...features: Parameters<typeof provideSpark>) => {
    TestBed.configureTestingModule({ providers: [provideSpark(...features)] });
    return TestBed.inject(LOG);
  };

  it('orders providers by the features\' own order, whatever order the app lists them in', () => {
    const a = () => logged('a', 10), b = () => logged('b', 200), c = () => logged('c', 900);
    expect(log(c(), a(), b())).toEqual(['a', 'b', 'c']);
    TestBed.resetTestingModule();
    expect(log(a(), b(), c())).toEqual(['a', 'b', 'c']);
  });

  it('keeps the app\'s order between features of the same order', () => {
    expect(log(logged('x', 500, true), logged('y', 500, true))).toEqual(['x', 'y']);
  });

  it('throws on a feature listed twice, naming it', () => {
    expect(() => provideSpark(logged('auth', 100), logged('auth', 100))).toThrow(/"auth"/);
  });

  it('lets a registration be listed more than once', () => {
    expect(log(logged('renderers', 500, true), logged('renderers', 500, true))).toEqual(['renderers', 'renderers']);
  });

  it('adds a required feature that is missing, and keeps a listed one', () => {
    const search = () => logged('search', 200);
    const description = sparkFeature('description', 210, [{ provide: LOG, useValue: 'description', multi: true }], { requires: [search] });
    expect(log(description)).toEqual(['search', 'description']);
    TestBed.resetTestingModule();
    expect(log(description, search())).toEqual(['search', 'description']);
  });

  it('provides the default configuration unless withConfig is listed', () => {
    TestBed.configureTestingModule({ providers: [provideSpark()] });
    expect(TestBed.inject(SPARK_CONFIG)).toEqual(defaultSparkConfig);
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideSpark(withConfig({ baseUrl: '/api' }))] });
    expect(TestBed.inject(SPARK_CONFIG).baseUrl).toBe('/api');
  });
});
