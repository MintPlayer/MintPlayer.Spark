import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';

import { SparkPoFormComponent } from './spark-po-form.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import {
  EntityAttributeDefinition,
  EntityType,
  LookupReference,
  PersistentObject,
  ShowedOn,
} from '@mintplayer/ng-spark/models';

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

const carType: EntityType = {
  id: 't-car',
  name: 'Car',
  clrType: 'Test.Car',
  attributes: [
    attr({ id: 'a-status', name: 'Status', order: 1, dataType: 'LookupReference', lookupReferenceType: 'CarStatus', triggersRefresh: true }),
    attr({ id: 'a-report', name: 'PoliceReport', order: 2, isVisible: false }),
    attr({ id: 'a-promo', name: 'PromoUrl', order: 3 }),
    attr({ id: 'a-plate', name: 'LicensePlate', order: 4, triggersRefresh: true }),
    attr({ id: 'a-notes', name: 'Notes', order: 5 }),
    attr({ id: 'a-jobs', name: 'Jobs', order: 6, dataType: 'AsDetail', isArray: true, asDetailType: 'Test.Job', editMode: 'inline' }),
  ],
  tabs: [],
  groups: [],
} as any;

const statusLookup: LookupReference = {
  name: 'CarStatus',
  isTransient: true,
  displayType: 0,
  values: [
    { key: 'InUse', values: { en: 'In use' } as any, isActive: true },
    { key: 'Stolen', values: { en: 'Stolen' } as any, isActive: true },
  ],
} as any;

/** A refresh response: every attribute, with only the named ones reshaped. */
function response(overrides: Record<string, Partial<any>> = {}): PersistentObject {
  return {
    id: undefined,
    name: 'Car',
    objectTypeId: 't-car',
    attributes: carType.attributes.map(a => ({
      id: a.id,
      name: a.name,
      dataType: a.dataType,
      isRequired: a.isRequired,
      isVisible: a.isVisible,
      isReadOnly: a.isReadOnly,
      order: a.order,
      rules: [],
      value: null,
      ...(overrides[a.name] ?? {}),
    })),
  } as any;
}

function createComponent(serviceOverrides: Partial<SparkService> = {}) {
  const service: any = {
    executeQueryByName: vi.fn().mockResolvedValue({ data: [], totalRecords: 0 }),
    getEntityTypes: vi.fn().mockResolvedValue([carType]),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getLookupReference: vi.fn().mockResolvedValue(statusLookup),
    refresh: vi.fn().mockResolvedValue(response()),
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
  return { fixture, component: fixture.componentInstance, service };
}

async function flush(): Promise<void> {
  for (let i = 0; i < 8; i++) await new Promise<void>(r => setTimeout(r, 0));
}

async function mount(fixture: any, formData: Record<string, any> = {}) {
  fixture.componentRef.setInput('entityType', carType);
  fixture.componentRef.setInput('objectTypeId', 't-car');
  fixture.componentRef.setInput('formData', formData);
  fixture.detectChanges();
  await flush();
}

function named(component: SparkPoFormComponent, name: string) {
  return component.editableAttributes().find(a => a.name === name);
}

describe('spark-po-form — TriggersRefresh', () => {
  describe('applying a response', () => {
    it('reshapes the rendered attributes', async () => {
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockResolvedValue(response({
          PoliceReport: { isVisible: true, isRequired: true },
          PromoUrl: { isVisible: false },
        })),
      } as any);
      await mount(fixture, { Status: 'Stolen' });

      component.onFieldChange(named(component, 'Status') ?? carType.attributes[0]);
      await flush();
      fixture.detectChanges();

      expect(named(component, 'PoliceReport')).toBeDefined();
      expect(named(component, 'PoliceReport')!.isRequired).toBe(true);
      expect(named(component, 'PromoUrl')).toBeUndefined();
    });

    it('issues no additional service requests', async () => {
      // ★ The discriminator for the overlay design. Applying a refresh by setting a new EntityType
      // would re-run the option-loading effect and re-issue every reference query, every lookup
      // fetch, a full getEntityTypes() and a getPermissions() per array-AsDetail attribute — on
      // every keystroke-triggered refresh, against a service that caches nothing.
      const { fixture, component, service } = createComponent({
        refresh: vi.fn().mockResolvedValue(response({ PoliceReport: { isVisible: true } })),
      } as any);
      await mount(fixture, { Status: 'Stolen' });

      const before = {
        entityTypes: service.getEntityTypes.mock.calls.length,
        query: service.executeQueryByName.mock.calls.length,
        lookup: service.getLookupReference.mock.calls.length,
        permissions: service.getPermissions.mock.calls.length,
      };

      component.onFieldChange(carType.attributes[0]);
      await flush();
      fixture.detectChanges();

      expect(service.getEntityTypes.mock.calls.length).toBe(before.entityTypes);
      expect(service.executeQueryByName.mock.calls.length).toBe(before.query);
      expect(service.getLookupReference.mock.calls.length).toBe(before.lookup);
      expect(service.getPermissions.mock.calls.length).toBe(before.permissions);
    });

    it('replaces a lookup attribute’s options from the response', async () => {
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockResolvedValue(response({
          Status: { options: [{ key: 'Scrapped', label: { en: 'Scrapped' } }] },
        })),
      } as any);
      await mount(fixture, { Status: 'Stolen' });

      component.onFieldChange(carType.attributes[0]);
      await flush();

      expect(component.lookupReferenceOptions()['CarStatus'].values.map(v => v.key)).toEqual(['Scrapped']);
    });

    it('leaves loaded options alone when the response does not mention them', async () => {
      // null means "unchanged", not "none". Collapsing the two blanks every dropdown the hook never
      // touched, on every refresh.
      const { fixture, component } = createComponent();
      await mount(fixture, { Status: 'Stolen' });

      component.onFieldChange(carType.attributes[0]);
      await flush();

      expect(component.lookupReferenceOptions()['CarStatus'].values.map(v => v.key)).toEqual(['InUse', 'Stolen']);
    });
  });

  describe('value merge', () => {
    it('keeps a value edited during the round trip when the server did not change it', async () => {
      let resolve!: (po: PersistentObject) => void;
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockReturnValue(new Promise<PersistentObject>(r => { resolve = r; })),
      } as any);
      await mount(fixture, { Status: 'Stolen', Notes: '' });

      component.onFieldChange(carType.attributes[0]);
      await flush();

      // The user keeps typing — the form is deliberately never frozen during a refresh.
      component.formData.set({ ...component.formData(), Notes: 'typed while in flight' });

      // The server echoes what it was given for anything the hook did not touch.
      resolve(response({ Notes: { value: '' } }));
      await flush();

      expect(component.formData()['Notes']).toBe('typed while in flight');
    });

    it('takes a value the server did change, even over a concurrent edit', async () => {
      // The other half. A design that simply never overwrites what the user touched passes the test
      // above and fails this one — and a dependent value the hook computed would never appear.
      let resolve!: (po: PersistentObject) => void;
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockReturnValue(new Promise<PersistentObject>(r => { resolve = r; })),
      } as any);
      await mount(fixture, { Status: 'Stolen', Notes: '' });

      component.onFieldChange(carType.attributes[0]);
      await flush();
      component.formData.set({ ...component.formData(), Notes: 'typed while in flight' });

      resolve(response({ Notes: { value: 'set by the hook' } }));
      await flush();

      expect(component.formData()['Notes']).toBe('set by the hook');
    });
  });

  describe('scheduling', () => {
    it('does not refresh on keystroke for a free-text trigger, and does on blur', async () => {
      const { fixture, component, service } = createComponent();
      await mount(fixture, { LicensePlate: 'ABC' });

      const plate = carType.attributes.find(a => a.name === 'LicensePlate')!;
      component.onFieldChange(plate);
      component.onFieldChange(plate);
      await flush();

      expect(service.refresh).not.toHaveBeenCalled();

      component.onFieldBlur(plate);
      await flush();

      expect(service.refresh).toHaveBeenCalledTimes(1);
    });

    it('refreshes immediately for a discrete trigger', async () => {
      const { fixture, component, service } = createComponent();
      await mount(fixture, { Status: 'Stolen' });

      component.onFieldChange(carType.attributes[0]);
      await flush();

      expect(service.refresh).toHaveBeenCalledTimes(1);
    });

    it('does not refresh for an attribute that does not declare a trigger', async () => {
      const { fixture, component, service } = createComponent();
      await mount(fixture, {});

      component.onFieldChange(carType.attributes.find(a => a.name === 'Notes')!);
      await flush();

      expect(service.refresh).not.toHaveBeenCalled();
    });

    it('discards a superseded response', async () => {
      const resolvers: ((po: PersistentObject) => void)[] = [];
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockImplementation(() => new Promise<PersistentObject>(r => resolvers.push(r))),
      } as any);
      await mount(fixture, { Status: 'Stolen' });

      component.onFieldChange(carType.attributes[0]);
      await flush();
      component.onFieldChange(carType.attributes[0]);
      await flush();

      // The stale response arrives — it cannot be cancelled, only ignored — and claims a shape the
      // newer one contradicts.
      resolvers[0]?.(response({ PoliceReport: { isVisible: true, isRequired: true } }));
      resolvers[1]?.(response({ PoliceReport: { isVisible: false } }));
      await flush();
      fixture.detectChanges();

      expect(named(component, 'PoliceReport')).toBeUndefined();
    });

    it('flushes a pending refresh before saving', async () => {
      const { fixture, component, service } = createComponent();
      await mount(fixture, { LicensePlate: 'ABC' });

      const plate = carType.attributes.find(a => a.name === 'LicensePlate')!;
      component.onFieldChange(plate);
      await component.onSave();
      await flush();

      expect(service.refresh).toHaveBeenCalledTimes(1);
    });
  });

  describe('client-side rules', () => {
    it('blocks save on a rule the refresh imposed', async () => {
      const saved = vi.fn();
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockResolvedValue(response({
          PoliceReport: { isVisible: true, isRequired: true },
        })),
      } as any);
      await mount(fixture, { Status: 'Stolen' });
      component.save.subscribe(saved);

      component.onFieldChange(carType.attributes[0]);
      await flush();

      await component.onSave();

      expect(saved).not.toHaveBeenCalled();
      expect(component.hasError('PoliceReport')).toBe(true);
    });

    it('allows save once the imposed rule is satisfied', async () => {
      const saved = vi.fn();
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockResolvedValue(response({
          PoliceReport: { isVisible: true, isRequired: true },
        })),
      } as any);
      await mount(fixture, { Status: 'Stolen' });
      component.save.subscribe(saved);

      component.onFieldChange(carType.attributes[0]);
      await flush();
      component.formData.set({ ...component.formData(), PoliceReport: 'PR-1' });

      await component.onSave();

      expect(saved).toHaveBeenCalledTimes(1);
    });
  });

  describe('AsDetail row triggers', () => {
    const col = attr({ id: 'c-kind', name: 'Kind', dataType: 'LookupReference', lookupReferenceType: 'CarStatus', triggersRefresh: true });

    it('addresses the trigger with the same path the inline validation errors use', async () => {
      // Reusing `{attr}[{index}].{col}` rather than inventing a second addressing scheme is the
      // whole reason R20 was cheap. If these diverge, a server-side handler cannot tell which row
      // asked without parsing two formats.
      const { fixture, component, service } = createComponent();
      await mount(fixture, { Jobs: [{ Kind: 'InUse' }, { Kind: 'Stolen' }] });

      component.onInlineCellChange(carType.attributes.find(a => a.name === 'Jobs')!, 1, col);
      await flush();

      expect(service.refresh).toHaveBeenCalledTimes(1);
      expect(service.refresh.mock.calls[0][2]).toBe('Jobs[1].Kind');
    });

    it('does not refresh for an inline column without the flag', async () => {
      const { fixture, component, service } = createComponent();
      await mount(fixture, { Jobs: [{ Kind: 'InUse' }] });

      component.onInlineCellChange(
        carType.attributes.find(a => a.name === 'Jobs')!,
        0,
        attr({ id: 'c-plain', name: 'Plain' }));
      await flush();

      expect(service.refresh).not.toHaveBeenCalled();
    });

    it('leaves the row array identity intact, so rows are not rebuilt', async () => {
      // Rows are tracked by index. Replacing the array would destroy and recreate every row's DOM
      // and take focus with it — the failure mode that would make an inline trigger worse than no
      // trigger at all.
      const rows = [{ Kind: 'InUse' }];

      // The server echoes the rows it was given, but as a NEW array — it went through JSON. Under
      // reference equality that reads as "the server changed this" and the array is replaced,
      // rebuilding every row. Structural comparison is what makes the identity survive.
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockResolvedValue(response({ Jobs: { value: [{ Kind: 'InUse' }] } })),
      } as any);
      await mount(fixture, { Jobs: rows });

      component.onInlineCellChange(carType.attributes.find(a => a.name === 'Jobs')!, 0, col);
      await flush();

      expect(component.formData()['Jobs']).toBe(rows);
    });
  });

  describe('re-entrancy', () => {
    it('gives each form instance its own coordinator', async () => {
      // The retry-action modal renders its own spark-po-form, and a refresh can carry a retry
      // operation — so a refresh can open a modal containing a form that refreshes. A shared
      // coordinator would let the nested form supersede this one's request.
      const a = createComponent();
      await mount(a.fixture, { Status: 'Stolen' });

      const second = TestBed.createComponent(SparkPoFormComponent);
      await mount(second, { Status: 'Stolen' });

      expect((a.component as any).refreshCoordinator)
        .not.toBe((second.componentInstance as any).refreshCoordinator);
    });
  });
});
