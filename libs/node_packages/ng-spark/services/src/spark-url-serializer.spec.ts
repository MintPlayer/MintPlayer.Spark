import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { SparkLanguageService } from './spark-language.service';
import { SparkUrlSerializer } from './spark-url-serializer';

/** Every router URL keeps the page's language (#604 D4), except in the default language. */
describe('SparkUrlSerializer', () => {
  const language = signal('nl');
  let serializer: SparkUrlSerializer;
  const roundTrip = (url: string) => serializer.serialize(serializer.parse(url));

  beforeEach(() => {
    language.set('nl');
    TestBed.configureTestingModule({
      providers: [
        SparkUrlSerializer,
        { provide: SparkLanguageService, useValue: { language, defaultLanguage: signal('en') } },
      ],
    });
    serializer = TestBed.inject(SparkUrlSerializer);
  });

  it('adds the current language when the URL names none', () => {
    expect(roundTrip('/po/song/Songs%2F1/halo')).toBe('/po/song/Songs%2F1/halo?lang=nl');
  });

  it('keeps the other query parameters', () => {
    expect(roundTrip('/query/songs?deleted=only')).toBe('/query/songs?deleted=only&lang=nl');
  });

  it('leaves the default language out', () => {
    language.set('en');
    expect(roundTrip('/po/song')).toBe('/po/song');
  });

  it('never changes a lang the URL sets, so a link can name the default language', () => {
    expect(roundTrip('/po/song?lang=en')).toBe('/po/song?lang=en');
  });
});
