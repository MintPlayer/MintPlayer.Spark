import { describe, expect, it } from 'vitest';
import { QueryCellValuePipe } from './query-cell.pipe';
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
