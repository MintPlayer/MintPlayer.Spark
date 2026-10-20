import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { describe, expect, it, vi } from 'vitest';

import { translated, type TranslatedString } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkTranslatedStringEditorComponent } from './spark-translated-string-editor.component';
import { SparkTranslatedStringComponent } from './spark-translated-string.component';
import { canonicalLanguageTag, isPlainEntry } from './language-label';

function language(current = 'en', languages: Record<string, TranslatedString> = { en: translated({ en: 'English' }), nl: translated({ en: 'Dutch' }) }) {
  return { t: (k: string) => k, language: signal(current), languages: signal(languages) };
}

function create<T>(type: new (...args: any[]) => T, inputs: Record<string, unknown>, lang = language()) {
  TestBed.configureTestingModule({ providers: [{ provide: SparkLanguageService, useValue: lang }] });
  const fixture = TestBed.createComponent(type);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.detectChanges();
  return fixture;
}

const inputs = (fixture: any): HTMLInputElement[] =>
  fixture.debugElement.queryAll(By.css('input')).map((d: any) => d.nativeElement);

describe('SparkTranslatedStringComponent (detail view)', () => {
  it('shows the text in the current language', () => {
    const fixture = create(SparkTranslatedStringComponent, { value: translated({ en: 'Car', nl: 'Auto' }) }, language('nl'));
    expect(fixture.nativeElement.querySelector('.spark-translated-string-text').textContent).toBe('Auto');
  });

  it('falls back through the RFC 4647 chain (nl-BE → nl)', () => {
    const fixture = create(SparkTranslatedStringComponent, { value: translated({ en: 'Car', nl: 'Auto' }) }, language('nl-BE'));
    expect(fixture.nativeElement.querySelector('.spark-translated-string-text').textContent).toBe('Auto');
  });

  it('lists every entry, with its kind, when there are several', () => {
    const value: TranslatedString = { Entries: [
      { Tag: 'nl', Value: "'s-Gravenhage", Kind: 'official' },
      { Tag: 'nl', Value: 'Den Haag', Kind: 'common' },
    ] };
    const fixture = create(SparkTranslatedStringComponent, { value }, language('nl'));
    const entries = fixture.debugElement.queryAll(By.css('.spark-translated-string-entry'));
    expect(entries).toHaveLength(2);
    expect(entries[1].nativeElement.textContent).toContain('spark.translatedString.kind.common');
  });

  it.each([null, '', 'not a translated string'])('renders %j as unset', value => {
    const fixture = create(SparkTranslatedStringComponent, { value });
    expect(fixture.nativeElement.textContent.trim()).toBe('-');
  });
});

describe('SparkTranslatedStringEditorComponent', () => {
  it('draws one input per app language, holding its text', () => {
    const fixture = create(SparkTranslatedStringEditorComponent, { value: translated({ en: 'Car' }) });
    // en, nl, then the "add a language" input.
    expect(inputs(fixture).map(i => i.value)).toEqual(['Car', '', '']);
  });

  it('adds a row for a language the value has beyond the app languages', () => {
    const fixture = create(SparkTranslatedStringEditorComponent, { value: translated({ en: 'Taipei', 'zh-Hant': '臺北' }) });
    expect(inputs(fixture).map(i => i.value)).toEqual(['Taipei', '', '臺北', '']);
  });

  it('emits a new value with the edited language, keeping the others and the names with a kind', () => {
    const value: TranslatedString = { Entries: [
      { Tag: 'en', Value: 'The Hague' },
      { Tag: 'nl', Value: "'s-Gravenhage", Kind: 'official' },
    ] };
    const fixture = create(SparkTranslatedStringEditorComponent, { value });
    const emitted = vi.fn();
    fixture.componentInstance.valueChange.subscribe(emitted);

    fixture.componentInstance.update('nl', 'Den Haag');

    expect(emitted).toHaveBeenCalledWith({ Entries: [
      { Tag: 'en', Value: 'The Hague' },
      { Tag: 'nl', Value: "'s-Gravenhage", Kind: 'official' },
      { Tag: 'nl', Value: 'Den Haag' },
    ] });
    expect(value.Entries).toHaveLength(2);
  });

  it('clearing a language sends it with an empty text, which the server takes as "remove"', () => {
    const fixture = create(SparkTranslatedStringEditorComponent, { value: translated({ en: 'Car', nl: 'Auto' }) });
    const emitted = vi.fn();
    fixture.componentInstance.valueChange.subscribe(emitted);

    fixture.componentInstance.update('nl', '');

    expect(emitted).toHaveBeenCalledWith(translated({ en: 'Car', nl: '' }));
  });

  it('adds any well-formed language tag in canonical form, and refuses an ill-formed one', () => {
    const fixture = create(SparkTranslatedStringEditorComponent, { value: null });
    const component = fixture.componentInstance as any;

    component.newTag.set('not a tag');
    component.addLanguage();
    expect(component.newTagInvalid()).toBe(true);

    component.newTag.set('zh-hant');
    component.addLanguage();
    fixture.detectChanges();
    expect(component.rows().map((r: { tag: string }) => r.tag)).toEqual(['en', 'nl', 'zh-Hant']);
  });

  it('shows the texts without inputs when read-only', () => {
    const fixture = create(SparkTranslatedStringEditorComponent, { value: translated({ en: 'Car' }), readonly: true });
    expect(inputs(fixture)).toHaveLength(0);
    expect(fixture.nativeElement.textContent).toContain('Car');
  });
});

describe('language helpers', () => {
  it('canonicalLanguageTag uses Intl and rejects junk', () => {
    expect(canonicalLanguageTag('NL-be')).toBe('nl-BE');
    expect(canonicalLanguageTag('')).toBeNull();
    expect(canonicalLanguageTag('en_US!')).toBeNull();
  });

  it('isPlainEntry mirrors TranslatedStringEntry.IsPlain', () => {
    expect(isPlainEntry({ Tag: 'en', Value: 'x' })).toBe(true);
    expect(isPlainEntry({ Tag: 'en', Value: 'x', Source: 'register' })).toBe(true);
    expect(isPlainEntry({ Tag: 'en', Value: 'x', Kind: 'common' })).toBe(false);
    expect(isPlainEntry({ Tag: 'en', Value: 'x', ValidTo: '2025-01-01' })).toBe(false);
  });
});
