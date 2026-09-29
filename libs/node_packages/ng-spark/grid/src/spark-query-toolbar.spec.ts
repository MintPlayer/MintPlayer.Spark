import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

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
    it('shows the chip with the count, and its button clears the selection', async () => {
      const { fixture, c } = await grid([deleteAction], { selectionModeSetting: 'multiple' });
      c.selection.set([rows[0], rows[1]]);
      fixture.detectChanges();
      await settle(fixture);

      const chip = fixture.nativeElement.querySelector('.spark-selection-chip') as HTMLElement;
      expect(chip).not.toBeNull();
      expect(chip.textContent).toContain('2');
      expect(chip.textContent).toContain('common.selected');

      (fixture.nativeElement.querySelector('.spark-selection-clear') as HTMLButtonElement).click();
      expect(c.selection()).toEqual([]);
    });

    it('select-all ticks the page, and a second press clears it', async () => {
      const { c } = await grid([deleteAction], { selectionModeSetting: 'multiple' });

      c.toggleSelectAll();
      expect(c.selection().map(r => r.id)).toEqual(['answers/1', 'answers/2', 'answers/3']);
      expect(c.allPageRowsSelected()).toBe(true);

      c.toggleSelectAll();
      expect(c.selection()).toEqual([]);
    });

    it('reports a partial page selection as indeterminate', async () => {
      const { c } = await grid([deleteAction], { selectionModeSetting: 'multiple' });
      c.selection.set([rows[1]]);
      expect(c.somePageRowsSelected()).toBe(true);
      expect(c.allPageRowsSelected()).toBe(false);
    });

    it('renders no selection bar when rows cannot be selected', async () => {
      const { fixture } = await grid([deleteAction], { selectionModeSetting: 'none' });
      expect(fixture.nativeElement.querySelector('.spark-selection-bar')).toBeNull();
    });

    it('renders the select-all box only for multiple selection', async () => {
      const { fixture } = await grid([duplicateAction], { selectionModeSetting: 'single' });
      expect(fixture.nativeElement.querySelector('.spark-select-all')).toBeNull();
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

    it('passes the sub-query entry selection mode down to the grid', async () => {
      const { fixture } = await card([deleteAction], { selectionMode: 'multiple' });
      const inner = fixture.debugElement.query(d => d.componentInstance instanceof SparkQueryGridComponent)
        .componentInstance as SparkQueryGridComponent;
      expect(inner.selectionMode()).toBe('multiple');
    });
  });
});
