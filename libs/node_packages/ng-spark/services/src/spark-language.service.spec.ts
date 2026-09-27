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

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
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

  it('prefers the language saved in localStorage over the server default', async () => {
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
    expect(localStorage.getItem('spark-lang')).toBe('en');
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
  });
});
