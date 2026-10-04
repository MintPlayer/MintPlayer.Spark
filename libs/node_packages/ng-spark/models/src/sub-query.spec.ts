import { describe, expect, it } from 'vitest';
import { normalizeSubQuery, subQueriesOf } from './sub-query';
import { selectionModeFor } from './selection-mode';
import { defaultQueryActions, filterDetailActions, filterQueryActions } from './query-actions';
import { CustomActionDefinition } from './custom-action';

const action = (name: string, showedOn: string, selectionRule?: string, isDefault?: boolean) =>
  ({ name, label: { en: name }, showedOn, selectionRule, refreshOnCompleted: false, offset: 0, isDefault }) as CustomActionDefinition;

describe('sub-query entries (#460 M15, D17)', () => {
  it('reads a bare alias and the object form alike', () => {
    expect(normalizeSubQuery('company-cars')).toEqual({ query: 'company-cars' });
    expect(normalizeSubQuery({ query: 'company-people', selectionMode: 'none' }))
      .toEqual({ query: 'company-people', selectionMode: 'none' });
  });

  it('lists a type\'s sub-queries in object form, and none for a type without', () => {
    expect(subQueriesOf({ queries: ['a', { query: 'b', selectionMode: 'none' }] }).map(e => e.query)).toEqual(['a', 'b']);
    expect(subQueriesOf({})).toEqual([]);
    expect(subQueriesOf(null)).toEqual([]);
  });
});

describe('selectionModeFor with a declared mode', () => {
  const gated = [action('Copy', 'query', '>=1')];

  it('a declared mode wins outright', () => {
    expect(selectionModeFor(gated, 'none')).toBe('none');
    expect(selectionModeFor([], 'multiple')).toBe('multiple');
  });

  it('auto and absent derive from the actions (#467 R1): any rule accepting a row gives multiple', () => {
    expect(selectionModeFor(gated, 'auto')).toBe('multiple');
    expect(selectionModeFor(gated)).toBe('multiple');
    expect(selectionModeFor([action('One', 'query', '=1')], null)).toBe('multiple');
    expect(selectionModeFor([action('Delete', 'both', '>0', true)])).toBe('multiple');
    expect(selectionModeFor([action('Refresh', 'query')])).toBe('none');
    // =0 accepts no row, so it never makes a list selectable (S2).
    expect(selectionModeFor([action('Scatter', 'query', '=0')])).toBe('none');
    expect(selectionModeFor([action('Big', 'query', '>500')])).toBe('none');
  });
});

describe('action filters', () => {
  const actions = [
    action('New', 'both', undefined, true),
    action('Delete', 'both', '>0', true),
    action('Export', 'Both'),
    action('Close', 'DETAIL'),
    action('Scatter', 'query', '=0'),
  ];

  it('compares showedOn case-insensitively', () => {
    expect(filterQueryActions(actions).map(a => a.name)).toEqual(['Export', 'Scatter']);
    expect(filterDetailActions(actions).map(a => a.name)).toEqual(['Export', 'Close']);
  });

  it('keeps the default New and Delete apart from the custom actions', () => {
    expect(defaultQueryActions(actions).map(a => a.name)).toEqual(['New', 'Delete']);
    expect(defaultQueryActions([action('Delete', 'detail', '>0', true)])).toEqual([]);
  });
});
