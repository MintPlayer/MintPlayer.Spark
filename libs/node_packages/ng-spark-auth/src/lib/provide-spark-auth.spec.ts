import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { describe, expect, it, beforeEach } from 'vitest';

import { provideSparkAuth, withSparkAuth } from './provide-spark-auth';
import { defaultSparkAuthConfig, SPARK_AUTH_CONFIG } from '@mintplayer/ng-spark-auth/models';
import { SPARK_AUTH_STATE } from '@mintplayer/ng-spark';

/**
 * This file had NO spec, and the consequence was not just untested code — it was
 * *unmeasured* code. Vitest's v8 provider parses a file no spec imports as raw JavaScript
 * rather than through the TypeScript transform, so `config?: Partial<SparkAuthConfig>`
 * failed to parse and the provider logged "Excluding it from coverage" and moved on.
 * The file vanished from the denominator entirely: 20 of 21 source files were reported,
 * and the missing one was invisible rather than 0%.
 *
 * So a spec here is worth more than its assertions: importing the file is what puts it
 * back in the measurement at all.
 */
describe('provideSparkAuth', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('falls back to the default config when given none', () => {
    TestBed.configureTestingModule({ providers: [provideSparkAuth()] });

    expect(TestBed.inject(SPARK_AUTH_CONFIG)).toEqual(defaultSparkAuthConfig);
  });

  it('merges a partial config over the defaults rather than replacing them', () => {
    TestBed.configureTestingModule({
      providers: [provideSparkAuth({ apiBase: '/custom-auth' } as never)],
    });

    const config = TestBed.inject(SPARK_AUTH_CONFIG) as Record<string, unknown>;

    expect(config['apiBase']).toBe('/custom-auth');
    // Every other default survives — a spread, not an overwrite.
    for (const [key, value] of Object.entries(defaultSparkAuthConfig)) {
      if (key !== 'apiBase') expect(config[key]).toEqual(value);
    }
  });

  it('bridges the signed-in user into ng-spark via SPARK_AUTH_STATE', () => {
    // The bridge exists so ng-spark can react to auth without depending on this package.
    // If it regressed to undefined, the program-units menu would simply never refresh.
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideSparkAuth()],
    });

    const state = TestBed.inject(SPARK_AUTH_STATE);

    expect(state).toBeDefined();
    expect(typeof state).toBe('function');
  });
});

describe('withSparkAuth', () => {
  it('contributes the interceptor and the XSRF configuration', () => {
    const features = withSparkAuth();

    // Two HttpFeatures: without the XSRF one, every mutation would be rejected by the
    // server's antiforgery check, which is not something a unit test of the interceptor
    // alone would notice.
    expect(features).toHaveLength(2);
    for (const feature of features) expect(feature.ɵkind).toBeDefined();
  });
});
