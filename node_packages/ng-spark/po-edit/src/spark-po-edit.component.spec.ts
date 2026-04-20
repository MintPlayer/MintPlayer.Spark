import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it, vi } from 'vitest';

import { SparkPoEditComponent } from './spark-po-edit.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { EntityType, PersistentObject, ShowedOn } from '@mintplayer/ng-spark/models';
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
      id: 'a-last', name: 'LastName', dataType: 'string',
      isRequired: false, isVisible: true, isReadOnly: false,
      order: 2, showedOn: ShowedOn.PersistentObject,
    } as any,
  ],
} as any;

const existingItem: PersistentObject = {
  id: 'people/1',
  name: 'Alice Smith',
  objectTypeId: 't-person',
  attributes: [
    { id: 'a-first', name: 'FirstName', value: 'Alice' } as any,
    { id: 'a-last', name: 'LastName', value: 'Smith' } as any,
  ],
} as any;

const routes: Routes = [
  { path: 'po/:type/:id/edit', component: SparkPoEditComponent },
  { path: 'po/:type/:id', component: StubComponent },
];

async function setup(serviceOverrides: Partial<SparkService> = {}) {
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    get: vi.fn().mockResolvedValue(existingItem),
    update: vi.fn().mockResolvedValue({ id: 'people/1', name: 'Updated' }),
    ...serviceOverrides,
  };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter(routes),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, service };
}

describe('SparkPoEditComponent', () => {
  it('fetches entity type + existing item and prefills form data', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();

    expect(service.getEntityTypes).toHaveBeenCalled();
    expect(service.get).toHaveBeenCalledWith('person', 'people/1');
    expect(c.entityType()?.name).toBe('Person');
    expect(c.formData()).toEqual({ FirstName: 'Alice', LastName: 'Smith' });
  });

  it('records load failure as a general validation error', async () => {
    const { harness } = await setup({
      get: vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 404, error: { error: 'Not found' } })),
    });
    const c = await harness.navigateByUrl('/po/person/people%2Fmissing/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();

    const errors = c.validationErrors();
    expect(errors).toHaveLength(1);
    expect(errors[0].attributeName).toBe('');
  });

  it('onSave is a no-op when no item loaded', async () => {
    const { harness, service } = await setup({
      get: vi.fn().mockRejectedValue(new Error('no item')),
    });
    const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();

    await c.onSave();

    expect(service.update).not.toHaveBeenCalled();
  });

  it('onSave updates with the new form values, emits saved, navigates to detail', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();
    c.formData.set({ ...c.formData(), FirstName: 'Alicia' });

    const saved = vi.fn();
    c.saved.subscribe(saved);

    const navigated = nextNavigationEnd();
    await c.onSave();
    await navigated;

    expect(service.update).toHaveBeenCalledOnce();
    const [, , payload] = (service.update as any).mock.calls[0];
    const firstAttr = payload.attributes.find((a: any) => a.name === 'FirstName');
    expect(firstAttr.value).toBe('Alicia');
    expect(firstAttr.isValueChanged).toBe(true);
    const lastAttr = payload.attributes.find((a: any) => a.name === 'LastName');
    expect(lastAttr.isValueChanged).toBe(false);

    expect(saved).toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/po/person/people%2F1');
    expect(c.isSaving()).toBe(false);
  });

  it('onSave 400 error populates validationErrors from the server payload', async () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: { errors: [{ attributeName: 'FirstName', errorMessage: { en: 'Required' }, ruleType: 'required' }] },
    });
    const { harness } = await setup({ update: vi.fn().mockRejectedValue(error) });
    const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();

    await c.onSave();

    expect(c.validationErrors()[0].attributeName).toBe('FirstName');
    expect(c.isSaving()).toBe(false);
  });

  it('onCancel navigates back to detail', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();

    const cancelled = vi.fn();
    c.cancelled.subscribe(cancelled);

    const navigated = nextNavigationEnd();
    c.onCancel();
    await navigated;

    expect(cancelled).toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/po/person/people%2F1');
  });
});
