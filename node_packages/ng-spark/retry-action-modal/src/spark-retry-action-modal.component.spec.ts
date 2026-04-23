import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, beforeEach, vi } from 'vitest';

import { SparkRetryActionModalComponent } from './spark-retry-action-modal.component';
import { RetryActionService, SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { EntityType, PersistentObject, RetryActionPayload } from '@mintplayer/ng-spark/models';

const confirmDeleteCarType: EntityType = {
  id: 't/confirm-delete-car',
  name: 'ConfirmDeleteCar',
  clrType: 'Fleet.VirtualObjects.ConfirmDeleteCar',
  displayAttribute: 'Confirmation',
  attributes: [
    { id: 'a-plate', name: 'LicensePlate', dataType: 'string', order: 1, isRequired: false, isVisible: true, isReadOnly: true, rules: [] },
    { id: 'a-conf',  name: 'Confirmation', dataType: 'string', order: 2, isRequired: true,  isVisible: true, isReadOnly: false, rules: [] },
  ],
};

const scaffoldedPo: PersistentObject = {
  id: '',
  name: 'ConfirmDeleteCar',
  objectTypeId: 't/confirm-delete-car',
  attributes: [
    { id: 'a-plate', name: 'LicensePlate', dataType: 'string', value: 'ABC123', isRequired: false, isVisible: true, isReadOnly: true, order: 1, rules: [] },
    { id: 'a-conf',  name: 'Confirmation', dataType: 'string', value: null,     isRequired: true,  isVisible: true, isReadOnly: false, order: 2, rules: [] },
  ],
};

const samplePayload: RetryActionPayload = {
  step: 'confirm-overwrite',
  title: 'Overwrite the existing record?',
  message: 'This will replace the current values.',
  options: ['Overwrite', 'Cancel'],
  defaultOption: 'Cancel',
  persistentObject: { id: 'p/1', name: 'Person', objectTypeId: 't/1', attributes: [] } as any,
};

function setupWithTypes(types: EntityType[]) {
  const sparkService: any = {
    getEntityTypes: vi.fn().mockResolvedValue(types),
  };
  TestBed.configureTestingModule({
    imports: [SparkRetryActionModalComponent],
    providers: [
      provideNoopAnimations(),
      { provide: SparkService, useValue: sparkService },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
    ],
  });
  const service = TestBed.inject(RetryActionService);
  const fixture = TestBed.createComponent(SparkRetryActionModalComponent);
  const component = fixture.componentInstance;
  return { service, component, fixture, sparkService };
}

describe('SparkRetryActionModalComponent', () => {
  let service: RetryActionService;
  let component: SparkRetryActionModalComponent;

  beforeEach(() => {
    const s = setupWithTypes([]);
    service = s.service;
    component = s.component;
  });

  it('isOpen() is false when no payload is set', () => {
    expect(component.isOpen()).toBe(false);
  });

  it('isOpen() becomes true once the service publishes a payload', () => {
    service.show(samplePayload);

    expect(component.isOpen()).toBe(true);
  });

  it('onOption() forwards the choice to RetryActionService and clears the payload', async () => {
    const promise = service.show(samplePayload);

    component.onOption('Overwrite');

    const result = await promise;
    expect(result.option).toBe('Overwrite');
    expect(result.step).toBe('confirm-overwrite');
    expect(service.payload()).toBeNull();
  });

  it('onOption() does nothing when no payload is active (no throw, no resolution)', () => {
    expect(() => component.onOption('Cancel')).not.toThrow();
  });
});

describe('SparkRetryActionModalComponent with PersistentObject', () => {
  it('seeds formData from the scaffolded PO and resolves the matching EntityType', async () => {
    const { component, service, fixture } = setupWithTypes([confirmDeleteCarType]);
    const promise = service.show({
      step: 'confirm',
      title: 'Delete car',
      options: ['Delete', 'Cancel'],
      persistentObject: scaffoldedPo,
    } as any);
    fixture.detectChanges();
    // Allow the effect + getEntityTypes promise to resolve.
    await new Promise(resolve => setTimeout(resolve, 0));
    await new Promise(resolve => setTimeout(resolve, 0));

    expect(component.entityType()?.id).toBe('t/confirm-delete-car');
    expect(component.formData()).toEqual({ LicensePlate: 'ABC123', Confirmation: null });

    component.onOption('Cancel'); // drain the promise
    await promise;
  });

  it('populates the submitted PO with formData values on option click', async () => {
    const { component, service, fixture } = setupWithTypes([confirmDeleteCarType]);
    const promise = service.show({
      step: 'confirm',
      title: 'Delete car',
      options: ['Delete', 'Cancel'],
      persistentObject: scaffoldedPo,
    } as any);
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve, 0));
    await new Promise(resolve => setTimeout(resolve, 0));

    component.formData.set({ LicensePlate: 'ABC123', Confirmation: 'ABC123' });
    component.onOption('Delete');

    const result = await promise;
    expect(result.option).toBe('Delete');
    expect(result.persistentObject).toBeDefined();
    const confirmationAttr = result.persistentObject!.attributes.find(a => a.name === 'Confirmation');
    expect(confirmationAttr?.value).toBe('ABC123');
    expect(confirmationAttr?.isValueChanged).toBe(true);
    // Preserve server-issued metadata (id).
    expect(confirmationAttr?.id).toBe('a-conf');
  });

  it('forwards the incoming PO unmodified when no EntityType resolves', async () => {
    const { component, service, fixture } = setupWithTypes([]);
    const promise = service.show({
      step: 'confirm',
      title: 'Delete car',
      options: ['Delete', 'Cancel'],
      persistentObject: scaffoldedPo,
    } as any);
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve, 0));

    component.onOption('Delete');

    const result = await promise;
    // No rebuild — incoming passthrough (values unchanged from scaffold).
    expect(result.persistentObject?.attributes.find(a => a.name === 'Confirmation')?.value).toBeNull();
  });
});
