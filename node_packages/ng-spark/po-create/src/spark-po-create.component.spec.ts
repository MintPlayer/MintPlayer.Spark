import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it, vi } from 'vitest';

import { SparkPoCreateComponent } from './spark-po-create.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { EntityType, ShowedOn } from '@mintplayer/ng-spark/models';
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
      id: 'a-active', name: 'Active', dataType: 'boolean',
      isRequired: false, isVisible: true, isReadOnly: false,
      order: 2, showedOn: ShowedOn.PersistentObject,
    } as any,
    {
      id: 'a-jobs', name: 'Jobs', dataType: 'AsDetail', isArray: true,
      isRequired: false, isVisible: true, isReadOnly: false,
      order: 3, showedOn: ShowedOn.PersistentObject,
    } as any,
  ],
} as any;

const routes: Routes = [
  { path: 'po/:type/new', component: SparkPoCreateComponent },
  { path: 'po/:type/:id', component: StubComponent },
];

async function setup(serviceOverrides: Partial<SparkService> = {}) {
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    create: vi.fn().mockResolvedValue({ id: 'people/new-1', name: 'Created' }),
    ...serviceOverrides,
  };
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, service };
}

describe('SparkPoCreateComponent', () => {
  it('loads entity type from the route param and initializes form data per dataType', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/po/person/new', SparkPoCreateComponent);
    await harness.fixture.whenStable();

    expect(c.entityType()?.name).toBe('Person');
    const data = c.formData();
    expect(data['FirstName']).toBe('');
    expect(data['Active']).toBe(false);
    expect(data['Jobs']).toEqual([]);
  });

  it('resolves entity type by alias OR id', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/po/t-person/new', SparkPoCreateComponent);
    await harness.fixture.whenStable();

    expect(c.entityType()?.id).toBe('t-person');
  });

  it('onSave is a no-op when no entityType resolved', async () => {
    const { harness, service } = await setup({ getEntityTypes: vi.fn().mockResolvedValue([]) });
    const c = await harness.navigateByUrl('/po/unknown/new', SparkPoCreateComponent);
    await harness.fixture.whenStable();

    await c.onSave();

    expect(service.create).not.toHaveBeenCalled();
  });

  it('onSave creates the PO, emits saved, and navigates to the detail route', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/new', SparkPoCreateComponent);
    await harness.fixture.whenStable();
    c.formData.set({ ...c.formData(), FirstName: 'Alice', Active: true, Jobs: [] });

    const saved = vi.fn();
    c.saved.subscribe(saved);

    const navigated = nextNavigationEnd();
    await c.onSave();
    await navigated;

    expect(service.create).toHaveBeenCalledOnce();
    const [type, payload] = (service.create as any).mock.calls[0];
    expect(type).toBe('person');
    expect(payload.objectTypeId).toBe('t-person');
    expect(payload.attributes.find((a: any) => a.name === 'FirstName').value).toBe('Alice');

    expect(saved).toHaveBeenCalledWith({ id: 'people/new-1', name: 'Created' });
    expect(TestBed.inject(Router).url).toBe('/po/person/people%2Fnew-1');
    expect(c.isSaving()).toBe(false);
  });

  it('onSave 400 error populates validationErrors from the server payload', async () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: { errors: [{ attributeName: 'FirstName', errorMessage: { en: 'Required' }, ruleType: 'required' }] },
    });
    const { harness } = await setup({ create: vi.fn().mockRejectedValue(error) });
    const c = await harness.navigateByUrl('/po/person/new', SparkPoCreateComponent);
    await harness.fixture.whenStable();

    await c.onSave();

    expect(c.validationErrors()).toHaveLength(1);
    expect(c.validationErrors()[0].attributeName).toBe('FirstName');
    expect(c.isSaving()).toBe(false);
  });

  it('onSave non-400 error sets a single generic error', async () => {
    const { harness } = await setup({ create: vi.fn().mockRejectedValue(new Error('boom')) });
    const c = await harness.navigateByUrl('/po/person/new', SparkPoCreateComponent);
    await harness.fixture.whenStable();

    await c.onSave();

    const errors = c.validationErrors();
    expect(errors).toHaveLength(1);
    expect(errors[0].attributeName).toBe('');
    expect(c.generalErrors()).toEqual(errors);
  });
});
