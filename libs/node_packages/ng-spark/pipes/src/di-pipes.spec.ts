import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach } from 'vitest';

import { TranslateKeyPipe } from './translate-key.pipe';
import { ResolveTranslationPipe } from './resolve-translation.pipe';
import { AsDetailDisplayValuePipe } from './as-detail-display-value.pipe';
import { ReferenceDisplayValuePipe } from './reference-display-value.pipe';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

class FakeLanguageService {
  t(key: string): string {
    const map: Record<string, string> = {
      notSet: '(not set)',
      notSelected: '(not selected)',
      clickToEdit: '(click to edit)',
      hello: 'Hello',
    };
    return map[key] ?? key;
  }
}

function createPipe<T>(pipeType: new (...args: any[]) => T): T {
  TestBed.configureTestingModule({
    providers: [
      { provide: SparkLanguageService, useClass: FakeLanguageService },
      pipeType as any,
    ],
  });
  return TestBed.inject(pipeType as any) as T;
}

describe('TranslateKeyPipe', () => {
  it('returns the translated string for the given key', () => {
    const pipe = createPipe(TranslateKeyPipe);
    expect(pipe.transform('hello')).toBe('Hello');
  });

  it('falls back to the key itself when not found', () => {
    const pipe = createPipe(TranslateKeyPipe);
    expect(pipe.transform('unknown.key')).toBe('unknown.key');
  });
});

// ResolveTranslationPipe is pure (no DI), but kept here next to its language-aware sibling for symmetry
describe('ResolveTranslationPipe (DI-free smoke)', () => {
  it('resolves nested translations directly', () => {
    const pipe = new ResolveTranslationPipe();
    expect(pipe.transform({ en: 'Hello' } as any)).toBe('Hello');
  });
});

describe('AsDetailDisplayValuePipe', () => {
  it('returns the (not set) translation when value is missing', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    expect(pipe.transform(attr, {}, {})).toBe('(not set)');
  });

  it('formats via displayFormat template when type defines one', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    const types = { addr: { displayFormat: '{Street}, {City}' } } as any;
    const formData = { addr: { Street: 'Main', City: 'Brussels' } };
    expect(pipe.transform(attr, formData, types)).toBe('Main, Brussels');
  });

  it('uses displayAttribute when no displayFormat', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    const types = { addr: { displayAttribute: 'City' } } as any;
    expect(pipe.transform(attr, { addr: { City: 'Brussels' } }, types)).toBe('Brussels');
  });

  it('falls back to common property names (Name)', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    expect(pipe.transform(attr, { addr: { Name: 'Acme' } }, {})).toBe('Acme');
  });

  it('falls back to (click to edit) translation when nothing matches', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    expect(pipe.transform(attr, { addr: { Unknown: 'x' } }, {})).toBe('(click to edit)');
  });
});

describe('ReferenceDisplayValuePipe', () => {
  it('returns the (not selected) translation when no id is selected', () => {
    const pipe = createPipe(ReferenceDisplayValuePipe);
    expect(pipe.transform({ name: 'Owner' } as any, {}, {})).toBe('(not selected)');
  });

  it('returns the breadcrumb of the matching option', () => {
    const pipe = createPipe(ReferenceDisplayValuePipe);
    const opts = { Owner: [{ id: 'p/1', breadcrumb: 'Alice', name: 'p1' } as any] };
    expect(pipe.transform({ name: 'Owner' } as any, { Owner: 'p/1' }, opts)).toBe('Alice');
  });

  it('falls back to name when no breadcrumb', () => {
    const pipe = createPipe(ReferenceDisplayValuePipe);
    const opts = { Owner: [{ id: 'p/1', name: 'Alice' } as any] };
    expect(pipe.transform({ name: 'Owner' } as any, { Owner: 'p/1' }, opts)).toBe('Alice');
  });

  it('returns the raw id when no matching option', () => {
    const pipe = createPipe(ReferenceDisplayValuePipe);
    expect(pipe.transform({ name: 'Owner' } as any, { Owner: 'p/missing' }, { Owner: [] })).toBe('p/missing');
  });
});
