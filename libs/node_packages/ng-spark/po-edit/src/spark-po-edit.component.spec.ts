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
    // Hidden as loaded. A refresh hook reveals it — see the overlay test below.
    {
      id: 'a-reason', name: 'Reason', dataType: 'string',
      isRequired: false, isVisible: false, isReadOnly: false,
      order: 3, showedOn: ShowedOn.PersistentObject,
    } as any,
  ],
} as any;

const existingItem: PersistentObject = {
  id: 'people/1',
  name: 'Alice Smith',
  objectTypeId: 't-person',
  // The server stamps a change vector on every load. The save has to send it back, or the
  // concurrency check -- which is opt-in by presence of the field -- never runs at all.
  etag: 'A:12-abc',
  attributes: [
    { id: 'a-first', name: 'FirstName', value: 'Alice' } as any,
    { id: 'a-last', name: 'LastName', value: 'Smith' } as any,
    { id: 'a-reason', name: 'Reason', value: null } as any,
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
      { provide: SparkLanguageService, useValue: { t: (k: string) => k, resolve: (ts: any) => ts?.en ?? '' } },
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

  // ⚠️ The regression this guards is not cosmetic: a refresh hook that reveals an attribute almost
  // always does so because the attribute has just become required. While this page filtered on the
  // attribute's state *as loaded*, such a field was rendered, filled in by the user, and then left
  // out of the save — so the server refused the save as missing the very value the user had just
  // typed, and the form had no way forward. Measured in Fleet: a car could not be marked stolen.
  it('sends the value of an attribute a refresh revealed', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();

    // Hidden as loaded, so it starts out with no slot at all.
    expect(Object.keys(c.formData())).not.toContain('Reason');

    // What the form does when a refresh response reveals it, and what its control does on first
    // keystroke: an in-place write into the shared formData object.
    c.refreshOverlay.set({ Reason: { isVisible: true, isRequired: true } });
    c.formData()['Reason'] = 'Moved abroad';

    await c.onSave();

    const [, , payload] = (service.update as any).mock.calls[0];
    const reason = payload.attributes.find((a: any) => a.name === 'Reason');
    expect(reason.value).toBe('Moved abroad');
    expect(reason.isValueChanged).toBe(true);
  });

  it('does not send a value for an attribute the refresh left hidden', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();

    await c.onSave();

    const [, , payload] = (service.update as any).mock.calls[0];
    const reason = payload.attributes.find((a: any) => a.name === 'Reason');
    expect(reason.value).toBeNull();
    expect(reason.isValueChanged).toBe(false);
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

  it('sends back the etag it was loaded with', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();

    await c.onSave();

    const [, , payload] = (service.update as any).mock.calls[0];
    expect(payload.etag).toBe('A:12-abc');
  });

  it('omits the etag when the server did not send one', async () => {
    // Guards the regression where somebody coalesces this to '' — an empty string IS present, so
    // the server would run the check against a token that can never match and every save would 409.
    const { etag, ...withoutEtag } = existingItem as any;
    const { harness, service } = await setup({ get: vi.fn().mockResolvedValue(withoutEtag) } as any);
    const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();

    await c.onSave();

    const [, , payload] = (service.update as any).mock.calls[0];
    expect(payload.etag).toBeUndefined();
  });

  it('onSave 409 renders the translated concurrency message, not the server string', async () => {
    const error = new HttpErrorResponse({
      status: 409,
      error: { result: { error: 'Concurrency conflict' }, operations: [] },
    });
    // The re-fetch the conflict merge needs fails, so there is nothing to merge against.
    const get = vi.fn().mockResolvedValueOnce(existingItem).mockRejectedValue(new Error('offline'));
    const { harness } = await setup({ update: vi.fn().mockRejectedValue(error), get } as any);
    const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
    await harness.fixture.whenStable();

    await c.onSave();

    // The stub language service echoes the key, so this asserts the key was looked up rather than
    // the server's own untranslated wording being shown.
    expect(c.validationErrors()[0].errorMessage.en).toBe('common.concurrencyConflict');
    expect(c.validationErrors()[0].attributeName).toBe('');
    // The typing is not thrown away — the user can retry or copy their values out.
    expect(c.formData()['FirstName']).toBe('Alice');
    expect(c.isSaving()).toBe(false);
  });

  describe('a 409 merged three-way against a re-fetch', () => {
    const conflict = new HttpErrorResponse({ status: 409, error: { result: { error: 'Concurrency conflict' }, operations: [] } });

    function theirVersion(values: Record<string, any>): PersistentObject {
      return {
        ...existingItem,
        etag: 'A:13-def',
        attributes: existingItem.attributes.map(a => (a.name in values ? { ...a, value: values[a.name] } : a)),
      };
    }

    async function editAndHitConflict(theirs: PersistentObject) {
      const get = vi.fn().mockResolvedValueOnce(existingItem).mockResolvedValue(theirs);
      const update = vi.fn().mockRejectedValueOnce(conflict).mockResolvedValue({ id: 'people/1', name: 'Updated' });
      const { harness, service } = await setup({ get, update } as any);
      const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
      await harness.fixture.whenStable();
      c.formData.set({ ...c.formData(), FirstName: 'Alicia' });
      await c.onSave();
      return { c, service };
    }

    it('merges disjoint edits, says what they changed, and saves nothing by itself', async () => {
      const { c, service } = await editAndHitConflict(theirVersion({ LastName: 'Jones' }));

      expect(service.get).toHaveBeenCalledTimes(2);
      expect(service.update).toHaveBeenCalledOnce();
      expect(c.pendingConflict()).toBeNull();
      expect(c.formData()).toMatchObject({ FirstName: 'Alicia', LastName: 'Jones' });
      expect(c.validationErrors()).toEqual([]);
      expect(c.conflictNotice()).toBe('common.conflictMerged');

      const navigated = nextNavigationEnd();
      await c.onSave();
      await navigated;

      const [, , payload] = service.update.mock.calls[1];
      // Rebased: the next save carries their token, so it does not 409 again.
      expect(payload.etag).toBe('A:13-def');
      const first = payload.attributes.find((a: any) => a.name === 'FirstName');
      const last = payload.attributes.find((a: any) => a.name === 'LastName');
      expect(first).toMatchObject({ value: 'Alicia', isValueChanged: true });
      // Their value, compared against their version: not sent as a change of mine.
      expect(last).toMatchObject({ value: 'Jones', isValueChanged: false });
    });

    it('opens the dialog on a true conflict, applies the choice, rebases, and the next save sends the fresh etag', async () => {
      const { c, service } = await editAndHitConflict(theirVersion({ FirstName: 'Alex', LastName: 'Jones' }));

      const pending = c.pendingConflict();
      expect(pending?.result.conflicts.map(x => x.path)).toEqual(['FirstName']);
      // Until the user decides, nothing moves: the form is as typed and the message stays.
      expect(c.formData()['FirstName']).toBe('Alicia');
      expect(c.validationErrors()[0].errorMessage.en).toBe('common.concurrencyConflict');

      c.onConflictResolved({ FirstName: 'mine' });

      expect(c.pendingConflict()).toBeNull();
      expect(c.formData()).toMatchObject({ FirstName: 'Alicia', LastName: 'Jones' });
      expect(c.item()?.etag).toBe('A:13-def');
      expect(c.validationErrors()).toEqual([]);
      expect(service.update).toHaveBeenCalledOnce();

      const navigated = nextNavigationEnd();
      await c.onSave();
      await navigated;

      const [, , payload] = service.update.mock.calls[1];
      expect(payload.etag).toBe('A:13-def');
      expect(payload.attributes.find((a: any) => a.name === 'FirstName')).toMatchObject({ value: 'Alicia', isValueChanged: true });
      expect(payload.attributes.find((a: any) => a.name === 'LastName')).toMatchObject({ value: 'Jones', isValueChanged: false });
    });

    it('takes their value when chosen, so nothing of mine is sent for it', async () => {
      const { c } = await editAndHitConflict(theirVersion({ FirstName: 'Alex' }));
      c.onConflictResolved({ FirstName: 'theirs' });
      expect(c.formData()['FirstName']).toBe('Alex');
    });

    it('cancelling the dialog keeps the form and the conflict message', async () => {
      const { c } = await editAndHitConflict(theirVersion({ FirstName: 'Alex' }));
      c.onConflictCancelled();

      expect(c.pendingConflict()).toBeNull();
      expect(c.formData()['FirstName']).toBe('Alicia');
      expect(c.item()?.etag).toBe('A:12-abc');
      expect(c.validationErrors()[0].errorMessage.en).toBe('common.concurrencyConflict');
    });

    it('shows who changed it and when, for an audited object', async () => {
      const theirs = theirVersion({ LastName: 'Jones' });
      theirs.attributes.push({ id: 'a-mb', name: 'ModifiedBy', value: 'users/42' } as any);
      const { c } = await editAndHitConflict(theirs);
      expect(c.conflictNotice()).toBe('common.conflictMerged common.conflictChangedBy');
    });
  });

  describe('a nested AsDetail type absent from the catalogue', () => {
    // ⚠️ The catalogue from /spark/types is Query-gated, and an AsDetail row type usually has no
    // rights of its own — nobody grants Query/GateSettings. So the type the save needs to rebuild
    // the nested wire shape is NOT in it, and the only place it exists is `detailTypes` on the
    // parent, which the server carries for exactly this reason.
    //
    // Before the fix this saved the attribute as a raw dict under `value`, the server could not map
    // an AsDetail from that, and EntityMapper's conversion `catch` swallowed it — the save returned
    // 200 and wrote nothing. Found by driving the real app and then reading the document.
    const gateType: any = {
      id: 't-gate', name: 'Gate', clrType: 'Test.Gate',
      attributes: [
        { id: 'g-mode', name: 'Mode', dataType: 'string', isVisible: true, isReadOnly: false, order: 1, showedOn: ShowedOn.PersistentObject },
      ],
    };

    const withGate: any = {
      ...personType,
      detailTypes: [gateType],
      attributes: [
        ...personType.attributes,
        {
          id: 'a-gate', name: 'Gate', dataType: 'AsDetail', asDetailType: 'Test.Gate',
          isArray: false, isVisible: true, isReadOnly: false, order: 4,
          showedOn: ShowedOn.PersistentObject,
        },
      ],
    };

    const itemWithGate: any = {
      ...existingItem,
      attributes: [...existingItem.attributes, { id: 'a-gate', name: 'Gate', dataType: 'AsDetail', asDetailType: 'Test.Gate', object: null }],
    };

    it('is resolved from detailTypes and saved as a nested object', async () => {
      const { harness, service } = await setup({
        // Deliberately does NOT include the gate type — that is the whole point.
        getEntityTypes: vi.fn().mockResolvedValue([withGate]),
        get: vi.fn().mockResolvedValue(itemWithGate),
      });
      const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
      await harness.fixture.whenStable();

      c.formData()['Gate'] = { Mode: 'fixed' };
      await c.onSave();

      const posted = service.update.mock.calls[0][2].attributes.find((a: any) => a.name === 'Gate');
      expect(posted.value).toBeNull();
      expect(posted.object).not.toBeNull();
      expect(posted.object.attributes.find((a: any) => a.name === 'Mode').value).toBe('fixed');
    });
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
