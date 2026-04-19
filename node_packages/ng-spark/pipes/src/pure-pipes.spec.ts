import { describe, expect, it } from 'vitest';
import { ArrayValuePipe } from './array-value.pipe';
import { AsDetailCellValuePipe } from './as-detail-cell-value.pipe';
import { AsDetailColumnsPipe } from './as-detail-columns.pipe';
import { AsDetailTypePipe } from './as-detail-type.pipe';
import { AttributeValuePipe } from './attribute-value.pipe';
import { CanCreateDetailRowPipe } from './can-create-detail-row.pipe';
import { CanDeleteDetailRowPipe } from './can-delete-detail-row.pipe';
import { ErrorForAttributePipe } from './error-for-attribute.pipe';
import { InlineRefOptionsPipe } from './inline-ref-options.pipe';
import { InputTypePipe } from './input-type.pipe';
import { LookupDisplayTypePipe } from './lookup-display-type.pipe';
import { LookupDisplayValuePipe } from './lookup-display-value.pipe';
import { LookupOptionsPipe } from './lookup-options.pipe';
import { RawAttributeValuePipe } from './raw-attribute-value.pipe';
import { ReferenceAttrValuePipe } from './reference-attr-value.pipe';
import { ReferenceLinkRoutePipe } from './reference-link-route.pipe';
import { ResolveTranslationPipe } from './resolve-translation.pipe';
import { RouterLinkPipe } from './router-link.pipe';
import { ELookupDisplayType } from '@mintplayer/ng-spark/models';

// ---------------------------------------------------------------------------
// arrayValue
// ---------------------------------------------------------------------------
describe('ArrayValuePipe', () => {
  const pipe = new ArrayValuePipe();

  it('returns the array value of an attribute', () => {
    const item = { attributes: [{ name: 'rows', value: [{ a: 1 }, { a: 2 }] }] } as any;
    expect(pipe.transform('rows', item)).toEqual([{ a: 1 }, { a: 2 }]);
  });

  it('returns empty array when attribute missing', () => {
    expect(pipe.transform('rows', { attributes: [] } as any)).toEqual([]);
  });

  it('returns empty array when value is not an array', () => {
    const item = { attributes: [{ name: 'rows', value: 'not-array' }] } as any;
    expect(pipe.transform('rows', item)).toEqual([]);
  });

  it('returns empty array when item is null', () => {
    expect(pipe.transform('rows', null)).toEqual([]);
  });
});

// ---------------------------------------------------------------------------
// asDetailCellValue
// ---------------------------------------------------------------------------
describe('AsDetailCellValuePipe', () => {
  const pipe = new AsDetailCellValuePipe();

  it('returns empty string for null cell value', () => {
    expect(pipe.transform({ City: null }, { name: 'addr' } as any, { name: 'City', dataType: 'string' } as any, {})).toBe('');
  });

  it('returns the raw value for non-reference cells', () => {
    expect(pipe.transform({ City: 'Brussels' }, { name: 'addr' } as any, { name: 'City', dataType: 'string' } as any, {})).toBe('Brussels');
  });

  it('resolves Reference value via asDetailRefOptions breadcrumb', () => {
    const refOptions = { addr: { Country: [{ id: 'BE', breadcrumb: 'Belgium', name: 'BE' } as any] } };
    const result = pipe.transform(
      { Country: 'BE' },
      { name: 'addr' } as any,
      { name: 'Country', dataType: 'Reference', query: 'countries' } as any,
      refOptions);
    expect(result).toBe('Belgium');
  });

  it('falls back to raw value when reference id not found in options', () => {
    const result = pipe.transform(
      { Country: 'XX' },
      { name: 'addr' } as any,
      { name: 'Country', dataType: 'Reference', query: 'countries' } as any,
      { addr: { Country: [] } });
    expect(result).toBe('XX');
  });
});

// ---------------------------------------------------------------------------
// asDetailColumns
// ---------------------------------------------------------------------------
describe('AsDetailColumnsPipe', () => {
  const pipe = new AsDetailColumnsPipe();

  it('returns visible attributes sorted by order', () => {
    const types = {
      addr: {
        attributes: [
          { name: 'City', isVisible: true, order: 2 },
          { name: 'Hidden', isVisible: false, order: 1 },
          { name: 'Street', isVisible: true, order: 1 },
        ],
      },
    } as any;
    const result = pipe.transform({ name: 'addr' } as any, types);
    expect(result.map(c => c.name)).toEqual(['Street', 'City']);
  });

  it('returns empty when type not found', () => {
    expect(pipe.transform({ name: 'addr' } as any, {})).toEqual([]);
  });
});

// ---------------------------------------------------------------------------
// asDetailType
// ---------------------------------------------------------------------------
describe('AsDetailTypePipe', () => {
  const pipe = new AsDetailTypePipe();

  it('returns the matching type', () => {
    const type = { id: '1', name: 'Address' } as any;
    expect(pipe.transform({ name: 'addr' } as any, { addr: type })).toBe(type);
  });

  it('returns null when not found', () => {
    expect(pipe.transform({ name: 'addr' } as any, {})).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// attributeValue (covers most decision branches; complex pipe)
// ---------------------------------------------------------------------------
describe('AttributeValuePipe', () => {
  const pipe = new AttributeValuePipe();

  it('returns empty string when item is null', () => {
    expect(pipe.transform('Name', null, null, {}, [])).toBe('');
  });

  it('returns breadcrumb when present', () => {
    const item = { attributes: [{ name: 'Owner', value: 'people/1', breadcrumb: 'Alice' }] } as any;
    expect(pipe.transform('Owner', item, null, {}, [])).toBe('Alice');
  });

  it('formats AsDetail array as count summary (plural)', () => {
    const item = { attributes: [{ name: 'Jobs', value: [{}, {}, {}] }] } as any;
    const entityType = { attributes: [{ name: 'Jobs', dataType: 'AsDetail' }] } as any;
    expect(pipe.transform('Jobs', item, entityType, {}, [])).toBe('3 items');
  });

  it('formats AsDetail array singular', () => {
    const item = { attributes: [{ name: 'Jobs', value: [{}] }] } as any;
    const entityType = { attributes: [{ name: 'Jobs', dataType: 'AsDetail' }] } as any;
    expect(pipe.transform('Jobs', item, entityType, {}, [])).toBe('1 item');
  });

  it('resolves lookup reference value via translation', () => {
    const item = { attributes: [{ name: 'Status', value: 'active' }] } as any;
    const entityType = { attributes: [{ name: 'Status', lookupReferenceType: 'StatusRef' }] } as any;
    const lookupRefOptions = {
      StatusRef: { values: [{ key: 'active', values: { en: 'Active', fr: 'Actif' } }] },
    } as any;
    expect(pipe.transform('Status', item, entityType, lookupRefOptions, [])).toBe('Active');
  });

  it('returns raw value as fallback', () => {
    const item = { attributes: [{ name: 'Name', value: 'Acme' }] } as any;
    expect(pipe.transform('Name', item, null, {}, [])).toBe('Acme');
  });
});

// ---------------------------------------------------------------------------
// canCreateDetailRow / canDeleteDetailRow
// ---------------------------------------------------------------------------
describe('CanCreateDetailRowPipe', () => {
  const pipe = new CanCreateDetailRowPipe();
  it('returns the canCreate permission', () => {
    expect(pipe.transform({ name: 'rows' } as any, { rows: { canCreate: false } as any })).toBe(false);
  });
  it('defaults to true when no permission entry exists', () => {
    expect(pipe.transform({ name: 'rows' } as any, {})).toBe(true);
  });
});

describe('CanDeleteDetailRowPipe', () => {
  const pipe = new CanDeleteDetailRowPipe();
  it('returns the canDelete permission', () => {
    expect(pipe.transform({ name: 'rows' } as any, { rows: { canDelete: true } as any })).toBe(true);
  });
  it('defaults to true when no permission entry exists', () => {
    expect(pipe.transform({ name: 'rows' } as any, {})).toBe(true);
  });
});

// ---------------------------------------------------------------------------
// errorForAttribute
// ---------------------------------------------------------------------------
describe('ErrorForAttributePipe', () => {
  const pipe = new ErrorForAttributePipe();
  it('returns the resolved error message for the attribute', () => {
    const errors = [{ attributeName: 'Email', errorMessage: { en: 'Invalid email' } } as any];
    expect(pipe.transform('Email', errors)).toBe('Invalid email');
  });
  it('returns null when no error matches', () => {
    expect(pipe.transform('Email', [])).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// inlineRefOptions
// ---------------------------------------------------------------------------
describe('InlineRefOptionsPipe', () => {
  const pipe = new InlineRefOptionsPipe();
  it('returns the nested option list', () => {
    const opts = { addr: { Country: [{ id: '1' } as any] } };
    expect(pipe.transform({ name: 'addr' } as any, { name: 'Country' } as any, opts)).toEqual([{ id: '1' }]);
  });
  it('returns empty when no entry', () => {
    expect(pipe.transform({ name: 'addr' } as any, { name: 'Country' } as any, {})).toEqual([]);
  });
});

// ---------------------------------------------------------------------------
// inputType
// ---------------------------------------------------------------------------
describe('InputTypePipe', () => {
  const pipe = new InputTypePipe();
  it.each([
    ['number', 'number'],
    ['decimal', 'number'],
    ['boolean', 'checkbox'],
    ['datetime', 'datetime-local'],
    ['date', 'date'],
    ['color', 'color'],
    ['string', 'text'],
    ['unknown-type', 'text'],
  ])('maps %s → %s', (input, expected) => {
    expect(pipe.transform(input)).toBe(expected);
  });
});

// ---------------------------------------------------------------------------
// lookupDisplayType
// ---------------------------------------------------------------------------
describe('LookupDisplayTypePipe', () => {
  const pipe = new LookupDisplayTypePipe();
  it('returns the lookup ref displayType', () => {
    const opts = { StatusRef: { displayType: ELookupDisplayType.Modal } } as any;
    expect(pipe.transform({ lookupReferenceType: 'StatusRef' } as any, opts)).toBe(ELookupDisplayType.Modal);
  });
  it('defaults to Dropdown when no lookupReferenceType', () => {
    expect(pipe.transform({} as any, {})).toBe(ELookupDisplayType.Dropdown);
  });
});

// ---------------------------------------------------------------------------
// lookupDisplayValue
// ---------------------------------------------------------------------------
describe('LookupDisplayValuePipe', () => {
  const pipe = new LookupDisplayValuePipe();
  it('returns the resolved translation for the selected key', () => {
    const opts = {
      StatusRef: { values: [{ key: 'active', isActive: true, values: { en: 'Active' } }] },
    } as any;
    expect(pipe.transform({ name: 'Status', lookupReferenceType: 'StatusRef' } as any, { Status: 'active' }, opts)).toBe('Active');
  });
  it('returns empty string when value is missing', () => {
    expect(pipe.transform({ name: 'Status' } as any, {}, {})).toBe('');
  });
  it('returns the raw key when selection not found in options', () => {
    expect(pipe.transform({ name: 'Status', lookupReferenceType: 'X' } as any, { Status: 'foo' }, { X: { values: [] } } as any)).toBe('foo');
  });
});

// ---------------------------------------------------------------------------
// lookupOptions
// ---------------------------------------------------------------------------
describe('LookupOptionsPipe', () => {
  const pipe = new LookupOptionsPipe();
  it('returns active values', () => {
    const opts = {
      StatusRef: {
        values: [
          { key: 'a', isActive: true },
          { key: 'b', isActive: false },
          { key: 'c', isActive: true },
        ],
      },
    } as any;
    const result = pipe.transform({ lookupReferenceType: 'StatusRef' } as any, opts);
    expect(result.map(v => v.key)).toEqual(['a', 'c']);
  });

  it('returns empty when no lookupReferenceType', () => {
    expect(pipe.transform({} as any, {})).toEqual([]);
  });
});

// ---------------------------------------------------------------------------
// rawAttributeValue
// ---------------------------------------------------------------------------
describe('RawAttributeValuePipe', () => {
  const pipe = new RawAttributeValuePipe();
  it('returns the raw value', () => {
    const item = { attributes: [{ name: 'X', value: 42 }] } as any;
    expect(pipe.transform('X', item)).toBe(42);
  });
  it('returns undefined when not found', () => {
    expect(pipe.transform('X', { attributes: [] } as any)).toBeUndefined();
  });
  it('handles null item', () => {
    expect(pipe.transform('X', null)).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// referenceAttrValue
// ---------------------------------------------------------------------------
describe('ReferenceAttrValuePipe', () => {
  const pipe = new ReferenceAttrValuePipe();
  it('prefers breadcrumb over raw value', () => {
    const item = { attributes: [{ name: 'Owner', value: 'p/1', breadcrumb: 'Alice' }] } as any;
    expect(pipe.transform(item, 'Owner')).toBe('Alice');
  });
  it('falls back to raw value when breadcrumb absent', () => {
    const item = { attributes: [{ name: 'Owner', value: 'p/1' }] } as any;
    expect(pipe.transform(item, 'Owner')).toBe('p/1');
  });
  it('returns empty when attribute missing', () => {
    expect(pipe.transform({ attributes: [] } as any, 'Owner')).toBe('');
  });
});

// ---------------------------------------------------------------------------
// referenceLinkRoute
// ---------------------------------------------------------------------------
describe('ReferenceLinkRoutePipe', () => {
  const pipe = new ReferenceLinkRoutePipe();
  it('builds /po/{alias}/{id} route', () => {
    const types = [{ id: 't1', clrType: 'TestApp.Person', alias: 'person' }] as any;
    expect(pipe.transform('TestApp.Person', 'people/1', types)).toEqual(['/po', 'person', 'people/1']);
  });
  it('falls back to id when alias missing', () => {
    const types = [{ id: 't1', clrType: 'TestApp.Person' }] as any;
    expect(pipe.transform('TestApp.Person', 'people/1', types)).toEqual(['/po', 't1', 'people/1']);
  });
  it('returns null when target type not found', () => {
    expect(pipe.transform('Unknown', 'p/1', [])).toBeNull();
  });
  it('returns null when referenceId is empty', () => {
    expect(pipe.transform('TestApp.Person', null, [])).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// resolveTranslation
// ---------------------------------------------------------------------------
describe('ResolveTranslationPipe', () => {
  const pipe = new ResolveTranslationPipe();
  it('resolves a TranslatedString', () => {
    expect(pipe.transform({ en: 'Hello' } as any)).toBe('Hello');
  });
  it('returns the fallback when value is undefined', () => {
    expect(pipe.transform(undefined, 'fallback')).toBe('fallback');
  });
  it('returns empty string when no value and no fallback', () => {
    expect(pipe.transform(undefined)).toBe('');
  });
});

// ---------------------------------------------------------------------------
// routerLink
// ---------------------------------------------------------------------------
describe('RouterLinkPipe', () => {
  const pipe = new RouterLinkPipe();
  it('builds /query/{alias} for query units', () => {
    expect(pipe.transform({ type: 'query', alias: 'cars' } as any)).toEqual(['/query', 'cars']);
  });
  it('builds /po/{alias} for persistentObject units', () => {
    expect(pipe.transform({ type: 'persistentObject', alias: 'person' } as any)).toEqual(['/po', 'person']);
  });
  it('falls back to /query/{queryId} when alias missing', () => {
    expect(pipe.transform({ type: 'query', queryId: 'q-uuid' } as any)).toEqual(['/query', 'q-uuid']);
  });
  it('returns / for unknown type', () => {
    expect(pipe.transform({ type: 'unknown' } as any)).toEqual(['/']);
  });
});
