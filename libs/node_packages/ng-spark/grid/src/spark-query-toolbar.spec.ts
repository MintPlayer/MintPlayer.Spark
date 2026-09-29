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
  entityType: 'Answer', sortColumns: [], isStreamingQuery: false, description: { en: 'Answers' },
} as any;

const rows: QueryResultItem[] = [
  { id: 'answers/1', values: [{ key: 'Body', value: 'one' }] },
  { id: 'answers/2', values: [{ key: 'Body', value: 'two' }] },
  { id: 'answers/3', values: [{ key: 'Body', value: 'three' }] },
];

const columns = [{ name: 'Body', dataType: 'string', order: 1 } as any];

const newAction = {
  name: 'New', displayName: { en: 'New' }, icon: 'plus-lg', showedOn: 'both', offset: 0,
  refreshOnCompleted: false, isDefault: true, variant: 'primary',
} as CustomActionDefinition;
const deleteAction = {
  name: 'Delete', displayName: { en: 'Delete' }, icon: 'trash', showedOn: 'both', selectionRule: '>0', offset: 0,
  refreshOnCompleted: false, isDefault: true, variant: 'danger', confirmationMessageKey: 'common.confirmDeleteSelected',
} as CustomActionDefinition;
const duplicateAction = {
  name: 'DuplicateAnswer', displayName: { en: 'Duplicate' }, showedOn: 'query', selectionRule: '=1', offset: 0,
  refreshOnCompleted: true,
} as CustomActionDefinition;
const exportAction = {
  name: 'Export', displayName: { en: 'Export' }, showedOn: 'Both', offset: 1, refreshOnCompleted: false,
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

  describe('selection modes', () => {
    it('auto derives from the custom actions only: the default Delete does not add checkboxes', async () => {
      const { c } = await grid([newAction, deleteAction]);
      expect(c.selectionMode()).toBe('none');
    });

    it('auto turns single for an =1 custom action', async () => {
      const { c } = await grid([newAction, deleteAction, duplicateAction]);
      expect(c.selectionMode()).toBe('single');
    });

    it('the query definition wins over auto', async () => {
      const { c } = await grid([deleteAction], {}, { getQuery: vi.fn().mockResolvedValue({ ...answersQuery, selectionMode: 'multiple' }) });
      expect(c.selectionMode()).toBe('multiple');
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

      expect(service.deleteMany).toHaveBeenCalledWith('t-answer', ['answers/1', 'answers/3'],
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
    it('shows the chip with the count while rows are selected, and no clear button of its own', async () => {
      const { fixture, c } = await grid([deleteAction], { selectionModeSetting: 'multiple' });
      expect(fixture.nativeElement.querySelector('.spark-selection-chip')).toBeNull();

      c.selection.set([rows[0], rows[1]]);
      fixture.detectChanges();
      await settle(fixture);

      const chip = fixture.nativeElement.querySelector('.spark-selection-chip') as HTMLElement;
      expect(chip).not.toBeNull();
      expect(chip.textContent).toContain('2');
      expect(chip.textContent).toContain('common.selected');
      // Deselect-all is the datatable's header checkbox; the chip does not repeat it.
      expect(chip.querySelector('button')).toBeNull();
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
    it('lists the actions whose rule accepts one row: Delete and =1, never New or a rule-less action', async () => {
      const { c } = await grid([newAction, deleteAction, duplicateAction, exportAction], { selectionModeSetting: 'none' });
      expect(c.rowActions().map(a => a.name)).toEqual(['Delete', 'DuplicateAnswer']);
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

      expect(service.deleteMany.mock.calls[0][1]).toEqual(['answers/3']);
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

    it('renders a menu column only when some action takes one row', async () => {
      const { fixture } = await grid([newAction]);
      expect(fixture.componentInstance.rowActions()).toEqual([]);
      expect(fixture.nativeElement.querySelector('.spark-row-actions')).toBeNull();
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
      const detailOnly = { name: 'CloseQuestion', displayName: { en: 'Close' }, showedOn: 'detail', offset: 0, refreshOnCompleted: false } as CustomActionDefinition;
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
