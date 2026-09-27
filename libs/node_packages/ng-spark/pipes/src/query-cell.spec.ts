import { describe, expect, it } from 'vitest';
import { QueryCellValuePipe, QueryReferenceChipsPipe } from './query-cell.pipe';
import { QueryColumn, QueryResultItem } from '@mintplayer/ng-spark/models';

/**
 * How one query-grid cell resolves its display text — specifically, which server-sent breadcrumbs
 * are labels and which are placeholders.
 *
 * `EntityMapper` never emits an empty breadcrumb: when a type's `[Breadcrumb]` template renders
 * blank it substitutes the CLR type name, and `QueryResultProjector` forwards that as the cell's
 * `breadcrumb` for a single AsDetail column. This pipe returned it verbatim, so a `Repository`
 * whose `Gate.ProjectMode` was unset rendered the literal "GateSettings" in the Gate column (#384).
 *
 * The filter is deliberately narrow: `asDetailType` is undefined on every other kind of column, so
 * a Reference cell's server-resolved label is never second-guessed here.
 */

const column = (over: Partial<QueryColumn> = {}): QueryColumn =>
  ({ name: 'Gate', dataType: 'AsDetail', isArray: false, order: 1, asDetailType: 'CodeCoverage.Entities.GateSettings', ...over }) as any;

const itemWith = (name: string, cell: Record<string, unknown>): QueryResultItem =>
  ({ id: 'Repositories/1', values: [{ key: name, ...cell }] }) as any;

const run = (col: QueryColumn, item: QueryResultItem) => new QueryCellValuePipe().transform(col, item, {});

describe('QueryCellValuePipe — AsDetail breadcrumbs', () => {
  it('uses the breadcrumb the server resolved', () => {
    expect(run(column(), itemWith('Gate', { value: {}, breadcrumb: 'auto' }))).toBe('auto');
  });

  /** The regression: before the fix this returned "GateSettings". */
  it('does not print the CLR type name the server substitutes for a blank template', () => {
    expect(run(column(), itemWith('Gate', { value: {}, breadcrumb: 'GateSettings' }))).toBe('');
  });

  it('filters the short name even though the column carries the full CLR name', () => {
    // The two shapes differ by design — `asDetailType` is the full CLR name, the placeholder is
    // always the short one — so the comparison has to be on the last dotted segment.
    const col = column({ asDetailType: 'Some.Deeply.Nested.Namespace.GateSettings' });

    expect(run(col, itemWith('Gate', { value: {}, breadcrumb: 'GateSettings' }))).toBe('');
  });

  it('keeps a real breadcrumb that merely resembles the type name', () => {
    expect(run(column(), itemWith('Gate', { value: {}, breadcrumb: 'GateSettings v2' }))).toBe('GateSettings v2');
  });

  it('leaves a reference cell alone — no asDetailType, nothing to compare', () => {
    // A Reference column's label is the server's answer and is never a type-name placeholder;
    // filtering here would blank a legitimate row whose label happened to match a type.
    const col = column({ name: 'Account', dataType: 'Reference', asDetailType: undefined, referenceType: 'CodeCoverage.Entities.Account' });

    expect(run(col, itemWith('Account', { value: 'Accounts/1', breadcrumb: 'Account' }))).toBe('Account');
  });

  it('still counts an AsDetail array cell', () => {
    // Array cells carry a count, never a child breadcrumb — unchanged by the filter.
    const col = column({ name: 'Columns', isArray: true, asDetailType: 'CodeCoverage.Entities.ProjectColumn' });

    expect(run(col, itemWith('Columns', { value: 4 }))).toBe('4 items');
  });
});

describe('QueryCellValuePipe — value kinds', () => {
  const plain = (over: Partial<QueryColumn>) => column({ name: 'X', asDetailType: undefined, isArray: false, ...over });

  it('returns empty text for a missing row or a cell the row does not carry', () => {
    const col = plain({ dataType: 'string' });
    expect(new QueryCellValuePipe().transform(col, null, {})).toBe('');
    expect(run(col, itemWith('Other', { value: 'x' }))).toBe('');
  });

  it('pluralises an AsDetail count and blanks zero, a non-number and a single-child cell', () => {
    const arr = plain({ dataType: 'AsDetail', isArray: true });
    expect(run(arr, itemWith('X', { value: 1 }))).toBe('1 item');
    expect(run(arr, itemWith('X', { value: 0 }))).toBe('');
    expect(run(arr, itemWith('X', { value: 'lots' }))).toBe('');
    // A single child without a breadcrumb must not be stringified to "[object Object]".
    expect(run(plain({ dataType: 'AsDetail', isArray: false }), itemWith('X', { value: { a: 1 } }))).toBe('');
  });

  describe('lookup columns', () => {
    const lookups = {
      Colors: { name: 'Colors', isTransient: true, displayType: 0, values: [
        { key: '1', values: { en: 'Red' }, isActive: true },
        { key: '2', values: {}, isActive: true },
      ] },
    } as any;
    const col = plain({ dataType: 'string', lookupReferenceType: 'Colors' });
    const cell = (value: unknown) => new QueryCellValuePipe().transform(col, itemWith('X', { value }), lookups);

    it('translates the key to its label, matching a numeric value against the string key', () => {
      expect(cell(1)).toBe('Red');
    });

    it('falls back to the key when the option has no translation', () => {
      expect(cell('2')).toBe('2');
    });

    it('shows the raw value when the key is unknown or the lookup is not loaded', () => {
      expect(cell('9')).toBe('9');
      expect(new QueryCellValuePipe().transform(col, itemWith('X', { value: '1' }), {})).toBe('1');
    });
  });

  it('passes a boolean through and maps an absent one to null (indeterminate, not unchecked)', () => {
    const col = plain({ dataType: 'boolean' });
    expect(run(col, itemWith('X', { value: false }))).toBe(false);
    expect(run(col, itemWith('X', { value: true }))).toBe(true);
    expect(run(col, itemWith('X', {}))).toBeNull();
  });

  describe('date columns', () => {
    const col = plain({ dataType: 'datetime' });

    it('parses an ISO string into a Date at the same instant', () => {
      const result = run(col, itemWith('X', { value: '2026-01-02T03:04:05Z' }));
      expect(result).toBeInstanceOf(Date);
      expect((result as Date).toISOString()).toBe('2026-01-02T03:04:05.000Z');
    });

    it('returns null for an absent value and for an invalid Date instance', () => {
      expect(run(col, itemWith('X', { value: null }))).toBeNull();
      expect(run(col, itemWith('X', { value: '' }))).toBeNull();
      expect(run(plain({ dataType: 'date' }), itemWith('X', { value: new Date('nope') }))).toBeNull();
    });

    it('passes a valid Date instance through unchanged', () => {
      const d = new Date('2026-05-05T00:00:00Z');
      expect(run(col, itemWith('X', { value: d }))).toBe(d);
    });

    it('keeps unparseable text as text rather than "Invalid Date"', () => {
      expect(run(col, itemWith('X', { value: 'someday' }))).toBe('someday');
    });
  });

  it('returns a plain value as-is and an undefined one as empty text', () => {
    const col = plain({ dataType: 'number' });
    expect(run(col, itemWith('X', { value: 42 }))).toBe(42);
    expect(run(col, itemWith('X', {}))).toBe('');
  });
});

describe('QueryReferenceChipsPipe', () => {
  const col = column({ name: 'Tags', dataType: 'Reference', isArray: true, asDetailType: undefined });
  const chips = (cell: Record<string, unknown> | null) =>
    new QueryReferenceChipsPipe().transform(col, cell ? itemWith('Tags', cell) : null);

  it('labels each id from breadcrumbs, falling back to the id, and drops blank ids', () => {
    expect(chips({ value: ['t/1', null, '', 't/2', 3], breadcrumbs: { 't/1': 'One', 't/2': null } })).toEqual([
      { id: 't/1', label: 'One' },
      { id: 't/2', label: 't/2' },
      { id: '3', label: '3' },
    ]);
  });

  it('falls back to ids when the cell carries no breadcrumbs at all', () => {
    expect(chips({ value: ['t/1'] })).toEqual([{ id: 't/1', label: 't/1' }]);
  });

  it('returns no chips for a missing row, a missing cell or a non-array value', () => {
    expect(chips(null)).toEqual([]);
    expect(new QueryReferenceChipsPipe().transform(col, itemWith('Other', { value: ['x'] }))).toEqual([]);
    expect(chips({ value: 't/1' })).toEqual([]);
  });
});
