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
import { nextNavigationEnd, StubComponent } from '../../src/test-utils';

const personType: EntityType = {
  id: 't-person',
  name: 'Person',
  alias: 'person',
  clrType: 'Test.Person',
  attributes: [
    {
      id: 'a-first', name: 'FirstName', dataType: 'string',
      isRequired: true, isVisible: true, isReadOnly: false,
      order: 1, showedOn: ShowedOn.PersistentObject,
    } as any,
    {
      id: 'a-query-only', name: 'QueryOnly', dataType: 'string',
      isRequired: false, isVisible: true, isReadOnly: false,
      order: 2, showedOn: ShowedOn.Query,
    } as any,
  ],
  groups: [{ id: 'g-main', name: 'Main', order: 1 }],
  tabs: [{ id: 'tab-main', name: 'Main', order: 1 }],
} as any;

const existingItem: PersistentObject = {
  id: 'people/1',
  name: 'Alice',
  objectTypeId: 't-person',
  attributes: [
    { id: 'a-first', name: 'FirstName', value: 'Alice' } as any,
  ],
} as any;

const customAction: CustomActionDefinition = {
  name: 'Archive',
  displayName: { en: 'Archive' } as any,
  showedOn: 'detail',
  refreshOnCompleted: false,
  offset: 0,
} as any;

const customActionWithConfirm: CustomActionDefinition = {
  ...customAction,
  name: 'Delete',
  confirmationMessageKey: 'confirmDelete',
};

const customActionRefresh: CustomActionDefinition = {
  ...customAction,
  name: 'Refresh',
  refreshOnCompleted: true,
};

const routes: Routes = [
  { path: 'po/:type/:id', component: SparkPoDetailComponent },
  { path: 'po/:type/:id/edit', component: StubComponent },
  { path: '', component: StubComponent },
];

async function setup(serviceOverrides: Partial<SparkService> = {}) {
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    get: vi.fn().mockResolvedValue(existingItem),
    getPermissions: vi.fn().mockResolvedValue({ canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getCustomActions: vi.fn().mockResolvedValue([customAction]),
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
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
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

    expect(service.delete).toHaveBeenCalledWith('person', 'people/1');
    expect(deleted).toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/');
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

  it('onCustomAction with refreshOnCompleted re-fetches the item', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await harness.fixture.whenStable();
    (service.get as any).mockClear();

    await c.onCustomAction(customActionRefresh);

    expect(service.executeCustomAction).toHaveBeenCalled();
    expect(service.get).toHaveBeenCalledWith('person', 'people/1');
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
});
