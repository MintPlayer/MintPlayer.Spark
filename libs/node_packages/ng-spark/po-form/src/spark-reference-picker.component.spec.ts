import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';

import { SparkReferencePickerComponent } from './spark-reference-picker.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { EntityType, PersistentObject } from '@mintplayer/ng-spark/models';

const companyType: EntityType = { id: 't-company', name: 'Company', clrType: 'Test.Company', attributes: [] };

const companies: PersistentObject[] = [
  { id: 'companies/1', breadcrumb: 'Acme Corp', values: [] } as any,
  { id: 'companies/2', values: [{ key: 'Name', value: 'Globex' }] } as any,
];

function createComponent(serviceOverrides: Partial<SparkService> = {}) {
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([companyType]),
    ...serviceOverrides,
  };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
    ],
  });
  const fixture = TestBed.createComponent(SparkReferencePickerComponent);
  return { fixture, component: fixture.componentInstance, service };
}

describe('SparkReferencePickerComponent', () => {
  it('displayValue prefers the breadcrumb, then the raw id, and shows notSelected when unset', () => {
    const { fixture, component } = createComponent();
    fixture.componentRef.setInput('options', companies);

    fixture.componentRef.setInput('value', 'companies/1');
    expect(component.displayValue()).toBe('Acme Corp');

    // A row carries only a server-resolved breadcrumb; without one there is nothing left to
    // display but the id, which is still more use than an empty cell (#327 M4).
    fixture.componentRef.setInput('value', 'companies/2');
    expect(component.displayValue()).toBe('companies/2');

    fixture.componentRef.setInput('value', 'companies/missing');
    expect(component.displayValue()).toBe('companies/missing');

    fixture.componentRef.setInput('value', null);
    expect(component.displayValue()).toBe('notSelected');
  });

  it('open lazily loads the target entity type for the grid columns and seeds pagination', async () => {
    const { fixture, component, service } = createComponent();
    fixture.componentRef.setInput('options', companies);
    fixture.componentRef.setInput('referenceType', 'Test.Company');

    await component.open();

    expect(service.getEntityTypes).toHaveBeenCalled();
    expect(component.entityType()?.clrType).toBe('Test.Company');
    expect(component.showModal()).toBe(true);
    expect(component.pagination()?.totalRecords).toBe(2);
  });

  it('applyFilter narrows pagination data by any cell value (case-insensitive)', async () => {
    const { fixture, component } = createComponent();
    fixture.componentRef.setInput('options', companies);
    fixture.componentRef.setInput('referenceType', 'Test.Company');
    await component.open();

    component.searchTerm = 'glob';
    component.onSearchChange();

    expect(component.pagination()?.data).toHaveLength(1);
    expect(component.pagination()?.data[0].id).toBe('companies/2');
  });

  it('select emits the picked id via valueChange and closes the modal', async () => {
    const { fixture, component } = createComponent();
    fixture.componentRef.setInput('options', companies);
    fixture.componentRef.setInput('referenceType', 'Test.Company');
    await component.open();

    const emitted = vi.fn();
    component.valueChange.subscribe(emitted);
    component.select(companies[0]);

    expect(emitted).toHaveBeenCalledWith('companies/1');
    expect(component.showModal()).toBe(false);
  });
});
