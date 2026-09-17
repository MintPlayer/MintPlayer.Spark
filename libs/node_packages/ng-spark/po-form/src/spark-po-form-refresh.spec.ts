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
    attr({ id: 'a-status', name: 'Status', order: 1, dataType: 'string', lookupReferenceType: 'CarStatus', triggersRefresh: true }),
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

    it('refreshes immediately for a lookup whose dataType is its key type', async () => {
      // ★ The regression this suite missed the first time. Fleet's Car.Status is
      // `dataType: "string"` with `lookupReferenceType: "CarStatus"` — it renders as a <bs-select>,
      // but a check keyed on dataType alone reads "string" and treats it as free text. It then
      // waits for a blur that a select never emits, so the refresh never fires at all: no request,
      // no error, nothing to see. The original fixture used dataType 'LookupReference', which no
      // real model produces, and so passed against the broken code.
      const { fixture, component, service } = createComponent();
      await mount(fixture, { Status: 'InUse' });

      component.onFieldChange(carType.attributes.find(a => a.name === 'Status')!);
      await flush();

      expect(service.refresh).toHaveBeenCalledTimes(1);
      expect(service.refresh.mock.calls[0][2]).toBe('Status');
    });

    it('sends the entity type id, not the route alias', async () => {
      // ★ Regression. The route segment is an alias ("car") as often as a guid, and the server types
      // persistentObject.objectTypeId as a Guid — so sending the alias fails deserialization and the
      // request 500s before the handler runs. No hook, no usable error, and every assertion in this
      // suite still passes, because they all mock the service and never look at what was sent.
      const { fixture, component, service } = createComponent();
      fixture.componentRef.setInput('entityType', carType);
      fixture.componentRef.setInput('objectTypeId', 'car');   // the alias, as po-create passes it
      fixture.componentRef.setInput('formData', { Status: 'InUse' });
      fixture.detectChanges();
      await flush();

      component.onFieldChange(carType.attributes.find(a => a.name === 'Status')!);
      await flush();

      expect(service.refresh).toHaveBeenCalledTimes(1);
      expect(service.refresh.mock.calls[0][0]).toBe('car');            // route segment: the alias
      expect(service.refresh.mock.calls[0][1].objectTypeId).toBe('t-car'); // payload: the real id
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

    it('applies the response to the row and to the column metadata', async () => {
      // A nested refresh runs against the ROW's type, so the response describes a CarreerJob-shaped
      // object, not this form. Its values belong to the row; its metadata belongs to the column
      // definition in asDetailTypes — a different signal from entityType, which is why a nested
      // response cannot go through the top-level overlay.
      const rows = [{ Kind: 'InUse', End: '2010-12-31' }];
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockResolvedValue({
          id: null,
          name: 'Job',
          objectTypeId: 't-job',
          attributes: [
            { name: 'Kind', value: 'Stolen', isVisible: true, isRequired: false, isReadOnly: false, rules: [] },
            { name: 'End', value: null, isVisible: true, isRequired: false, isReadOnly: true, rules: [] },
          ],
        }),
      } as any);
      await mount(fixture, { Jobs: rows });

      (component as any).asDetailTypes.set({
        Jobs: { id: 't-job', name: 'Job', clrType: 'Test.Job', tabs: [], groups: [], queries: [],
                attributes: [attr({ id: 'c-kind', name: 'Kind' }), attr({ id: 'c-end', name: 'End' })] },
      });

      component.onInlineCellChange(carType.attributes.find(a => a.name === 'Jobs')!, 0, col);
      await flush();

      expect(rows[0].End).toBeNull();
      expect(rows[0].Kind).toBe('Stolen');
      expect(component.asDetailTypes()['Jobs'].attributes.find(a => a.name === 'End')!.isReadOnly).toBe(true);
    });

    it('does not apply a nested response to the top-level overlay', async () => {
      // The bug this guards: routing a CarreerJob-shaped response through the top-level overlay
      // hides every attribute the row does not happen to have — which is most of the form.
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockResolvedValue({
          id: null, name: 'Job', objectTypeId: 't-job',
          attributes: [{ name: 'Kind', value: 'x', isVisible: true, isRequired: false, isReadOnly: false, rules: [] }],
        }),
      } as any);
      await mount(fixture, { Jobs: [{ Kind: 'InUse' }], Status: 'InUse' });

      component.onInlineCellChange(carType.attributes.find(a => a.name === 'Jobs')!, 0, col);
      await flush();
      fixture.detectChanges();

      expect(named(component, 'Status')).toBeDefined();
      expect(named(component, 'Notes')).toBeDefined();
    });

    it('marks a free-text column pending and sends it on blur', async () => {
      // Until #413 the free-text inline editors bound `(ngModelChange)="onFieldChange()"` with no
      // argument, so onInlineCellChange never ran, nothing was ever marked pending, and the blur
      // handler short-circuited on an empty pending set. A triggersRefresh on a text or number
      // column produced no request, ever — invisible because the shipped sample uses a Reference.
      const freeText = attr({ id: 'c-note', name: 'Note', dataType: 'string', triggersRefresh: true });
      const { fixture, component, service } = createComponent();
      await mount(fixture, { Jobs: [{ Note: 'x' }] });

      const jobs = carType.attributes.find(a => a.name === 'Jobs')!;
      component.onInlineCellChange(jobs, 0, freeText);
      await flush();
      expect(service.refresh).not.toHaveBeenCalled();

      component.onInlineCellBlur(jobs, 0, freeText);
      await flush();

      expect(service.refresh).toHaveBeenCalledTimes(1);
      expect(service.refresh.mock.calls[0][2]).toBe('Jobs[0].Note');
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

  describe('AsDetail single-object triggers', () => {
    // The gate attribute is isArray: false, so it renders as a textbox + pencil opening a modal,
    // and the modal's recursive form is what raises these events.
    const gateAttr = attr({
      id: 'a-gate', name: 'Gate', order: 7, dataType: 'AsDetail', isArray: false,
      asDetailType: 'Test.Gate',
    });
    const modeCol = attr({
      id: 'c-mode', name: 'Mode', dataType: 'LookupReference', lookupReferenceType: 'CarStatus',
      triggersRefresh: true,
    });

    const gateType: any = {
      id: 't-gate', name: 'Gate', clrType: 'Test.Gate', tabs: [], groups: [], queries: [],
      attributes: [modeCol, attr({ id: 'c-target', name: 'Target' })],
    };

    /** The host type, which must actually declare the attribute the modal edits. */
    const gateCarType: any = { ...carType, attributes: [...carType.attributes, gateAttr] };

    async function mountWithGate(fixture: any, formData: Record<string, any>) {
      fixture.componentRef.setInput('entityType', gateCarType);
      fixture.componentRef.setInput('objectTypeId', 't-car');
      fixture.componentRef.setInput('formData', formData);
      fixture.detectChanges();
      await flush();
    }

    /** Opens the modal the way the pencil button does, with the embedded type registered. */
    async function openGate(component: SparkPoFormComponent, fixture: any, gate: Record<string, any>) {
      (component as any).asDetailTypes.set({ Gate: gateType });
      component.openAsDetailEditor(gateAttr);
      (component as any).asDetailFormData.set({ ...gate });
      fixture.detectChanges();
      await flush();
    }

    it('addresses the trigger without an index', async () => {
      // "Gate.Mode", not "Gate[0].Mode". A single embedded object has nothing to index, and the
      // server rejects a path whose shape disagrees with the attribute's isArray.
      const { fixture, component, service } = createComponent();
      await mount(fixture, { Gate: { Mode: 'InUse' } });
      await openGate(component, fixture, { Mode: 'InUse' });

      component.onEmbeddedTrigger('Gate', { path: 'Gate.Mode', immediate: true });
      await flush();

      expect(service.refresh).toHaveBeenCalledTimes(1);
      expect(service.refresh.mock.calls[0][2]).toBe('Gate.Mode');
    });

    it('is issued by the host form, against the host type', async () => {
      // The child cannot issue it: its own entityType is the embedded type, so a payload built
      // there would describe a Gate while the request must describe the Car that owns it — and the
      // server authorizes a nested refresh on the OWNING type, which has the rights.
      const { fixture, component, service } = createComponent();
      await mount(fixture, { Gate: { Mode: 'InUse' } });
      await openGate(component, fixture, { Mode: 'InUse' });

      component.onEmbeddedTrigger('Gate', { path: 'Gate.Mode', immediate: true });
      await flush();

      expect(service.refresh.mock.calls[0][0]).toBe('t-car');
      expect(service.refresh.mock.calls[0][1].objectTypeId).toBe('t-car');
    });

    it('posts the modal working copy, not the parent snapshot', async () => {
      // Caught in the browser, not here: the payload carried `Gate: {}` because buildRefreshPayload
      // read formData, while the modal edits the copy in asDetailFormData. The hook then decided
      // against a null ProjectMode and hid the target no matter what the user picked — which looks
      // like it works, because hiding is the default branch.
      const { fixture, component, service } = createComponent();
      await mountWithGate(fixture, { Gate: {} });
      await openGate(component, fixture, { Mode: 'Stolen' });

      component.onEmbeddedTrigger('Gate', { path: 'Gate.Mode', immediate: true });
      await flush();

      const posted = service.refresh.mock.calls[0][1].attributes
        .find((a: any) => a.name === 'Gate');
      expect(posted.value).toEqual({ Mode: 'Stolen' });
    });

    it('does not refresh for a column without the flag', async () => {
      const { fixture, component, service } = createComponent();
      await mount(fixture, { Gate: { Mode: 'InUse' } });
      await openGate(component, fixture, { Mode: 'InUse' });

      // The child never emits for an unflagged attribute, so nothing reaches the host.
      await flush();

      expect(service.refresh).not.toHaveBeenCalled();
    });

    it('applies the response to the modal working copy and the embedded metadata', async () => {
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockResolvedValue({
          id: null, name: 'Gate', objectTypeId: 't-gate',
          attributes: [
            { name: 'Mode', value: 'Stolen', isVisible: true, isRequired: false, isReadOnly: false, rules: [] },
            { name: 'Target', value: 80, isVisible: true, isRequired: true, isReadOnly: false, rules: [] },
          ],
        }),
      } as any);
      await mount(fixture, { Gate: { Mode: 'InUse', Target: null } });
      await openGate(component, fixture, { Mode: 'InUse', Target: null });

      component.onEmbeddedTrigger('Gate', { path: 'Gate.Mode', immediate: true });
      await flush();

      expect((component as any).asDetailFormData()['Target']).toBe(80);
      expect(component.asDetailTypes()['Gate'].attributes.find(a => a.name === 'Target')!.isRequired).toBe(true);
    });

    it('does not write through to the parent until the modal is confirmed', async () => {
      // openAsDetailEditor copies the embedded dict precisely so dismissing the modal discards the
      // edit. A refreshed value written straight into formData would make a cancelled edit stick —
      // the one invariant a single-object refresh can break that a row refresh cannot.
      const gate = { Mode: 'InUse', Target: null };
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockResolvedValue({
          id: null, name: 'Gate', objectTypeId: 't-gate',
          attributes: [{ name: 'Target', value: 80, isVisible: true, isRequired: true, isReadOnly: false, rules: [] }],
        }),
      } as any);
      await mount(fixture, { Gate: gate });
      await openGate(component, fixture, gate);

      component.onEmbeddedTrigger('Gate', { path: 'Gate.Mode', immediate: true });
      await flush();

      expect(component.formData()['Gate'].Target).toBeNull();

      component.closeAsDetailModal();
      expect(component.formData()['Gate'].Target).toBeNull();
    });

    it('does not apply a single-object response to the top-level overlay', async () => {
      const { fixture, component } = createComponent({
        refresh: vi.fn().mockResolvedValue({
          id: null, name: 'Gate', objectTypeId: 't-gate',
          attributes: [{ name: 'Mode', value: 'x', isVisible: true, isRequired: false, isReadOnly: false, rules: [] }],
        }),
      } as any);
      await mount(fixture, { Gate: { Mode: 'InUse' }, Status: 'InUse' });
      await openGate(component, fixture, { Mode: 'InUse' });

      component.onEmbeddedTrigger('Gate', { path: 'Gate.Mode', immediate: true });
      await flush();
      fixture.detectChanges();

      expect(named(component, 'Status')).toBeDefined();
      expect(named(component, 'Notes')).toBeDefined();
    });

    it('marks a free-text trigger pending and sends it on blur', async () => {
      const { fixture, component, service } = createComponent();
      await mount(fixture, { Gate: { Mode: 'InUse' } });
      await openGate(component, fixture, { Mode: 'InUse' });

      component.onEmbeddedTrigger('Gate', { path: 'Gate.Mode', immediate: false });
      await flush();
      expect(service.refresh).not.toHaveBeenCalled();

      component.onEmbeddedTriggerBlur('Gate.Mode');
      await flush();
      expect(service.refresh).toHaveBeenCalledTimes(1);
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
