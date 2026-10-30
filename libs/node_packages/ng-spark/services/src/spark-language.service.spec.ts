import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { currentLanguage } from '@mintplayer/ng-spark/models';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkLanguageService } from './spark-language.service';

/**
 * Language selection: a saved choice beats the server default, and every resolution falls back
 * current language → English → first available → '' (or the key itself, for `t`).
 */
describe('SparkLanguageService', () => {
  let httpTesting: HttpTestingController;

  const culture = { languages: { en: { en: 'English' }, nl: { en: 'Dutch' } }, defaultLanguage: 'nl' };

  const create = (providers: unknown[] = []) => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), ...(providers as any[])],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    return TestBed.inject(SparkLanguageService);
  };

  /** Answers both constructor requests and lets their awaits run. */
  const load = async (base = '/spark', translations: Record<string, Record<string, string>> = {}) => {
    httpTesting.expectOne(`${base}/culture`).flush(culture);
    httpTesting.expectOne(`${base}/translations`).flush(translations);
    await new Promise(resolve => setTimeout(resolve, 0));
  };

  // The cookie is the source of truth, so one test's language must not leak into the next.
  const clearCookie = () => { document.cookie = 'spark-lang=; max-age=0; path=/'; };

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    clearCookie();
    currentLanguage.set('en');
  });

  afterEach(() => {
    httpTesting.verify();
    localStorage.clear();
    currentLanguage.set('en');
  });

  it('adopts the server default language and publishes the language list', async () => {
    const service = create();
    expect(service.language()).toBe('en');

    await load();

    expect(service.language()).toBe('nl');
    expect(currentLanguage()).toBe('nl');
    expect(service.languages()).toEqual(culture.languages);
  });

  it('prefers the spark-lang cookie over localStorage and the server default', async () => {
    document.cookie = 'spark-lang=en; path=/';
    localStorage.setItem('spark-lang', 'fr');
    const service = create();
    await load();

    expect(service.language()).toBe('en');
    expect(currentLanguage()).toBe('en');
  });

  it('falls back to a localStorage choice that predates the cookie', async () => {
    localStorage.setItem('spark-lang', 'fr');
    const service = create();
    await load();

    expect(service.language()).toBe('fr');
    expect(currentLanguage()).toBe('fr');
  });

  it('uses the configured base URL', async () => {
    const service = create([{ provide: SPARK_CONFIG, useValue: { baseUrl: '/api/spark' } }]);
    await load('/api/spark');
    expect(service.language()).toBe('nl');
  });

  it('setLanguage updates both signals and persists the choice', async () => {
    const service = create();
    await load();

    service.setLanguage('en');

    expect(service.language()).toBe('en');
    expect(currentLanguage()).toBe('en');
    // The cookie alone persists it: a server render reads the cookie, never localStorage.
    expect(document.cookie).toContain('spark-lang=en');
    expect(localStorage.getItem('spark-lang')).toBeNull();
  });

  it('writes the spark-lang cookie for the initial language too', async () => {
    localStorage.setItem('spark-lang', 'fr');
    create();
    await load();

    expect(document.cookie).toContain('spark-lang=fr');
  });

  describe('resolve', () => {
    it('falls back current language → English → first value → empty', async () => {
      const service = create();
      await load(); // current language: nl

      expect(service.resolve({ nl: 'Hallo', en: 'Hello' })).toBe('Hallo');
      expect(service.resolve({ fr: 'Bonjour', en: 'Hello' })).toBe('Hello');
      expect(service.resolve({ fr: 'Bonjour' })).toBe('Bonjour');
      expect(service.resolve({})).toBe('');
      expect(service.resolve(undefined)).toBe('');
    });
  });

  describe('t', () => {
    it('translates a known key into the current language', async () => {
      const service = create();
      await load('/spark', { save: { en: 'Save', nl: 'Opslaan' } });

      expect(service.t('save')).toBe('Opslaan');
    });

    it('returns the key itself when it is unknown or translates to empty text', async () => {
      const service = create();
      await load('/spark', { blank: { nl: '' } });

      expect(service.t('missing')).toBe('missing');
      expect(service.t('blank')).toBe('blank');
    });

    it('returns the key before the translations have arrived', async () => {
      const service = create();
      expect(service.t('save')).toBe('save');
      await load();
    });

    /**
     * #268 M6: `t(key) || 'fallback'` (spark-query-grid) could never fall back, because a missing key comes
     * back as the key itself; the grid showed "spark.query.unavailable" to users before the translations
     * arrived. The fallback argument is the one way to say it.
     */
    it('returns the fallback instead of the key when the text is missing, and the text when it is there', async () => {
      const service = create();
      expect(service.t('spark.query.unavailable', 'This list is not available.')).toBe('This list is not available.');
      await load('/spark', { 'spark.query.unavailable': { nl: 'Deze lijst is niet beschikbaar.' }, blank: { nl: '' } });

      expect(service.t('spark.query.unavailable', 'This list is not available.')).toBe('Deze lijst is niet beschikbaar.');
      expect(service.t('blank', 'Fallback')).toBe('Fallback');
      expect(service.t('missing', 'Fallback')).toBe('Fallback');
    });
  });
});
