import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach } from 'vitest';

import { TranslateKeyPipe } from './translate-key.pipe';
import { ResolveTranslationPipe } from './resolve-translation.pipe';
import { AsDetailDisplayValuePipe } from './as-detail-display-value.pipe';
import { ReferenceDisplayValuePipe } from './reference-display-value.pipe';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { AS_DETAIL_SELF_BREADCRUMB_KEY } from '@mintplayer/ng-spark/models';

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

  it('formats via the breadcrumb template when the type defines one', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    const types = { addr: { breadcrumb: '{Street}, {City}' } } as any;
    const formData = { addr: { Street: 'Main', City: 'Brussels' } };
    expect(pipe.transform(attr, formData, types)).toBe('Main, Brussels');
  });

  it('resolves a single-placeholder breadcrumb to the property value', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    const types = { addr: { breadcrumb: '{City}' } } as any;
    expect(pipe.transform(attr, { addr: { City: 'Brussels' } }, types)).toBe('Brussels');
  });

  it('falls back to (click to edit) when no breadcrumb is defined', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    expect(pipe.transform(attr, { addr: { Name: 'Acme' } }, {})).toBe('(click to edit)');
  });

  /**
   * The reported bug. HR's `Address` renders its breadcrumb as `{Crumb}`, and `Crumb` is
   * `[Breadcrumb, IgnoreProperty]` — a computed property deliberately kept OUT of the model. No
   * client can substitute it, for any row, ever; only the server can, by reflecting over the CLR
   * property. The edit form showed `(click to edit)` where the detail page showed the address.
   */
  it('prefers the server-resolved breadcrumb over a template the model cannot satisfy', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    const types = { addr: { name: 'Address', breadcrumb: '{Crumb}' } } as any;
    const formData = {
      addr: {
        Street: 'Abdijsteeg 30', PostalCode: '9700', City: 'Oudenaarde',
        [AS_DETAIL_SELF_BREADCRUMB_KEY]: 'Abdijsteeg 30, 9700 Oudenaarde',
      },
    };

    expect(pipe.transform(attr, formData, types)).toBe('Abdijsteeg 30, 9700 Oudenaarde');
  });

  /**
   * `EntityMapper` never sends an empty breadcrumb: a template that renders blank is replaced with
   * the bare CLR type name (EntityMapper.cs:209-211). Rendering that would be worse than the
   * placeholder, because "Address" reads as real data.
   */
  it('ignores the server placeholder that is just the type name', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    const types = { addr: { name: 'Address', breadcrumb: '{Crumb}' } } as any;
    const formData = { addr: { Street: '', [AS_DETAIL_SELF_BREADCRUMB_KEY]: 'Address' } };

    expect(pipe.transform(attr, formData, types)).toBe('(click to edit)');
  });

  /** The create path has no server object yet, so substitution stays the only strategy there. */
  it('still substitutes client-side when no server breadcrumb was carried', () => {
    const pipe = createPipe(AsDetailDisplayValuePipe);
    const attr = { name: 'addr' } as any;
    const types = { addr: { name: 'Address', breadcrumb: '{Street}, {City}' } } as any;

    expect(pipe.transform(attr, { addr: { Street: 'Main', City: 'Brussels' } }, types)).toBe('Main, Brussels');
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

  it('falls back to the raw id when an option carries no breadcrumb', () => {
    // A row carries a server-resolved `breadcrumb` and nothing else to display by; the old
    // `name` fallback went with the persistent-object row shape (#327 M4).
    const pipe = createPipe(ReferenceDisplayValuePipe);
    const opts = { Owner: [{ id: 'p/1', values: [] } as any] };
    expect(pipe.transform({ name: 'Owner' } as any, { Owner: 'p/1' }, opts)).toBe('p/1');
  });

  it('returns the raw id when no matching option', () => {
    const pipe = createPipe(ReferenceDisplayValuePipe);
    expect(pipe.transform({ name: 'Owner' } as any, { Owner: 'p/missing' }, { Owner: [] })).toBe('p/missing');
  });
});
