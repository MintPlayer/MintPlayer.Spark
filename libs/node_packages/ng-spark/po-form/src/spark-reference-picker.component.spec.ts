import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';

import { SparkReferencePickerComponent } from './spark-reference-picker.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { EntityType, QueryResultItem } from '@mintplayer/ng-spark/models';

const companyType: EntityType = { id: 't-company', name: 'Company', clrType: 'Test.Company', attributes: [] };

const companies: QueryResultItem[] = [
  { id: 'companies/1', breadcrumb: 'Acme Corp', values: [] },
  { id: 'companies/2', values: [{ key: 'Name', value: 'Globex' }] },
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
    expect(component.displayValue()).toBe('common.notSelected');
  });

  describe('the loaded value (D10)', () => {
    // companies/500 was stored on the object but lies outside the options, which are one page of the
    // reference query; the server resolved its label when it loaded the object.
    const loaded = { value: 'companies/500', breadcrumb: 'Umbrella' };

    it('shows the server label for a loaded value outside the options', () => {
      const { fixture, component } = createComponent();
      fixture.componentRef.setInput('options', companies);
      fixture.componentRef.setInput('loaded', loaded);
      fixture.componentRef.setInput('value', 'companies/500');

      expect(component.displayValue()).toBe('Umbrella');
    });

    it('shows the picked option row after a change, and the server label again after changing back', () => {
      const { fixture, component } = createComponent();
      fixture.componentRef.setInput('options', companies);
      fixture.componentRef.setInput('loaded', loaded);
      fixture.componentRef.setInput('value', 'companies/500');

      fixture.componentRef.setInput('value', 'companies/1');
      expect(component.displayValue()).toBe('Acme Corp');

      fixture.componentRef.setInput('value', 'companies/500');
      expect(component.displayValue()).toBe('Umbrella');
    });

    it('prefers the server label over the option row while the value is the loaded one', () => {
      // Both render T's template, but the option page may be older than the object: one source wins.
      const { fixture, component } = createComponent();
      fixture.componentRef.setInput('options', companies);
      fixture.componentRef.setInput('loaded', { value: 'companies/1', breadcrumb: 'Acme Corporation' });
      fixture.componentRef.setInput('value', 'companies/1');

      expect(component.displayValue()).toBe('Acme Corporation');
    });

    it('falls back to the id when neither the server nor an option has a label', () => {
      const { fixture, component } = createComponent();
      fixture.componentRef.setInput('options', companies);
      fixture.componentRef.setInput('loaded', { value: 'companies/500', breadcrumb: null });
      fixture.componentRef.setInput('value', 'companies/500');
      expect(component.displayValue()).toBe('companies/500');

      fixture.componentRef.setInput('loaded', { value: 'companies/500', breadcrumb: '   ' });
      expect(component.displayValue()).toBe('companies/500');
    });
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
