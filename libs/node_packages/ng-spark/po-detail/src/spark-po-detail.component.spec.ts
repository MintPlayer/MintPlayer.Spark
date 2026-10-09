import { Component, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';

import { SparkPoDetailComponent } from './spark-po-detail.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import {
  CustomActionDefinition,
  EntityType,
  PersistentObject,
  ShowedOn,
} from '@mintplayer/ng-spark/models';
import { SparkAttributeRefreshService, SparkQueryRefreshService } from '@mintplayer/ng-spark/client-operations';
import { nextNavigationEnd, settle, StubComponent } from '../../src/test-utils';

const personType: EntityType = {
  id: 't-person',
  name: 'Person',
  alias: 'person',
  clrType: 'Test.Person',
  attributes: [
    {
      id: 'a-first', name: 'FirstName', dataType: 'string',
      isRequired: true, isReadOnly: false,
      order: 1, showedOn: ShowedOn.PersistentObject,
    } as any,
    {
      id: 'a-query-only', name: 'QueryOnly', dataType: 'string',
      isRequired: false, isReadOnly: false,
      order: 2, showedOn: ShowedOn.Query,
    } as any,
  ],
  groups: [{ id: 'g-main', name: 'Main', order: 1 }],
  tabs: [{ id: 'tab-main', name: 'Main', order: 1 }],
} as any;

const existingItem: PersistentObject = {
  id: 'people/1',
  name: 'Alice',
  etag: 'A:1',
  objectTypeId: 't-person',
  attributes: [
    { id: 'a-first', name: 'FirstName', value: 'Alice' } as any,
  ],
} as any;

const customAction: CustomActionDefinition = {
  name: 'Archive',
  label: { en: 'Archive' } as any,
  showedOn: 'detail',
  refreshOnCompleted: false,
  offset: 0,
} as any;

const customActionWithConfirm: CustomActionDefinition = {
  ...customAction,
  name: 'Delete',
  confirmation: { en: 'confirmDelete' },
};

const customActionRefresh: CustomActionDefinition = {
  ...customAction,
  name: 'Refresh',
  refreshOnCompleted: true,
};

/** The core catalogue's Edit and Delete (#467 D7/D8), as `/spark/actions/list` lists them for a caller with the rights. */
const builtInEdit: CustomActionDefinition = {
  name: 'Edit', label: { en: 'Edit' }, icon: 'pencil', showedOn: 'both', selectionRule: '=1',
  refreshOnCompleted: false, offset: 0, isDefault: true,
};
const builtInDelete: CustomActionDefinition = {
  name: 'Delete', label: { en: 'Delete' }, icon: 'trash', showedOn: 'both', selectionRule: '>0', variant: 'danger',
  confirmation: { en: 'Delete {count} item(s)?' }, refreshOnCompleted: false, offset: 0, isDefault: true,
};

const routes: Routes = [
  { path: 'po/:type/:id', component: SparkPoDetailComponent },
  { path: 'po/:type/:id/edit', component: StubComponent },
  { path: 'query/:alias', component: StubComponent },
  { path: '', component: StubComponent },
];

async function setup(serviceOverrides: Partial<SparkService> = {}, renderers: any[] = []) {
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    get: vi.fn().mockResolvedValue(existingItem),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getCustomActions: vi.fn().mockResolvedValue([customAction, builtInEdit, builtInDelete]),
    executeCustomAction: vi.fn().mockResolvedValue(undefined),
    delete: vi.fn().mockResolvedValue(undefined),
    getLookupReference: vi.fn().mockResolvedValue({ name: 'dummy', values: [] } as any),
    executeQueryByName: vi.fn().mockResolvedValue({ data: [], totalRecords: 0 }),
    ...serviceOverrides,
  };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter(routes),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: renderers },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, service };
}

describe('SparkPoDetailComponent', () => {
  const confirmSpy = vi.spyOn(globalThis, 'confirm');

  beforeEach(() => confirmSpy.mockReset().mockReturnValue(true));
  afterEach(() => confirmSpy.mockReset());

  it('loads entity type + item + permissions + custom actions for detail view', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    expect(service.get).toHaveBeenCalledWith('person', 'people/1');
    expect(c.entityType()?.name).toBe('Person');
    expect(c.item()?.id).toBe('people/1');
    expect(c.canEdit()).toBe(true);
    expect(c.canDelete()).toBe(true);
    expect(c.customActions()).toHaveLength(1);
  });

  it('never shows the next object under the previous object\'s Delete while its rights load', async () => {
    // The component is reused across /po/:type/:id. Object 1 may be deleted; object 2 may not,
    // and its rights arrive late. Before the fix the new object was published first, so object 2
    // briefly rendered with object 1's Delete button.
    let releaseSecond!: () => void;
    const secondRights = new Promise<void>((resolve) => (releaseSecond = resolve));
    const getPermissions = vi.fn()
      .mockResolvedValueOnce({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true })
      .mockImplementationOnce(async () => {
        await secondRights;
        return { canQuery: true, canRead: true, canCreate: false, canEdit: false, canDelete: false };
      });
    const { harness } = await setup({
      get: vi.fn().mockImplementation(async (_type: string, id: string) => ({ ...existingItem, id })),
      getPermissions,
    } as Partial<SparkService>);

    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();
    expect(c.canDelete()).toBe(true);

    void harness.navigateByUrl('/po/person/people%2F2');
    await vi.waitFor(() => expect(getPermissions).toHaveBeenCalledTimes(2));
    // Rights for object 2 are still pending: the page must not yet claim object 2 with object 1's Delete.
    expect(c.item()?.id === 'people/2' && c.canDelete()).toBe(false);

    releaseSecond();
    await vi.waitFor(() => expect(c.item()?.id).toBe('people/2'));
    expect(c.canDelete()).toBe(false);
  });

  it('prefers the per-row can block over type-level permissions (#236 G5)', async () => {
    // Type-level says edit+delete allowed, but this row's can block forbids both — a row the
    // caller may read but not mutate must hide its Edit/Delete buttons instead of 404ing.
    const { harness } = await setup({
      get: vi.fn().mockResolvedValue({ ...existingItem, can: { edit: false, delete: false } }),
    } as Partial<SparkService>);
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    expect(c.canEdit()).toBe(false);
    expect(c.canDelete()).toBe(false);
  });

  it('hides Edit and Delete when the object withholds them (#460, D13)', async () => {
    // The server's OnDisableActionsAsync covers the built-in actions too, and refuses a disabled
    // one with 403 at submit -- so the button must not be offered. Case-insensitive; Save withholds Edit.
    const { harness } = await setup({
      get: vi.fn().mockResolvedValue({ ...existingItem, disabledActions: ['save', 'DELETE'] }),
    } as Partial<SparkService>);
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    expect(c.canEdit()).toBe(false);
    expect(c.canDelete()).toBe(false);
  });

  it('falls back to type-level permissions when no can block is present', async () => {
    // Unruled types omit the can block entirely; behaviour is exactly as before, backward-compatible.
    const { harness } = await setup({
      get: vi.fn().mockResolvedValue(existingItem),
      getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: false }),
    } as Partial<SparkService>);
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    expect(c.canEdit()).toBe(true);
    expect(c.canDelete()).toBe(false);
  });

  /** #467 D8: the buttons are the catalogue's entries; an app that removes or moves one removes the button. */
  it('offers Edit and Delete only when the catalogue lists them for the detail side', async () => {
    const { harness } = await setup({
      getCustomActions: vi.fn().mockResolvedValue([{ ...builtInEdit, showedOn: 'query' }]),
    } as Partial<SparkService>);
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    expect(c.canEdit()).toBe(false); // Edit is query-only
    expect(c.canDelete()).toBe(false); // Delete was removed ("Delete": null), so it is not listed
  });

  it("renders Edit and Delete with the catalogue's label and icon", async () => {
    const { harness } = await setup({
      getCustomActions: vi.fn().mockResolvedValue([{ ...builtInEdit, label: { en: 'Modify' } }, builtInDelete]),
    } as Partial<SparkService>);
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();
    harness.fixture.detectChanges();

    expect(c.editAction()?.label).toEqual({ en: 'Modify' });
    expect(harness.fixture.nativeElement.textContent).toContain('Modify');
  });

  it("onDelete asks the catalogue's Delete confirmation with a count of one", async () => {
    confirmSpy.mockReturnValueOnce(false);
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    await c.onDelete();

    expect(confirmSpy).toHaveBeenCalledWith('Delete 1 item(s)?');
  });

  it('resolves entity type via id OR alias', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/po/t-person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    expect(c.entityType()?.id).toBe('t-person');
  });

  it('filters custom actions to those showedOn detail or both', async () => {
    const actions = [
      { ...customAction, name: 'A', showedOn: 'detail' },
      { ...customAction, name: 'B', showedOn: 'both' },
      { ...customAction, name: 'C', showedOn: 'list' },
    ];
    const { harness } = await setup({
      getCustomActions: vi.fn().mockResolvedValue(actions),
    });
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    expect(c.customActions().map(a => a.name)).toEqual(['A', 'B']);
  });

  it('sets errorMessage when the item fails to load', async () => {
    const { harness } = await setup({
      get: vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 404, error: { error: 'Not found' } })),
    });
    const c = await harness.navigateByUrl('/po/person/people%2Fmissing', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    expect(c.errorMessage()).toBe('Not found');
    expect(c.item()).toBeNull();
  });

  it('visibleAttributes filters out query-only attributes', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    const names = c.visibleAttributes().map(a => a.name);
    expect(names).toEqual(['FirstName']);
  });

  // #264 G-Q3: the model says None (drawn nowhere), and OnLoadAsync shows the field for this object.
  describe('the loaded object decides where an attribute is drawn (runtime showedOn)', () => {
    const withReport = {
      ...personType,
      attributes: [
        ...personType.attributes,
        { id: 'a-report', name: 'PoliceReport', dataType: 'string', isRequired: false, isReadOnly: false, order: 3, showedOn: 'None' } as any,
      ],
    } as EntityType;

    async function loadWith(report: Record<string, unknown>) {
      const { harness } = await setup({
        getEntityTypes: vi.fn().mockResolvedValue([withReport]),
        get: vi.fn().mockResolvedValue({ ...existingItem, attributes: [...existingItem.attributes, { id: 'a-report', name: 'PoliceReport', value: 'PV-1', ...report }] }),
      } as Partial<SparkService>);
      const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
      await harness.fixture.whenStable();
      return c;
    }

    it('draws an attribute the model shows nowhere when the loaded object shows it', async () => {
      const c = await loadWith({ showedOn: 'PersistentObject' });
      expect(c.visibleAttributes().map(a => a.name)).toEqual(['FirstName', 'PoliceReport']);
    });

    it('does not draw an attribute shown nowhere, although its value ships', async () => {
      const c = await loadWith({ showedOn: 'None' });
      expect(c.visibleAttributes().map(a => a.name)).toEqual(['FirstName']);
    });

    it('falls back to the model when the object says nothing', async () => {
      const c = await loadWith({});
      expect(c.visibleAttributes().map(a => a.name)).toEqual(['FirstName']);
    });
  });

  it('renders the [i] beside a described attribute label and not beside an undescribed one (#348)', async () => {
    const describedType: EntityType = {
      ...personType,
      attributes: [
        { ...personType.attributes[0], description: { en: 'Given name.' } },
        {
          id: 'a-last', name: 'LastName', dataType: 'string',
          isRequired: false, isReadOnly: false,
          order: 2, showedOn: ShowedOn.PersistentObject,
        } as any,
      ],
    };
    const { harness } = await setup({ getEntityTypes: vi.fn().mockResolvedValue([describedType]) });
    await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const terms: HTMLElement[] = Array.from(harness.routeNativeElement!.querySelectorAll('dt'));
    const first = terms.find(t => t.textContent?.includes('FirstName'))!;
    const last = terms.find(t => t.textContent?.includes('LastName'))!;

    expect(first.querySelector('spark-attribute-description button')?.getAttribute('aria-label')).toBe('Given name.');
    expect(last.querySelector('spark-attribute-description button')).toBeNull();
  });

  it('onEdit emits edited and navigates to the edit route', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    const edited = vi.fn();
    c.edited.subscribe(edited);

    const navigated = nextNavigationEnd();
    c.onEdit();
    await navigated;

    expect(edited).toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/po/person/people%2F1/edit');
  });

  it('onDelete calls SparkService.delete and navigates away when confirmed', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    const deleted = vi.fn();
    c.deleted.subscribe(deleted);

    const navigated = nextNavigationEnd();
    await c.onDelete();
    await navigated;

    expect(service.delete).toHaveBeenCalledWith('person', 'people/1', 'A:1');
    expect(deleted).toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('onDelete returns to the type\'s list, not the start page, when no list recorded where it came from', async () => {
    const { harness } = await setup({
      getQueries: vi.fn().mockResolvedValue([{ id: 'q-people', alias: 'people', entityType: 'Person' }]),
      getProgramUnits: vi.fn().mockResolvedValue({ programUnitGroups: [{ programUnits: [{ type: 'query', queryId: 'q-people', alias: 'people', order: 1 }] }] }),
    } as any);
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    const navigated = nextNavigationEnd();
    await c.onDelete();
    await navigated;

    expect(TestBed.inject(Router).url).toBe('/query/people');
  });

  it('onDelete is a no-op when confirm returns false', async () => {
    confirmSpy.mockReturnValueOnce(false);
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    await c.onDelete();

    expect(service.delete).not.toHaveBeenCalled();
  });

  it('onCustomAction executes without confirmation when none is configured', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    const executed = vi.fn();
    c.customActionExecuted.subscribe(executed);

    await c.onCustomAction(customAction);

    expect(service.executeCustomAction).toHaveBeenCalledWith('person', 'Archive', existingItem);
    expect(executed).toHaveBeenCalledWith(expect.objectContaining({ action: customAction }));
    expect(confirmSpy).not.toHaveBeenCalled();
  });

  it('onCustomAction with confirmationMessageKey prompts; no-op on cancel', async () => {
    confirmSpy.mockReturnValueOnce(false);
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    await c.onCustomAction(customActionWithConfirm);

    expect(confirmSpy).toHaveBeenCalled();
    expect(service.executeCustomAction).not.toHaveBeenCalled();
  });

  describe('visibleCustomActions', () => {
    // The action catalogue is fetched per TYPE, so an action that applies to only some rows can
    // only be withheld per row -- the entity's actions hook does it server-side and the object
    // arrives carrying the answer. Coverage shipped an irreversible red "Delete data" button on
    // every repository page, healthy ones included, that only admitted it would refuse AFTER the
    // confirmation prompt.
    async function withDisabled(disabledActions: string[] | undefined) {
      const { harness } = await setup();
      const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
      await harness.fixture.whenStable();

      c.item.set({ ...c.item()!, disabledActions } as any);
      c.customActions.set([customAction, { ...customAction, name: 'Other' }]);
      return c;
    }

    it('offers every action when the object withholds none', async () => {
      const c = await withDisabled(undefined);
      expect(c.visibleCustomActions().map(a => a.name)).toEqual(['Archive', 'Other']);
    });

    it('withholds the named action', async () => {
      const c = await withDisabled(['Archive']);
      expect(c.visibleCustomActions().map(a => a.name)).toEqual(['Other']);
    });

    it('matches case-insensitively, so a name spelled differently still withholds', async () => {
      const c = await withDisabled(['archive']);
      expect(c.visibleCustomActions().map(a => a.name)).toEqual(['Other']);
    });

    it('treats an empty list as withholding nothing', async () => {
      const c = await withDisabled([]);
      expect(c.visibleCustomActions()).toHaveLength(2);
    });
  });

  it('onCustomAction disables the buttons while it runs and re-enables them on failure', async () => {
    // A custom action is not necessarily quick -- Coverage's Resync makes paged GitHub calls
    // inside the request. Without a busy state the button stays live and looks inert, and a
    // second click queues a second full run. The reset-in-finally matters as much as the guard:
    // an action that throws must not leave every button on the page permanently dead.
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    expect(c.runningAction()).toBeNull();

    let release: (() => void) | undefined;
    (service.executeCustomAction as any).mockImplementationOnce(
      () => new Promise<void>((_, reject) => { release = () => reject(new Error('boom')); }));

    const running = c.onCustomAction(customAction);
    expect(c.runningAction()).toBe(customAction.name);

    // A second click while the first is in flight must not start another run.
    await c.onCustomAction(customAction);
    expect(service.executeCustomAction).toHaveBeenCalledTimes(1);

    release!();
    await running;

    expect(c.runningAction()).toBeNull();
  });

  it('onCustomAction with refreshOnCompleted re-fetches the item', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();
    (service.get as any).mockClear();

    await c.onCustomAction(customActionRefresh);

    expect(service.executeCustomAction).toHaveBeenCalled();
    expect(service.get).toHaveBeenCalledWith('person', 'people/1');
  });

  /**
   * #319. The server may already have bumped a sub-query's token inside `executeCustomAction` (a
   * `refreshQuery` operation). Bumping it again after the awaited re-fetch of the object lands in a
   * second change-detection flush, so the grid fetched twice for one click.
   */
  it('onCustomAction with refreshOnCompleted asks the sub-query grids to refresh before re-fetching the item (#319)', async () => {
    const withSubQuery = { ...personType, queries: ['person-friends', { query: 'person-pets' }] } as any;
    const { harness, service } = await setup({
      getEntityTypes: vi.fn().mockResolvedValue([withSubQuery]),
      // Keeps the sub-query grids parked: this test is about the page, not the grids.
      getQuery: vi.fn().mockReturnValue(new Promise(() => undefined)),
    } as any);
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();
    const request = vi.spyOn(TestBed.inject(SparkQueryRefreshService), 'request');
    (service.get as any).mockClear();

    await c.onCustomAction(customActionRefresh);

    expect(request.mock.calls.map(([key]) => key)).toEqual(['person-friends', 'person-pets']);
    const lastRequest = Math.max(...request.mock.invocationCallOrder);
    expect(lastRequest).toBeLessThan((service.get as any).mock.invocationCallOrder[0]);
  });

  it('onCustomAction failure sets errorMessage', async () => {
    const { harness } = await setup({
      executeCustomAction: vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 500, error: { error: 'Boom' } })),
    });
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();

    await c.onCustomAction(customAction);

    expect(c.errorMessage()).toBe('Boom');
  });

  describe('renderer inputs (#241/#245)', () => {
    @Component({ selector: 'spec-detail-full-renderer', standalone: true, template: '' })
    class FullDetailRenderer {
      value = input<any>();
      attribute = input<any>();
      options = input<Record<string, any>>();
      formData = input<Record<string, any>>({});
      item = input<any>();
    }
    @Component({ selector: 'spec-detail-value-only-renderer', standalone: true, template: '' })
    class ValueOnlyRenderer {
      value = input<any>();
    }

    const nested = { id: 'cov/1', objectTypeId: 't-cov', attributes: [] } as any;
    const asDetailAttr = { name: 'Coverage', dataType: 'AsDetail' } as any;

    it('AsDetail attribute: detail renderer receives the nested PO as value AND in formData', async () => {
      const { harness } = await setup();
      const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
      const item = {
        id: 'people/1',
        attributes: [
          { name: 'FirstName', value: 'Alice' },
          { name: 'Coverage', value: null, object: nested },
        ],
      } as PersistentObject;

      const inputs = c.getDetailRendererInputs(FullDetailRenderer, asDetailAttr, item);
      expect(inputs['value']).toBe(nested);
      expect(inputs['formData']).toEqual({ FirstName: 'Alice', Coverage: nested });
      expect(inputs['item']).toBe(item);
    });

    it('renderer declaring only value gets a filtered bag', async () => {
      const { harness } = await setup();
      const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
      const item = { id: 'people/1', attributes: [{ name: 'FirstName', value: 'Alice' }] } as PersistentObject;

      const inputs = c.getDetailRendererInputs(ValueOnlyRenderer, personType.attributes[0], item);
      expect(Object.keys(inputs)).toEqual(['value']);
    });

    it('AsDetail sub-table cell renderer receives the flat row as item', async () => {
      const { harness } = await setup();
      const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
      const row = { Label: 'a', Code: 'X1' };
      const col = { name: 'Code' } as any;

      const inputs = c.getAsDetailCellRendererInputs(FullDetailRenderer, row, col);
      expect(inputs['value']).toBe('X1');
      expect(inputs['item']).toBe(row);
    });
  });

  describe('renderer resolution', () => {
    @Component({ selector: 'spec-detail-stars', standalone: true, template: '' })
    class StarsDetail {}
    @Component({ selector: 'spec-column-stars', standalone: true, template: '' })
    class StarsColumn {}

    const registry = [
      { name: 'stars', detailComponent: StarsDetail, columnComponent: StarsColumn },
      // Registered for the form only: a detail page must fall back to its own display, not crash.
      { name: 'edit-only', editComponent: StarsDetail },
    ];

    async function mounted() {
      const { harness } = await setup({}, registry);
      return harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    }

    it('resolves the detail component of a registered renderer', async () => {
      const c = await mounted();
      expect(c.getDetailRendererComponent({ name: 'X', renderer: 'stars' } as any)).toBe(StarsDetail);
    });

    it('resolves the column component for an AsDetail sub-table cell', async () => {
      const c = await mounted();
      expect(c.getAsDetailCellRendererComponent({ name: 'X', renderer: 'stars' } as any)).toBe(StarsColumn);
    });

    it('is null without a renderer, for an unregistered name, and for a renderer lacking that slot', async () => {
      const c = await mounted();
      expect(c.getAsDetailCellRendererComponent({ name: 'X' } as any)).toBeNull();
      expect(c.getAsDetailCellRendererComponent({ name: 'X', renderer: 'nope' } as any)).toBeNull();
      expect(c.getAsDetailCellRendererComponent({ name: 'X', renderer: 'edit-only' } as any)).toBeNull();
      expect(c.getDetailRendererComponent({ name: 'X', renderer: 'nope' } as any)).toBeNull();
      expect(c.getDetailRendererComponent({ name: 'X', renderer: 'edit-only' } as any)).toBeNull();
    });
  });

  describe('server-issued attribute patches (refreshAttribute)', () => {
    const rows = [{ id: 'l/1', objectTypeId: 't-line', attributes: [] }] as any[];
    const patchedItem: PersistentObject = {
      id: 'people/1',
      name: 'Alice',
      objectTypeId: 't-person',
      attributes: [
        { id: 'a-first', name: 'FirstName', dataType: 'string', value: 'Alice', object: null, objects: null } as any,
        { id: 'a-lines', name: 'Lines', dataType: 'AsDetail', value: null, object: null, objects: rows } as any,
        { id: 'a-addr', name: 'Address', dataType: 'AsDetail', value: null, object: null, objects: null } as any,
      ],
    } as any;

    async function mounted() {
      const { harness } = await setup({ get: vi.fn().mockResolvedValue(patchedItem) } as Partial<SparkService>);
      const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
      await settle(harness.fixture);
      const refresh = TestBed.inject(SparkAttributeRefreshService);
      const patch = async (name: string, p: Record<string, unknown>) => {
        refresh.request('t-person', 'people/1', name, p);
        await settle(harness.fixture);
      };
      const attr = (name: string) => c.item()!.attributes.find(a => a.name === name) as any;
      return { c, harness, patch, attr };
    }

    it('applies a changed scalar in place, without a re-fetch', async () => {
      const { c, patch, attr } = await mounted();
      const service = TestBed.inject(SparkService) as any;
      service.get.mockClear();

      await patch('FirstName', { value: 'Bob', object: null, objects: null });

      expect(attr('FirstName').value).toBe('Bob');
      expect(service.get).not.toHaveBeenCalled();
      // Untouched attributes keep their identity: only the patched one was rebuilt.
      expect(attr('Lines')).toBe(patchedItem.attributes[1]);
      expect(c.item()).not.toBe(patchedItem);
    });

    it('leaves the object alone when the scalar is unchanged and object/objects are null against null', async () => {
      // The server writes object/objects as null on every scalar patch. Treating null-vs-null as a
      // change would repaint the page on each patch for nothing.
      const { c, patch } = await mounted();
      const before = c.item();

      await patch('FirstName', { value: 'Alice', object: null, objects: null });
      await patch('Address', { object: null, objects: null });

      expect(c.item()).toBe(before);
    });

    it('ignores object/objects on a non-AsDetail attribute', async () => {
      const { c, patch, attr } = await mounted();
      const before = c.item();

      await patch('FirstName', { object: { id: 'x' }, objects: [{ id: 'y' }] });

      expect(c.item()).toBe(before);
      expect(attr('FirstName').object).toBeNull();
      expect(attr('FirstName').objects).toBeNull();
    });

    it('replaces the rows of an AsDetail collection, including emptying it', async () => {
      // AsDetail carries its rows in objects and leaves value null — a value-only patch could never
      // refresh a detail grid.
      const { patch, attr } = await mounted();
      const next = [{ id: 'l/2', objectTypeId: 't-line', attributes: [] }];

      await patch('Lines', { value: null, objects: next });
      expect(attr('Lines').objects).toBe(next);

      await patch('Lines', { objects: [] });
      expect(attr('Lines').objects).toEqual([]);
    });

    it('replaces a single embedded AsDetail object', async () => {
      const { patch, attr } = await mounted();
      const address = { id: 'addr', objectTypeId: 't-addr', attributes: [] };

      await patch('Address', { object: address });

      expect(attr('Address').object).toBe(address);
    });

    it('ignores a patch for an attribute the object does not carry', async () => {
      const { c, patch } = await mounted();
      const before = c.item();

      await patch('Unknown', { value: 'x' });

      expect(c.item()).toBe(before);
    });

    it('ignores patches addressed to another object', async () => {
      const { c, harness, attr } = await mounted();
      const before = c.item();

      TestBed.inject(SparkAttributeRefreshService).request('t-person', 'people/2', 'FirstName', { value: 'Eve' });
      await settle(harness.fixture);

      expect(c.item()).toBe(before);
      expect(attr('FirstName').value).toBe('Alice');
    });
  });

  describe('lookup options (#453)', () => {
    const lookupType: EntityType = {
      ...personType,
      attributes: [
        { id: 'a-role', name: 'Role', dataType: 'string', lookupReferenceType: 'Roles', order: 1, showedOn: ShowedOn.PersistentObject } as any,
        { id: 'a-status', name: 'Status', dataType: 'string', lookupReferenceType: 'Statuses', order: 2, showedOn: ShowedOn.PersistentObject } as any,
        { id: 'a-alt', name: 'AltRole', dataType: 'string', lookupReferenceType: 'Roles', order: 3, showedOn: ShowedOn.PersistentObject } as any,
      ],
    } as any;
    const statuses = { name: 'Statuses', values: [{ key: 'on', values: { en: 'On' }, isActive: true }] } as any;

    it('a failed lookup costs only its own labels: the others still load and the page does not error', async () => {
      const getLookupReference = vi.fn((name: string) =>
        name === 'Roles' ? Promise.reject(new HttpErrorResponse({ status: 404 })) : Promise.resolve(statuses));
      const { harness } = await setup({
        getEntityTypes: vi.fn().mockResolvedValue([lookupType]),
        getLookupReference,
      } as any);
      const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
      await settle(harness.fixture);

      // Deduplicated: two attributes share Roles, so it is asked once.
      expect(getLookupReference).toHaveBeenCalledTimes(2);
      expect(Object.keys(c.lookupReferenceOptions())).toEqual(['Statuses']);
      expect(c.lookupReferenceOptions()['Statuses']).toBe(statuses);
      expect(c.errorMessage()).toBeNull();
      expect(c.item()?.id).toBe('people/1');
    });
  });

  describe('AsDetail row types', () => {
    const lineType: EntityType = {
      id: 't-line',
      name: 'Line',
      clrType: 'Test.Line',
      attributes: [
        { id: 'l-product', name: 'Product', dataType: 'Reference', query: 'GetProducts', referenceType: 'Test.Product', order: 1, showedOn: ShowedOn.PersistentObject } as any,
        { id: 'l-qty', name: 'Qty', dataType: 'string', order: 2, showedOn: ShowedOn.PersistentObject } as any,
      ],
    } as any;
    const orderType = (detailTypes?: EntityType[]): EntityType => ({
      ...personType,
      attributes: [
        { id: 'a-lines', name: 'Lines', dataType: 'AsDetail', isArray: true, asDetailType: 'Test.Line', order: 1, showedOn: ShowedOn.PersistentObject } as any,
        // No type anywhere: must be skipped rather than recorded as undefined.
        { id: 'a-ghost', name: 'Ghosts', dataType: 'AsDetail', isArray: true, asDetailType: 'Test.Ghost', order: 2, showedOn: ShowedOn.PersistentObject } as any,
      ],
      detailTypes,
    } as any);
    const orderItem = {
      id: 'people/1', objectTypeId: 't-person',
      attributes: [{ id: 'a-lines', name: 'Lines', dataType: 'AsDetail', isArray: true, value: [{ Product: 'products/1', Qty: '2' }] }],
    } as any;
    const products = [{ id: 'products/1', breadcrumb: 'Widget', values: [] }];

    it('takes the row type from the embedded detailTypes when the catalogue lacks it (#385), and loads its reference options', async () => {
      const executeQueryByName = vi.fn().mockResolvedValue({ items: products, totalItems: 1 });
      const { harness } = await setup({
        getEntityTypes: vi.fn().mockResolvedValue([orderType([lineType])]),
        get: vi.fn().mockResolvedValue(orderItem),
        executeQueryByName,
      } as any);
      const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
      await settle(harness.fixture);

      expect(c.asDetailTypes()['Lines']).toBe(lineType);
      expect(Object.keys(c.asDetailTypes())).toEqual(['Lines']);
      // The sub-column's query runs scoped to the object on screen.
      expect(executeQueryByName).toHaveBeenCalledTimes(1);
      expect(executeQueryByName).toHaveBeenCalledWith('GetProducts', { parentId: 'people/1', parentType: 'person' });
      expect(c.asDetailReferenceOptions()).toEqual({ Lines: { Product: products } });
      // And the table resolves the reference id to its label through those options.
      const cells = Array.from(harness.routeNativeElement!.querySelectorAll('tbody td')).map(td => td.textContent?.trim());
      expect(cells).toContain('Widget');
      expect(cells).not.toContain('products/1');
    });

    it('prefers the catalogue copy over the embedded one', async () => {
      const catalogueLine = { ...lineType, attributes: [lineType.attributes[1]] } as EntityType;
      const executeQueryByName = vi.fn();
      const { harness } = await setup({
        getEntityTypes: vi.fn().mockResolvedValue([orderType([lineType]), catalogueLine]),
        get: vi.fn().mockResolvedValue(orderItem),
        executeQueryByName,
      } as any);
      const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
      await settle(harness.fixture);

      expect(c.asDetailTypes()['Lines']).toBe(catalogueLine);
      // The catalogue copy has no Reference column, so there is nothing to query.
      expect(executeQueryByName).not.toHaveBeenCalled();
    });

    it('records nothing when the row type is in neither source', async () => {
      const { harness } = await setup({
        getEntityTypes: vi.fn().mockResolvedValue([orderType(undefined)]),
        get: vi.fn().mockResolvedValue(orderItem),
      } as any);
      const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
      await settle(harness.fixture);

      expect(c.asDetailTypes()).toEqual({});
      expect(c.asDetailReferenceOptions()).toEqual({});
    });
  });
});
