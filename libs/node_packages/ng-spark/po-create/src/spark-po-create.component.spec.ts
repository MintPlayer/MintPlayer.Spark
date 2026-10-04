import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
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
    // Hidden as loaded; revealed by a refresh in the overlay test below.
    {
      id: 'a-reason', name: 'Reason', dataType: 'string',
      isRequired: false, isVisible: false, isReadOnly: false,
      order: 4, showedOn: ShowedOn.PersistentObject,
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
    newObject: vi.fn().mockResolvedValue({ name: 'Person', attributes: [] }),
    ...serviceOverrides,
  };
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      // The general-error <bs-alert> animates (@fadeInOut); a refused New renders it.
      provideNoopAnimations(),
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

  // Worse here than on the edit page: this list is the ONLY source of the create payload, so an
  // attribute a refresh revealed was not sent stale — it was absent from the new object entirely.
  it('includes an attribute a refresh revealed', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/new', SparkPoCreateComponent);
    await harness.fixture.whenStable();

    expect(c.getEditableAttributes().map(a => a.name)).not.toContain('Reason');

    c.refreshOverlay.set({ Reason: { isVisible: true, isRequired: true } });
    c.formData()['Reason'] = 'Relocation';

    await c.onSave();

    const [, payload] = (service.create as any).mock.calls[0];
    expect(payload.attributes.find((a: any) => a.name === 'Reason')?.value).toBe('Relocation');
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

  it('onSave 400 reads the errors inside the client-operation envelope', async () => {
    // The server wraps every body as { result, operations }: a create refused by a rule (a
    // suspended author, a closed question) carries its reason in result.errors.
    const error = new HttpErrorResponse({
      status: 400,
      error: { result: { errors: [{ attributeName: '', errorMessage: { en: 'Your account is suspended.' }, ruleType: 'custom' }] }, operations: [] },
    });
    const { harness } = await setup({ create: vi.fn().mockRejectedValue(error) });
    const c = await harness.navigateByUrl('/po/person/new', SparkPoCreateComponent);
    await harness.fixture.whenStable();

    await c.onSave();

    expect(c.validationErrors()).toHaveLength(1);
    expect(c.validationErrors()[0].errorMessage).toEqual({ en: 'Your account is suspended.' });
    expect(c.generalErrors()).toHaveLength(1);
  });

  // #264 G6: HR's create form did not draw the required LastName, and the server's "Last Name is required."
  // went nowhere: the form only shows an attribute's error next to that attribute. An error on an
  // attribute the form does not draw is promoted to a form-level error, as Vidyano does.
  it('shows a 400 error on an attribute the form does not draw as a form-level error', async () => {
    const withUndrawn = {
      ...personType,
      attributes: [
        ...personType.attributes,
        { id: 'a-code', name: 'Code', dataType: 'string', isRequired: true, isVisible: true, isReadOnly: false, order: 5, showedOn: ShowedOn.Query } as any,
      ],
    } as EntityType;
    const error = new HttpErrorResponse({
      status: 400,
      error: { result: { errors: [{ attributeName: 'Code', errorMessage: { en: 'Code is required.' }, ruleType: 'required' }] }, operations: [] },
    });
    const { harness } = await setup({ getEntityTypes: vi.fn().mockResolvedValue([withUndrawn]), create: vi.fn().mockRejectedValue(error) });
    const c = await harness.navigateByUrl('/po/person/new', SparkPoCreateComponent);
    await harness.fixture.whenStable();

    await c.onSave();
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    expect((harness.routeNativeElement as HTMLElement).textContent).toContain('Code is required.');
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

  describe('the blank object comes from the server (#460 M15, D19)', () => {
    it('asks /po/new with the sub-query parent from the query parameters and seeds the form', async () => {
      const newObject = vi.fn().mockResolvedValue({
        name: 'Person',
        attributes: [{ name: 'FirstName', value: 'from the hook' }, { name: 'Reason', value: 'hidden' }],
      });
      const { harness } = await setup({ newObject } as any);

      const c = await harness.navigateByUrl(
        '/po/person/new?parentId=companies%2F1&parentType=Company&queryId=company-people', SparkPoCreateComponent);
      await harness.fixture.whenStable();

      expect(newObject).toHaveBeenCalledWith('person', { parentId: 'companies/1', parentType: 'Company', queryId: 'company-people' });
      expect(c.formData()['FirstName']).toBe('from the hook');
      expect(c.formData()['Reason']).toBeUndefined();
    });

    // #264 G5/G7: what OnNewAsync sets on the blank object shapes the form from the first render.
    it('applies the runtime showedOn and isRequired the hook set on the blank object', async () => {
      const withReport = {
        ...personType,
        attributes: [
          ...personType.attributes,
          { id: 'a-report', name: 'PoliceReport', dataType: 'string', isRequired: false, isVisible: true, isReadOnly: false, order: 5, showedOn: ShowedOn.None } as any,
        ],
      } as EntityType;
      const newObject = vi.fn().mockResolvedValue({
        name: 'Person',
        attributes: [{ name: 'PoliceReport', value: null, showedOn: 'PersistentObject', isRequired: true }],
      });
      const { harness } = await setup({ getEntityTypes: vi.fn().mockResolvedValue([withReport]), newObject } as any);

      const c = await harness.navigateByUrl('/po/person/new', SparkPoCreateComponent);
      await harness.fixture.whenStable();

      const report = c.getEditableAttributes().find(a => a.name === 'PoliceReport');
      expect(report?.isRequired).toBe(true);
      expect('PoliceReport' in c.formData()).toBe(true);
    });

    it('leaves an attribute the model shows nowhere off the create form', async () => {
      const withReport = {
        ...personType,
        attributes: [
          ...personType.attributes,
          { id: 'a-report', name: 'PoliceReport', dataType: 'string', isRequired: false, isVisible: true, isReadOnly: false, order: 5, showedOn: ShowedOn.None } as any,
        ],
      } as EntityType;
      const { harness } = await setup({ getEntityTypes: vi.fn().mockResolvedValue([withReport]) });

      const c = await harness.navigateByUrl('/po/person/new', SparkPoCreateComponent);
      await harness.fixture.whenStable();

      expect(c.getEditableAttributes().map(a => a.name)).not.toContain('PoliceReport');
    });

    it('asks /po/new without a parent for a standalone New', async () => {
      const newObject = vi.fn().mockResolvedValue({ name: 'Person', attributes: [] });
      const { harness } = await setup({ newObject } as any);

      await harness.navigateByUrl('/po/person/new', SparkPoCreateComponent);
      await harness.fixture.whenStable();

      expect(newObject).toHaveBeenCalledWith('person', undefined);
    });

    it('shows a refused New and keeps the blank form usable', async () => {
      const newObject = vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 404, statusText: 'Not Found' }));
      const { harness } = await setup({ newObject } as any);

      const c = await harness.navigateByUrl(
        '/po/person/new?parentId=companies%2Fgone&parentType=Company&queryId=company-people', SparkPoCreateComponent);
      await harness.fixture.whenStable();

      expect(c.entityType()?.name).toBe('Person');
      expect(c.formData()['FirstName']).toBe('');
      expect(c.generalErrors()).toHaveLength(1);
    });
  });
});
