import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import type { DistinctValue, FilterContext, FilterSelection } from '@mintplayer/ng-bootstrap/datatable';
import type { QueryColumn } from '@mintplayer/ng-spark/models';
import { describe, expect, it, vi } from 'vitest';

import { SparkColumnFilterPanelComponent } from './spark-column-filter-panel.component';

/**
 * The panel holds no filter state: it renders from `ctx.selection` and writes only through `ctx`
 * (docs/column_filter_reopen_selection_PRD.md). A panel that kept its own copy started empty when the
 * datatable recreated it on a reopen, so the grid stayed filtered with nothing ticked, and the next tick
 * replaced the selection instead of extending it.
 */
const ada: DistinctValue = { value: 'Ada', label: 'Ada' };
const bob: DistinctValue = { value: 'Bob', label: 'Bob' };

function context(selection: Partial<FilterSelection> = {}, extra: Partial<FilterContext> = {}): FilterContext {
  return {
    values: { matching: [ada, bob], remaining: [], hasMore: false },
    loading: false,
    selection: { values: [], inverse: false, ...selection },
    term: '',
    readonly: false,
    search: vi.fn(),
    apply: vi.fn(),
    compare: vi.fn(),
    clear: vi.fn(),
    close: vi.fn(),
    ...extra,
  } as FilterContext;
}

function create(ctx: FilterContext, column: Partial<QueryColumn> = {}) {
  TestBed.configureTestingModule({ providers: [provideNoopAnimations()] });
  const fixture = TestBed.createComponent(SparkColumnFilterPanelComponent);
  fixture.componentRef.setInput('column', { name: 'Name', ...column } as QueryColumn);
  fixture.componentRef.setInput('ctx', ctx);
  fixture.componentRef.setInput('values', ctx.values);
  fixture.detectChanges();
  return fixture;
}

const boxes = (root: HTMLElement) => [...root.querySelectorAll<HTMLInputElement>('bs-checkbox.spark-filter-value input')];

describe('SparkColumnFilterPanelComponent', () => {
  it('ticks the values the datatable says are selected, on a freshly created panel', () => {
    const fixture = create(context({ values: [bob] }));

    expect(boxes(fixture.nativeElement).map(b => b.checked)).toEqual([false, true]);
  });

  it('adds a tick to the existing selection instead of replacing it', () => {
    const ctx = context({ values: [bob] });
    const fixture = create(ctx);

    boxes(fixture.nativeElement)[0].click();

    expect(ctx.apply).toHaveBeenCalledWith([bob, ada], false);
  });

  it('removes an untick from the existing selection', () => {
    const ctx = context({ values: [ada, bob] });
    const fixture = create(ctx);

    boxes(fixture.nativeElement)[0].click();

    expect(ctx.apply).toHaveBeenCalledWith([bob], false);
  });

  it('follows a selection changed from outside the panel', () => {
    const fixture = create(context({ values: [] }));

    fixture.componentRef.setInput('ctx', context({ values: [ada] }));
    fixture.detectChanges();

    expect(boxes(fixture.nativeElement).map(b => b.checked)).toEqual([true, false]);
  });

  it('shows the inverse from the datatable and flips it through apply', () => {
    const ctx = context({ values: [ada], inverse: true });
    const fixture = create(ctx);
    const inverse: HTMLButtonElement = fixture.nativeElement.querySelector('.spark-filter-inverse');

    expect(inverse.getAttribute('aria-pressed')).toBe('true');
    inverse.click();
    expect(ctx.apply).toHaveBeenCalledWith([ada], false);
  });

  it("shows the datatable's search term, so a kept panel never shows text the table has reset", () => {
    const fixture = create(context({}, { term: 'ad' }));
    const search: HTMLInputElement = fixture.nativeElement.querySelector('.filter-search');

    expect(search.value).toBe('ad');
  });

  it('seeds the free-text box from the applied text of a non-listable column', () => {
    const fixture = create(context({ values: [{ value: 'smith', label: 'smith' }] }), { canListDistincts: false });
    const text: HTMLInputElement = fixture.nativeElement.querySelector('.filter-search');

    expect(text.value).toBe('smith');
  });

  it('disables every editing control when the filters are read-only, and keeps the search box', () => {
    const fixture = create(context({ values: [ada] }, { readonly: true }));
    const root: HTMLElement = fixture.nativeElement;

    expect(boxes(root).every(b => b.disabled)).toBe(true);
    expect(root.querySelector<HTMLButtonElement>('.spark-filter-inverse')!.disabled).toBe(true);
    expect(root.querySelector<HTMLButtonElement>('.spark-filter-clear')!.disabled).toBe(true);
    expect(root.querySelector<HTMLInputElement>('.filter-search')!.disabled).toBe(false);
  });
});
