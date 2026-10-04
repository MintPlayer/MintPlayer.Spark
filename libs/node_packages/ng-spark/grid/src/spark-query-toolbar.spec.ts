import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { By } from '@angular/platform-browser';
import { BsDatatableComponent } from '@mintplayer/ng-bootstrap/datatable';

import { SparkQueryGridComponent } from './spark-query-grid.component';
import { SparkQueryCardComponent } from './spark-query-card.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { CustomActionDefinition, EntityType, QueryResultItem, ShowedOn } from '@mintplayer/ng-spark/models';
import { settle } from '../../src/test-utils';

/**
 * #460 M15 — the toolbar model both hosts render (New, Delete, custom actions), the selection bar,
 * the per-row menu, and the card's header order.
 */

const answerType: EntityType = {
  id: 't-answer', name: 'Answer', alias: 'answer', clrType: 'QnA.Answer',
  attributes: [
    { id: 'a-body', name: 'Body', dataType: 'string', isVisible: true, isReadOnly: false, isRequired: false,
      order: 1, showedOn: ShowedOn.Query | ShowedOn.PersistentObject } as any,
  ],
} as any;

const answersQuery = {
  id: 'q-answers', name: 'Question_Answers', source: 'Custom.Question_Answers', alias: 'question-answers',
  entityType: 'Answer', sortColumns: [], isStreamingQuery: false, label: { en: 'Answers' },
} as any;

const rows: QueryResultItem[] = [
  { id: 'answers/1', etag: 'e1', values: [{ key: 'Body', value: 'one' }] },
  { id: 'answers/2', etag: 'e2', values: [{ key: 'Body', value: 'two' }] },
  { id: 'answers/3', etag: 'e3', values: [{ key: 'Body', value: 'three' }] },
];

const columns = [{ name: 'Body', dataType: 'string', order: 1 } as any];

const newAction = {
  name: 'New', label: { en: 'New' }, icon: 'plus-lg', showedOn: 'query', offset: 0,
  refreshOnCompleted: false, isDefault: true, variant: 'primary',
} as CustomActionDefinition;
const editAction = {
  name: 'Edit', label: { en: 'Edit' }, icon: 'pencil', showedOn: 'both', selectionRule: '=1', offset: 0,
  refreshOnCompleted: false, isDefault: true,
} as CustomActionDefinition;
const deleteAction = {
  name: 'Delete', label: { en: 'Delete' }, icon: 'trash', showedOn: 'both', selectionRule: '>0', offset: 0,
  refreshOnCompleted: false, isDefault: true, variant: 'danger', confirmation: { en: 'common.confirmDeleteSelected' },
} as CustomActionDefinition;
const duplicateAction = {
  name: 'DuplicateAnswer', label: { en: 'Duplicate' }, showedOn: 'query', selectionRule: '=1', offset: 0,
  refreshOnCompleted: true,
} as CustomActionDefinition;
const exportAction = {
  name: 'Export', label: { en: 'Export' }, showedOn: 'Both', offset: 1, refreshOnCompleted: false,
} as CustomActionDefinition;

const langStub = { t: (k: string) => k, resolve: (v: any) => (typeof v === 'string' ? v : v?.en ?? '') };

function makeService(actions: CustomActionDefinition[], overrides: Record<string, unknown> = {}) {
  return {
    getEntityTypes: vi.fn().mockResolvedValue([answerType]),
    getQueries: vi.fn().mockResolvedValue([answersQuery]),
    getQuery: vi.fn().mockResolvedValue(answersQuery),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getCustomActions: vi.fn().mockResolvedValue(actions),
    executeQuery: vi.fn().mockResolvedValue({ columns, items: rows, totalItems: rows.length, skip: 0, take: 50 }),
    executeCustomAction: vi.fn().mockResolvedValue(undefined),
    deleteMany: vi.fn().mockResolvedValue(undefined),
    getLookupReference: vi.fn().mockResolvedValue({ values: [] }),
    getDistinctValues: vi.fn().mockResolvedValue({ matching: [], remaining: [], hasMore: false }),
    ...overrides,
  } as any;
}

function configure(service: any) {
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: langStub },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });
}

async function grid(actions: CustomActionDefinition[], inputs: Record<string, unknown> = {}, overrides: Record<string, unknown> = {}) {
  const service = makeService(actions, overrides);
  configure(service);
  const fixture: ComponentFixture<SparkQueryGridComponent> = TestBed.createComponent(SparkQueryGridComponent);
  fixture.componentRef.setInput('queryId', 'question-answers');
  fixture.componentRef.setInput('data', rows);
  fixture.componentRef.setInput('columns', columns);
  for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
  fixture.detectChanges();
  await settle(fixture);
  return { fixture, c: fixture.componentInstance, service };
}

async function card(actions: CustomActionDefinition[], inputs: Record<string, unknown> = {}) {
  const service = makeService(actions);
  configure(service);
  const fixture = TestBed.createComponent(SparkQueryCardComponent);
  fixture.componentRef.setInput('queryId', 'question-answers');
  fixture.componentRef.setInput('data', rows);
  for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
  fixture.detectChanges();
  await settle(fixture);
  return { fixture, service, el: fixture.nativeElement as HTMLElement };
}

describe('query toolbar (#460 M15)', () => {
  beforeEach(() => TestBed.resetTestingModule());
  afterEach(() => vi.restoreAllMocks());

  /**
   * #467 R1: in `auto`, a list is selectable exactly when an action COUNTS — the caller holds it (it
   * was listed), it shows on queries, the result does not withhold it, the recycle bin does not hide
   * it, and its rule accepts at least one row. One case per condition, each failing only that one.
   */
  describe('selection modes (R1)', () => {
    it('the built-in Delete counts: a deleting user gets checkboxes', async () => {
      const { c } = await grid([newAction, deleteAction]);
      expect(c.selectionMode()).toBe('multiple');
    });

    it('the built-in Edit counts too', async () => {
      const { c } = await grid([newAction, editAction]);
      expect(c.selectionMode()).toBe('multiple');
    });

    it('an =1 custom action gives multiple: there is no single selection (D10)', async () => {
      const { c } = await grid([newAction, duplicateAction]);
      expect(c.selectionMode()).toBe('multiple');
    });

    it('(1) right: an action the server did not list does not count — a read-only user sees no checkboxes', async () => {
      const { c } = await grid([newAction]);
      expect(c.selectionMode()).toBe('none');
    });

    it('(2) placement: an action shown only on the detail page does not count', async () => {
      const detailOnly = { ...deleteAction, name: 'Archive', isDefault: undefined, showedOn: 'detail' } as CustomActionDefinition;
      const { c } = await grid([detailOnly]);
      expect(c.selectionMode()).toBe('none');
    });

    it('(3) withheld: an action the result disables does not count, and re-enabling it counts again', async () => {
      const { c } = await grid([deleteAction]);
      (c as any).disabledActions.set(['Delete']);
      expect(c.selectionMode()).toBe('none');
      (c as any).disabledActions.set([]);
      expect(c.selectionMode()).toBe('multiple');
    });

    it('(3) withheld: Edit does not count when the result withholds Save', async () => {
      const { c } = await grid([editAction]);
      (c as any).disabledActions.set(['save']);
      expect(c.selectionMode()).toBe('none');
    });

    it('(3) recycle bin: nothing counts while the list shows deleted rows', async () => {
      const { c } = await grid([deleteAction, duplicateAction], { deleted: 'only' });
      expect(c.selectionMode()).toBe('none');
    });

    it('(4) rule: an =0 action accepts no row and does not count; a rule-less one neither', async () => {
      const zero = { ...duplicateAction, name: 'Scatter', selectionRule: '=0' } as CustomActionDefinition;
      const { c } = await grid([zero, exportAction]);
      expect(c.selectionMode()).toBe('none');
    });

    it('drops the selection when the list stops being selectable', async () => {
      const { c, fixture } = await grid([deleteAction]);
      c.selection.set([rows[0]]);
      (c as any).disabledActions.set(['delete']);
      fixture.detectChanges();
      await settle(fixture);
      expect(c.selection()).toEqual([]);
    });

    it('an explicit multiple wins over auto', async () => {
      const { c } = await grid([newAction], {}, { getQuery: vi.fn().mockResolvedValue({ ...answersQuery, selectionMode: 'multiple' }) });
      expect(c.selectionMode()).toBe('multiple');
    });

    it('an explicit none wins over counting actions', async () => {
      const { c } = await grid([deleteAction, editAction], {}, { getQuery: vi.fn().mockResolvedValue({ ...answersQuery, selectionMode: 'none' }) });
      expect(c.selectionMode()).toBe('none');
    });

    it('the sub-query entry wins over the query definition', async () => {
      const { c } = await grid([duplicateAction], { selectionModeSetting: 'none' },
        { getQuery: vi.fn().mockResolvedValue({ ...answersQuery, selectionMode: 'multiple' }) });
      expect(c.selectionMode()).toBe('none');
    });
  });

  describe('toolbar actions', () => {
    it('lists New, Delete and the custom actions in priority order', async () => {
      const { c } = await grid([newAction, deleteAction, duplicateAction, exportAction], { selectionModeSetting: 'multiple' });

      expect(c.toolbarActions().map(a => `${a.kind}:${a.name}`))
        .toEqual(['new:New', 'delete:Delete', 'custom:DuplicateAnswer', 'custom:Export']);
      const priorities = c.toolbarActions().map(a => a.priority);
      expect([...priorities].sort((a, b) => a - b)).toEqual(priorities);
    });

    it('puts Edit between New and Delete, enabled for exactly one ticked row (R3)', async () => {
      const { c } = await grid([newAction, editAction, deleteAction]);
      expect(c.toolbarActions().map(a => `${a.kind}:${a.name}`)).toEqual(['new:New', 'edit:Edit', 'delete:Delete']);
      const edit = c.toolbarActions().find(a => a.kind === 'edit')!;

      expect(c.isToolbarActionEnabled(edit)).toBe(false);
      c.selection.set([rows[0]]);
      expect(c.isToolbarActionEnabled(edit)).toBe(true);
      c.selection.set([rows[0], rows[1]]);
      expect(c.isToolbarActionEnabled(edit)).toBe(false);
      expect(c.selection().length).toBe(2);
    });

    it('Edit opens the ticked row on its edit page, with this list as the return state', async () => {
      const { c } = await grid([editAction]);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      c.selection.set([rows[1]]);

      await c.runToolbarAction(c.toolbarActions().find(a => a.kind === 'edit')!);

      const [commands, extras] = navigate.mock.calls[0] as [unknown[], any];
      expect(commands).toEqual(['/po', 'answer', 'answers/2', 'edit']);
      expect(extras.state).toBeDefined();
    });

    it('withholds Edit when the result disables Edit or Save', async () => {
      for (const withheld of ['Edit', 'Save']) {
        TestBed.resetTestingModule();
        const { c } = await grid([editAction, deleteAction]);
        (c as any).disabledActions.set([withheld]);
        expect(c.toolbarActions().map(a => a.name)).toEqual(['Delete']);
      }
    });

    it('offers Delete whenever it counts, without an explicit selection mode', async () => {
      const { c } = await grid([deleteAction]);
      expect(c.toolbarActions().map(a => a.name)).toEqual(['Delete']);
    });

    it('offers a custom action whose showedOn differs only in case', async () => {
      const { c } = await grid([exportAction]);
      expect(c.toolbarActions().map(a => a.name)).toContain('Export');
    });

    it('leaves Delete out while rows cannot be selected', async () => {
      const { c } = await grid([newAction, deleteAction], { selectionModeSetting: 'none' });
      expect(c.toolbarActions().map(a => a.name)).toEqual(['New']);
    });

    it('leaves New out without the New right', async () => {
      const { c } = await grid([newAction], {}, {
        getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: false, canEdit: true, canDelete: true }),
      });
      expect(c.toolbarActions()).toEqual([]);
    });

    it('enables each action from the live selection count', async () => {
      const { c } = await grid([newAction, deleteAction, duplicateAction], { selectionModeSetting: 'multiple' });
      const byName = (name: string) => c.toolbarActions().find(a => a.name === name)!;

      expect(c.isToolbarActionEnabled(byName('New'))).toBe(true);
      expect(c.isToolbarActionEnabled(byName('Delete'))).toBe(false);
      expect(c.isToolbarActionEnabled(byName('DuplicateAnswer'))).toBe(false);

      c.selection.set([rows[0]]);
      expect(c.isToolbarActionEnabled(byName('Delete'))).toBe(true);
      expect(c.isToolbarActionEnabled(byName('DuplicateAnswer'))).toBe(true);

      c.selection.set([rows[0], rows[1]]);
      expect(c.isToolbarActionEnabled(byName('Delete'))).toBe(true);
      expect(c.isToolbarActionEnabled(byName('DuplicateAnswer'))).toBe(false);
      expect(c.isToolbarActionEnabled(byName('New'))).toBe(true);
    });

    it('bulk Delete posts the selected ids with the query and the parent, then drops them from the selection', async () => {
      vi.spyOn(window, 'confirm').mockReturnValue(true);
      const { c, service } = await grid([deleteAction],
        { selectionModeSetting: 'multiple', parentId: 'questions/1', parentType: 'Question' });
      c.selection.set([rows[0], rows[2]]);

      await c.runToolbarAction(c.toolbarActions().find(a => a.kind === 'delete')!);

      expect(service.deleteMany).toHaveBeenCalledWith('t-answer', [{ id: 'answers/1', etag: 'e1' }, { id: 'answers/3', etag: 'e3' }],
        { queryId: 'q-answers', parentId: 'questions/1', parentType: 'Question' });
      expect(c.selection()).toEqual([]);
    });

    it('asks first, and a refused confirmation deletes nothing', async () => {
      vi.spyOn(window, 'confirm').mockReturnValue(false);
      const { c, service } = await grid([deleteAction], { selectionModeSetting: 'multiple' });
      c.selection.set([rows[0]]);

      await c.runToolbarAction(c.toolbarActions().find(a => a.kind === 'delete')!);

      expect(service.deleteMany).not.toHaveBeenCalled();
    });

    it('New carries the sub-query parent to the create page', async () => {
      const { c } = await grid([newAction], { parentId: 'questions/1', parentType: 'Question' });
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

      await c.runToolbarAction(c.toolbarActions().find(a => a.kind === 'new')!);

      expect(navigate).toHaveBeenCalledTimes(1);
      const [commands, extras] = navigate.mock.calls[0] as [unknown[], any];
      expect(commands).toEqual(['/po', 'answer', 'new']);
      expect(extras.queryParams).toEqual({ parentId: 'questions/1', parentType: 'Question', queryId: 'question-answers' });
    });

    it('a top-level New carries no parent', async () => {
      const { c } = await grid([newAction]);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

      c.startNew();

      const [, extras] = navigate.mock.calls[0] as [unknown[], any];
      expect(extras.queryParams).toBeUndefined();
    });
  });

  describe('selection bar', () => {
    it('shows the chip with the count while rows are selected, and its ⊗ drops the whole selection', async () => {
      const { fixture, c } = await grid([deleteAction], { selectionModeSetting: 'multiple' });
      expect(fixture.nativeElement.querySelector('.spark-selection-chip')).toBeNull();

      c.selection.set([rows[0], rows[1]]);
      fixture.detectChanges();
      await settle(fixture);

      const chip = fixture.nativeElement.querySelector('.spark-selection-chip') as HTMLElement;
      expect(chip).not.toBeNull();
      expect(chip.textContent).toContain('2');
      expect(chip.textContent).toContain('common.selected');
      expect(chip.querySelector('.spark-selection-off-page')).toBeNull();

      (chip.querySelector('.spark-selection-clear') as HTMLButtonElement).click();
      expect(c.selection()).toEqual([]);
    });

    /**
     * #467 D19 / S1: the selection survives paging, so the chip counts ticked rows on other pages
     * too, says how many those are, and the count is exactly what an action receives.
     */
    it('counts ticked rows on other pages, and Delete receives exactly what the chip counts', async () => {
      vi.spyOn(window, 'confirm').mockReturnValue(true);
      const { fixture, c, service } = await grid([deleteAction]);
      const offPage = { id: 'answers/99', etag: 'e99', values: [{ key: 'Body', value: 'elsewhere' }] } as QueryResultItem;
      c.selection.set([rows[0], offPage]);
      fixture.detectChanges();
      await settle(fixture);

      const chip = fixture.nativeElement.querySelector('.spark-selection-chip') as HTMLElement;
      expect(chip.textContent).toContain('2');
      expect(chip.querySelector('.spark-selection-off-page')!.textContent).toContain('1');
      expect(c.offPageCount()).toBe(1);

      await c.runToolbarAction(c.toolbarActions().find(a => a.kind === 'delete')!);
      expect(service.deleteMany.mock.calls[0][1]).toEqual([{ id: 'answers/1', etag: 'e1' }, { id: 'answers/99', etag: 'e99' }]);
    });

    it('renders the checkbox-only datatable mode, so a row click never selects (D9)', async () => {
      const { fixture } = await grid([deleteAction]);
      const table = fixture.debugElement.query(By.directive(BsDatatableComponent)).componentInstance as BsDatatableComponent<QueryResultItem>;
      expect(table.selectionMode()).toBe('checkbox');
    });

    it('a row click opens the row on a selectable list, and a click in the checkbox cell does not', async () => {
      const { c } = await grid([deleteAction]);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      const cell = document.createElement('td');
      cell.className = 'checkbox-cell';
      const inCell = document.createElement('span');
      cell.appendChild(inCell);

      (c as any).onRowClick({ row: rows[0], originalEvent: new MouseEvent('click') });
      (c as any).onRowClick({ row: rows[1], originalEvent: { target: inCell } });

      expect(navigate).toHaveBeenCalledTimes(1);
      expect(navigate.mock.calls[0][0]).toEqual(['/po', 'answer', 'answers/1']);
      expect(c.selection()).toEqual([]);
    });

    it('offers no select-all: with lazy or virtual rows it could only tick the loaded ones', async () => {
      const { fixture } = await grid([deleteAction], { selectionModeSetting: 'multiple' });
      expect(fixture.nativeElement.querySelector('.spark-selection-bar input[type="checkbox"]')).toBeNull();
      expect(fixture.nativeElement.querySelector('.spark-select-all')).toBeNull();
    });

    it("the datatable's deselect-all clears the grid's selection", async () => {
      const { fixture, c } = await grid([deleteAction], { selectionModeSetting: 'multiple' });
      c.selection.set([rows[0], rows[1]]);
      fixture.detectChanges();
      await settle(fixture);

      const table = fixture.debugElement.query(By.directive(BsDatatableComponent)).componentInstance as BsDatatableComponent<QueryResultItem>;
      expect(table.selection().length).toBe(2);
      table.selection.set([]);

      expect(c.selection()).toEqual([]);
    });

    it('renders no selection bar when rows cannot be selected', async () => {
      const { fixture } = await grid([deleteAction], { selectionModeSetting: 'none' });
      expect(fixture.nativeElement.querySelector('.spark-selection-bar')).toBeNull();
    });
  });

  describe('row menu', () => {
    it('lists the actions whose rule accepts one row: Edit, Delete and =1, never New or a rule-less action', async () => {
      const { c } = await grid([newAction, editAction, deleteAction, duplicateAction, exportAction], { selectionModeSetting: 'none' });
      expect(c.rowActions().map(a => a.name)).toEqual(['Edit', 'Delete', 'DuplicateAnswer']);
    });

    it("Edit in a row's menu opens that row's edit page", async () => {
      const { c } = await grid([editAction]);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

      await c.runRowAction(c.rowActions()[0], rows[2]);

      expect(navigate.mock.calls[0][0]).toEqual(['/po', 'answer', 'answers/3', 'edit']);
    });

    it('leaves out an action the result withholds', async () => {
      const { c } = await grid([deleteAction, duplicateAction]);
      (c as any).disabledActions.set(['delete']);
      expect(c.rowActions().map(a => a.name)).toEqual(['DuplicateAnswer']);
    });

    it('runs a custom action on that row only, leaving the checkbox selection alone', async () => {
      const { c, service } = await grid([duplicateAction], { selectionModeSetting: 'multiple' });
      c.selection.set([rows[0], rows[1]]);

      await c.runRowAction(c.rowActions()[0], rows[2]);

      const [, name, , ids] = service.executeCustomAction.mock.calls[0];
      expect(name).toBe('DuplicateAnswer');
      expect(ids).toEqual(['answers/3']);
      expect(c.selection().map(r => r.id)).toEqual(['answers/1', 'answers/2']);
    });

    it('deletes that row only', async () => {
      vi.spyOn(window, 'confirm').mockReturnValue(true);
      const { c, service } = await grid([deleteAction], { selectionModeSetting: 'multiple' });
      c.selection.set([rows[0]]);

      await c.runRowAction(c.rowActions()[0], rows[2]);

      expect(service.deleteMany.mock.calls[0][1]).toEqual([{ id: 'answers/3', etag: 'e3' }]);
      expect(c.selection().map(r => r.id)).toEqual(['answers/1']);
    });

    it('opens from the row toggle, lists the actions and runs the chosen one on that row', async () => {
      const { c, fixture, service } = await grid([deleteAction, duplicateAction], { selectionModeSetting: 'multiple' });
      const toggles = fixture.nativeElement.querySelectorAll('.spark-row-menu-toggle') as NodeListOf<HTMLButtonElement>;
      expect(toggles.length).toBe(rows.length);

      toggles[1].click();
      await settle(fixture);

      // The CDK overlay renders outside the grid, in the overlay container.
      const items = Array.from(document.querySelectorAll('.spark-row-action')) as HTMLElement[];
      expect(items.map(i => i.getAttribute('data-action'))).toEqual(['Delete', 'DuplicateAnswer']);
      expect(toggles[1].getAttribute('aria-expanded')).toBe('true');

      items[1].click();
      await settle(fixture);

      expect(service.executeCustomAction.mock.calls[0][3]).toEqual(['answers/2']);
      expect(c.openRowMenu()).toBeNull();
      expect(document.querySelector('.spark-row-action')).toBeNull();
    });

    it('keeps at most one row menu open, and its own toggle closes it', async () => {
      const { c, fixture } = await grid([duplicateAction]);
      const toggles = fixture.nativeElement.querySelectorAll('.spark-row-menu-toggle') as NodeListOf<HTMLButtonElement>;

      toggles[0].click();
      toggles[2].click();
      await settle(fixture);
      expect(c.openRowMenu()).toBe('answers/3');
      expect(document.querySelectorAll('.spark-row-action').length).toBe(1);

      toggles[2].click();
      await settle(fixture);
      expect(c.openRowMenu()).toBeNull();
    });

    it("opening another row's menu while one is open leaves the new one open", async () => {
      const { c, fixture } = await grid([duplicateAction]);
      const toggles = fixture.nativeElement.querySelectorAll('.spark-row-menu-toggle') as NodeListOf<HTMLButtonElement>;

      toggles[0].click();
      await settle(fixture);
      expect(document.querySelectorAll('.spark-row-menu').length).toBe(1);

      // The first overlay reports this click as an outside click, and then detaches: neither may
      // close the menu the click has just opened.
      toggles[2].click();
      await settle(fixture);

      expect(c.openRowMenu()).toBe('answers/3');
      expect(document.querySelectorAll('.spark-row-menu').length).toBe(1);
      expect(toggles[2].getAttribute('aria-expanded')).toBe('true');
    });

    // The contrast rule itself lives in spark-query-grid.component.scss. The analog vitest plugin
    // compiles components without their stylesheets (the host gets no _nghost attribute), so a
    // computed-style assertion cannot see it here; the browser check covers the colour. What the
    // rule keys on is the datatable's marker, pinned here: if the datatable stopped writing
    // data-selected on the selected row, the rule would silently stop matching.
    it('marks the selected row with data-selected, which the contrast rule keys on', async () => {
      const { c, fixture } = await grid([duplicateAction], { selectionModeSetting: 'multiple' });
      c.selection.set([rows[1]]);
      fixture.detectChanges();
      await settle(fixture);

      const selected = Array.from(fixture.nativeElement.querySelectorAll('tbody tr[data-selected="true"]')) as HTMLElement[];
      expect(selected.length).toBe(1);
      expect(selected[0].querySelector('.spark-row-menu-toggle')).not.toBeNull();
    });

    it('renders a menu column only when some action takes one row', async () => {
      const { fixture } = await grid([newAction]);
      expect(fixture.componentInstance.rowActions()).toEqual([]);
      expect(fixture.nativeElement.querySelector('.spark-row-actions')).toBeNull();
    });
  });

  /**
   * #460: in the recycle bin nothing but the SoftDelete entry point's Restore/Purge applies, as on
   * the detail page of a row opened with `?deleted=only`. The rule lives in the grid once, so the
   * query-list page and the sub-query card cannot disagree.
   */
  describe('recycle bin', () => {
    const everything = [newAction, deleteAction, duplicateAction, exportAction];

    it("deleted: 'only' offers no New, no Delete and no custom action in the toolbar or the row menu", async () => {
      const { c, fixture } = await grid(everything, { selectionModeSetting: 'multiple', deleted: 'only' });

      expect(c.toolbarActions()).toEqual([]);
      expect(c.rowActions()).toEqual([]);
      expect(c.offersCreate()).toBe(false);
      expect(c.visibleCustomActions()).toEqual([]);
      expect(fixture.nativeElement.querySelector('.spark-row-menu-toggle')).toBeNull();
    });

    it("deleted: 'include' and 'exclude' keep the normal toolbar", async () => {
      for (const deleted of ['include', 'exclude'] as const) {
        TestBed.resetTestingModule();
        const { c } = await grid(everything, { selectionModeSetting: 'multiple', deleted });
        expect(c.toolbarActions().map(a => a.name)).toEqual(['New', 'Delete', 'DuplicateAnswer', 'Export']);
        expect(c.rowActions().map(a => a.name)).toEqual(['Delete', 'DuplicateAnswer']);
      }
    });

    it('a grid under a deleted parent offers nothing either, without changing what it fetches', async () => {
      const { c } = await grid(everything, { selectionModeSetting: 'multiple', parentDeleted: true });

      expect(c.toolbarActions()).toEqual([]);
      expect(c.rowActions()).toEqual([]);
    });

    it("a card in the recycle bin renders no action buttons and no row menu", async () => {
      for (const inputs of [{ deleted: 'only' }, { parentDeleted: true, parentId: 'questions/1', parentType: 'Question' }]) {
        TestBed.resetTestingModule();
        const { el } = await card(everything, { selectionMode: 'multiple', ...inputs });

        expect(el.querySelectorAll('bs-card-header [data-action]').length).toBe(0);
        expect(el.querySelector('.spark-row-menu-toggle')).toBeNull();
      }
    });
  });

  describe('card header', () => {
    it('puts the caption first and the actions after it', async () => {
      const { el } = await card([newAction, duplicateAction]);

      const header = el.querySelector('bs-card-header')!;
      const caption = header.querySelector('.spark-query-card-caption')!;
      const nav = header.querySelector('bs-priority-nav')!;
      expect(caption).not.toBeNull();
      expect(nav).not.toBeNull();
      expect(caption.compareDocumentPosition(nav) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    });

    it('renders New and, with selection, Delete as buttons', async () => {
      const { el } = await card([newAction, deleteAction], { selectionMode: 'multiple' });

      const names = [...el.querySelectorAll('bs-card-header [data-action]')].map(b => b.getAttribute('data-action'));
      expect(names).toContain('New');
      expect(names).toContain('Delete');
    });

    /**
     * The owner's end-to-end ask for a sub-query card, in one place: the default New and Delete, every
     * custom action shown on the query (or both), each enabled live from the selection, and the row
     * menu. A detail-only action stays off the card.
     */
    it('a sub-query card offers New, Delete and the query actions, enabled live, with the row menu', async () => {
      const detailOnly = { name: 'CloseQuestion', label: { en: 'Close' }, showedOn: 'detail', offset: 0, refreshOnCompleted: false } as CustomActionDefinition;
      const { fixture, el } = await card([newAction, deleteAction, duplicateAction, exportAction, detailOnly],
        { selectionMode: 'multiple', parentId: 'questions/1', parentType: 'Question' });
      const inner = fixture.debugElement.query(d => d.componentInstance instanceof SparkQueryGridComponent)
        .componentInstance as SparkQueryGridComponent;

      // bs-priority-nav stamps each item more than once (a measuring copy, the strip, the overflow
      // list), so the names are compared as a set; which copy is visible is the nav's business.
      const buttons = (name: string) => [...el.querySelectorAll(`bs-card-header .priority-nav-strip [data-action='${name}']`)] as HTMLButtonElement[];
      const names = new Set([...el.querySelectorAll('bs-card-header [data-action]')].map(b => b.getAttribute('data-action')));
      expect([...names]).toEqual(['New', 'Delete', 'DuplicateAnswer', 'Export']);
      expect(buttons('New')).toHaveLength(1);

      const enabled = (name: string) => !buttons(name)[0].disabled;
      expect(enabled('New')).toBe(true);
      expect(enabled('Delete')).toBe(false);
      expect(enabled('DuplicateAnswer')).toBe(false);
      expect(enabled('Export')).toBe(true);

      inner.selection.set([rows[0]]);
      await settle(fixture);
      expect(enabled('Delete')).toBe(true);
      expect(enabled('DuplicateAnswer')).toBe(true);

      inner.selection.set([rows[0], rows[1]]);
      await settle(fixture);
      expect(enabled('Delete')).toBe(true);
      expect(enabled('DuplicateAnswer')).toBe(false);

      expect(el.querySelectorAll('.spark-row-menu-toggle').length).toBe(rows.length);
      expect(inner.rowActions().map(a => a.name)).toEqual(['Delete', 'DuplicateAnswer']);
    });

    it('passes the sub-query entry selection mode down to the grid', async () => {
      const { fixture } = await card([deleteAction], { selectionMode: 'multiple' });
      const inner = fixture.debugElement.query(d => d.componentInstance instanceof SparkQueryGridComponent)
        .componentInstance as SparkQueryGridComponent;
      expect(inner.selectionMode()).toBe('multiple');
    });
  });
});
