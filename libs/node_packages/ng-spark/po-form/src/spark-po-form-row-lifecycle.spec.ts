import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';

import { SparkPoFormComponent } from './spark-po-form.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import {
  AS_DETAIL_ROW_KEY,
  EntityAttributeDefinition,
  EntityType,
  ShowedOn,
} from '@mintplayer/ng-spark/models';

/**
 * The client half of the server-side row lifecycle (#386).
 *
 * The property under test is not "does it call the endpoint" but the pair of conditions around it:
 * a type that has NOT opted in must issue no request at all — otherwise the flag costs every
 * existing app a round trip per click — and a refusal must leave the collection untouched, which is
 * the entire difference between a veto and a message.
 */

function attr(partial: Partial<EntityAttributeDefinition>): EntityAttributeDefinition {
  return {
    id: partial.name || 'a',
    name: 'a',
    dataType: 'string',
    isRequired: false,
    isVisible: true,
    isReadOnly: false,
    order: 1,
    showedOn: ShowedOn.PersistentObject,
    rules: [],
    ...partial,
  } as EntityAttributeDefinition;
}

const entriesAttr = attr({
  id: 'a-entries',
  name: 'ServiceEntries',
  dataType: 'AsDetail',
  isArray: true,
  asDetailType: 'Test.ServiceEntry',
  order: 1,
});

function entryType(serverSideRowLifecycle: boolean): EntityType {
  return {
    id: 't-entry',
    name: 'ServiceEntry',
    clrType: 'Test.ServiceEntry',
    serverSideRowLifecycle,
    attributes: [attr({ id: 'e-desc', name: 'Description', order: 1 })],
  } as EntityType;
}

function carType(serverSideRowLifecycle: boolean): EntityType {
  return {
    id: 't-car',
    name: 'Car',
    clrType: 'Test.Car',
    attributes: [entriesAttr],
    detailTypes: [entryType(serverSideRowLifecycle)],
  } as EntityType;
}

function createComponent(serverSideRowLifecycle: boolean, serviceOverrides: any = {}) {
  const service: any = {
    executeQueryByName: vi.fn().mockResolvedValue({ columns: [], items: [], totalItems: 0 }),
    // Deliberately empty: the row type is resolved out of the parent's `detailTypes`, which is the
    // production case — a row type edited through its parent has no rights of its own and so is
    // absent from the Query-gated catalogue.
    getEntityTypes: vi.fn().mockResolvedValue([]),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getLookupReference: vi.fn().mockResolvedValue({ name: 'x', values: [] }),
    newObject: vi.fn().mockResolvedValue({
      id: 'server-minted-key',
      name: 'ServiceEntry',
      objectTypeId: 't-entry',
      attributes: [{ name: 'Description', value: 'Service — 1-ABC-123', dataType: 'string' }],
    }),
    deleteRow: vi.fn().mockResolvedValue(undefined),
    ...serviceOverrides,
  };

  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });

  const fixture = TestBed.createComponent(SparkPoFormComponent);
  const component = fixture.componentInstance;
  fixture.componentRef.setInput('entityType', carType(serverSideRowLifecycle));
  fixture.componentRef.setInput('objectId', 'Cars/1');
  fixture.componentRef.setInput('parentType', 'Test.Car');
  return { fixture, component, service };
}

async function flush(): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await new Promise<void>(r => setTimeout(r, 0));
  }
}

async function ready(fixture: any): Promise<void> {
  fixture.detectChanges();
  await flush();
}

function refusal() {
  return {
    status: 400,
    error: { result: { errors: [{ attributeName: 'ServiceEntries', errorMessage: { en: 'Already invoiced.' }, ruleType: 'custom' }] } },
  };
}

describe('SparkPoFormComponent — server-side row lifecycle', () => {
  describe('when the row type has not opted in', () => {
    it('addInlineRow pushes a blank row without asking the server', async () => {
      const { fixture, component, service } = createComponent(false);
      await ready(fixture);

      await component.addInlineRow(entriesAttr);

      expect(service.newObject).not.toHaveBeenCalled();
      expect(component.formData()['ServiceEntries']).toEqual([{}]);
    });

    it('removeArrayItem splices without asking the server', async () => {
      const { fixture, component, service } = createComponent(false);
      await ready(fixture);
      component.formData.set({ ServiceEntries: [{ Description: 'a' }, { Description: 'b' }] });

      await component.removeArrayItem(entriesAttr, 0);

      expect(service.deleteRow).not.toHaveBeenCalled();
      expect(component.formData()['ServiceEntries']).toEqual([{ Description: 'b' }]);
    });
  });

  describe('when the row type has opted in', () => {
    it('addInlineRow uses the object the server constructed, keyed', async () => {
      const { fixture, component, service } = createComponent(true);
      await ready(fixture);

      await component.addInlineRow(entriesAttr);

      expect(service.newObject).toHaveBeenCalledWith('t-entry', {
        asDetailAttribute: 'ServiceEntries',
        parentType: 'Test.Car',
        parentId: 'Cars/1',
      });

      const rows = component.formData()['ServiceEntries'];
      expect(rows).toHaveLength(1);
      expect(rows[0]['Description']).toBe('Service — 1-ABC-123');
      // The point of the round trip: the row is matchable on save from the moment it appears.
      expect(rows[0][AS_DETAIL_ROW_KEY]).toBe('server-minted-key');
    });

    it('addInlineRow adds nothing when the server refuses', async () => {
      const { fixture, component } = createComponent(true, {
        newObject: vi.fn().mockRejectedValue(refusal()),
      });
      await ready(fixture);

      await component.addInlineRow(entriesAttr);

      expect(component.formData()['ServiceEntries']).toBeUndefined();
      expect(component.rowLifecycleErrorsFor('ServiceEntries')[0].errorMessage.en).toBe('Already invoiced.');
    });

    it('names the parent type by id when the host binds no parentType', async () => {
      // The create-page shape: spark-po-create has no parent route segment to bind, so the form
      // falls back. ⚠️ The fallback must be the type ID — the server resolves parentType through
      // ModelLoader.ResolveEntityType, which accepts a GUID or a declared alias and nothing else,
      // so a CLR name is refused exactly like an unknown type and the user sees a bare refusal on
      // an ordinary Add.
      const { fixture, component, service } = createComponent(true);
      fixture.componentRef.setInput('parentType', undefined);
      await ready(fixture);

      await component.addInlineRow(entriesAttr);

      expect(service.newObject).toHaveBeenCalledWith('t-entry', expect.objectContaining({
        parentType: 't-car',
      }));
      const sent = service.newObject.mock.calls[0][1].parentType;
      expect(sent).not.toBe('Test.Car');
    });

    it('addArrayItem opens the modal pre-filled with the constructed row', async () => {
      const { fixture, component } = createComponent(true);
      await ready(fixture);

      await component.addArrayItem(entriesAttr);

      expect(component.showAsDetailModal()).toBe(true);
      expect(component.asDetailFormData()['Description']).toBe('Service — 1-ABC-123');
    });

    it('addArrayItem does not open the modal when the server refuses', async () => {
      const { fixture, component } = createComponent(true, {
        newObject: vi.fn().mockRejectedValue(refusal()),
      });
      await ready(fixture);

      await component.addArrayItem(entriesAttr);

      expect(component.showAsDetailModal()).toBe(false);
    });

    it('removeArrayItem asks first, then splices', async () => {
      const { fixture, component, service } = createComponent(true);
      await ready(fixture);
      component.formData.set({
        ServiceEntries: [
          { Description: 'a', [AS_DETAIL_ROW_KEY]: 'key-a' },
          { Description: 'b', [AS_DETAIL_ROW_KEY]: 'key-b' },
        ],
      });

      await component.removeArrayItem(entriesAttr, 0);

      expect(service.deleteRow).toHaveBeenCalledWith('t-entry', {
        asDetailAttribute: 'ServiceEntries',
        parentType: 'Test.Car',
        parentId: 'Cars/1',
        rowKey: 'key-a',
      });
      expect(component.formData()['ServiceEntries']).toHaveLength(1);
    });

    it('removeArrayItem leaves the row in place when the hook refuses', async () => {
      const { fixture, component } = createComponent(true, {
        deleteRow: vi.fn().mockRejectedValue(refusal()),
      });
      await ready(fixture);
      component.formData.set({ ServiceEntries: [{ Description: 'a', [AS_DETAIL_ROW_KEY]: 'key-a' }] });

      await component.removeArrayItem(entriesAttr, 0);

      // The whole difference between a veto and a message.
      expect(component.formData()['ServiceEntries']).toHaveLength(1);
      expect(component.rowLifecycleErrorsFor('ServiceEntries')[0].errorMessage.en).toBe('Already invoiced.');
    });

    it('removeArrayItem does not ask about a row that was never stored', async () => {
      const { fixture, component, service } = createComponent(true);
      await ready(fixture);
      // Added in this same editing session and not yet saved: it has a key, but the server has
      // never seen it, so asking could only ever be answered "no such row".
      component.formData.set({ ServiceEntries: [{ Description: 'a' }] });

      await component.removeArrayItem(entriesAttr, 0);

      expect(service.deleteRow).not.toHaveBeenCalled();
      expect(component.formData()['ServiceEntries']).toHaveLength(0);
    });

    it('a refusal is cleared by the next attempt', async () => {
      const newObject = vi.fn()
        .mockRejectedValueOnce(refusal())
        .mockResolvedValueOnce({ id: 'k2', name: 'ServiceEntry', objectTypeId: 't-entry', attributes: [] });
      const { fixture, component } = createComponent(true, { newObject });
      await ready(fixture);

      await component.addInlineRow(entriesAttr);
      expect(component.rowLifecycleErrorsFor('ServiceEntries')).toHaveLength(1);

      await component.addInlineRow(entriesAttr);
      expect(component.rowLifecycleErrorsFor('ServiceEntries')).toHaveLength(0);
    });
  });
});
