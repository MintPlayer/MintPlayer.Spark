import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BsFormComponent } from '@mintplayer/ng-bootstrap/form';
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

  it('open lazily loads the target entity type and seeds pagination', async () => {
    const { fixture, component, service } = createComponent();
    fixture.componentRef.setInput('options', companies);
    fixture.componentRef.setInput('referenceType', 'Test.Company');

    await component.open();

    expect(service.getEntityTypes).toHaveBeenCalled();
    expect(component.entityType()?.clrType).toBe('Test.Company');
    expect(component.showModal()).toBe(true);
    expect(component.pagination()?.totalRecords).toBe(2);
  });

  it('without a query, the search narrows the options in memory by any cell value (case-insensitive)', async () => {
    const { fixture, component } = createComponent();
    fixture.componentRef.setInput('options', companies);
    fixture.componentRef.setInput('referenceType', 'Test.Company');
    await component.open();

    expect(component.fetchFn()).toBeNull();
    component.onSearchChange('glob');

    expect(component.pagination()?.data).toHaveLength(1);
    expect(component.pagination()?.data[0].id).toBe('companies/2');
  });

  describe('with a query (server paging)', () => {
    // 120 candidates on the server; the form pre-loaded only the first page of them.
    const page = (skip: number, take: number) => ({
      columns: [],
      items: Array.from({ length: take }, (_, i) => ({ id: `companies/${skip + i + 1}`, breadcrumb: `Company ${skip + i + 1}`, values: [] })),
      totalItems: 120,
      skip,
      take,
    });

    async function openWithQuery() {
      const executeQueryByName = vi.fn((_name: string, o: any) => Promise.resolve(page(o.skip, o.take)));
      const created = createComponent({ executeQueryByName } as any);
      created.fixture.componentRef.setInput('options', companies);
      created.fixture.componentRef.setInput('referenceType', 'Test.Company');
      created.fixture.componentRef.setInput('query', 'GetCompanies');
      created.fixture.componentRef.setInput('queryParent', { parentId: 'people/1', parentType: 'Test.Person' });
      await created.component.open();
      return { ...created, executeQueryByName };
    }

    it('fetches each page from the server with skip/take and the parent, and reports totalItems to the pager', async () => {
      const { component, executeQueryByName } = await openWithQuery();
      const fetch = component.fetchFn();
      expect(fetch).not.toBeNull();

      const result = await fetch!({ page: 9, perPage: 10, sortColumns: [] } as any);

      expect(executeQueryByName).toHaveBeenCalledWith('GetCompanies', expect.objectContaining({
        skip: 80, take: 10, parentId: 'people/1', parentType: 'Test.Person', search: undefined,
      }));
      expect(result.totalRecords).toBe(120);
      expect(result.totalPages).toBe(12);
      expect(result.data[0].id).toBe('companies/81');
    });

    it('sends the search term to the server and resets to page 1', async () => {
      const { component, executeQueryByName } = await openWithQuery();
      component.page.set(4);

      component.onSearchChange('  Company 117 ');
      expect(component.page()).toBe(1);

      await component.fetchFn()!({ page: 1, perPage: 10, sortColumns: [] } as any);
      expect(executeQueryByName).toHaveBeenLastCalledWith('GetCompanies', expect.objectContaining({ skip: 0, take: 10, search: 'Company 117' }));
      expect(component.resultCount()).toBe(120);
    });

    it('forwards the sort the grid asks for', async () => {
      const { component, executeQueryByName } = await openWithQuery();
      const sortColumns = [{ property: 'Name', direction: 'descending' }];

      await component.fetchFn()!({ page: 1, perPage: 25, sortColumns } as any);

      expect(executeQueryByName).toHaveBeenLastCalledWith('GetCompanies', expect.objectContaining({ sortColumns, take: 25 }));
    });

    it(`draws the columns the result declares, in the result's order, not the entity type's attributes (#284, D9)`, async () => {
      // The type has a Query-surface attribute the result leaves out (the query's columnOrder hid it,
      // or rights stripped it), and its `order` would put Name before City. The result decides both.
      const typed: EntityType = {
        ...companyType,
        attributes: [
          { id: 'a-name', name: 'Name', dataType: 'string', order: 1, showedOn: 'Query, PersistentObject' },
          { id: 'a-city', name: 'City', dataType: 'string', order: 2, showedOn: 'Query, PersistentObject' },
          { id: 'a-secret', name: 'Secret', dataType: 'string', order: 3, showedOn: 'Query, PersistentObject' },
        ] as any,
      };
      const columns = [
        { name: 'City', dataType: 'string', order: 2 },
        { name: 'Name', dataType: 'string', order: 1, canSort: false },
      ];
      const executeQueryByName = vi.fn().mockResolvedValue({ ...page(0, 10), columns });
      const { fixture, component } = createComponent({ executeQueryByName, getEntityTypes: vi.fn().mockResolvedValue([typed]) } as any);
      fixture.componentRef.setInput('referenceType', 'Test.Company');
      fixture.componentRef.setInput('query', 'GetCompanies');
      await component.open();
      expect(component.columns()).toEqual([]);

      await component.fetchFn()!({ page: 1, perPage: 10, sortColumns: [] } as any);

      expect(component.columns().map(c => c.name)).toEqual(['City', 'Name']);
      expect(component.columns().map(c => c.canSort)).toEqual([undefined, false]);
    });

    it('keeps the columns of the last good page when a page fails', async () => {
      const columns = [{ name: 'Name', dataType: 'string', order: 1 }];
      const executeQueryByName = vi.fn()
        .mockResolvedValueOnce({ ...page(0, 10), columns })
        .mockRejectedValueOnce(new Error('boom'));
      const { fixture, component } = createComponent({ executeQueryByName } as any);
      fixture.componentRef.setInput('referenceType', 'Test.Company');
      fixture.componentRef.setInput('query', 'GetCompanies');
      await component.open();

      await component.fetchFn()!({ page: 1, perPage: 10, sortColumns: [] } as any);
      await component.fetchFn()!({ page: 2, perPage: 10, sortColumns: [] } as any);

      expect(component.columns().map(c => c.name)).toEqual(['Name']);
    });

    it('reports a failed page instead of showing it as an empty list', async () => {
      const executeQueryByName = vi.fn().mockRejectedValue(new Error('boom'));
      const { fixture, component } = createComponent({ executeQueryByName } as any);
      fixture.componentRef.setInput('referenceType', 'Test.Company');
      fixture.componentRef.setInput('query', 'GetCompanies');
      await component.open();

      const result = await component.fetchFn()!({ page: 1, perPage: 10, sortColumns: [] } as any);

      expect(result.data).toEqual([]);
      expect(component.loadError()).not.toBeNull();
    });

    it('labels a row picked from a later page, which the options do not hold, and emits it before the id', async () => {
      const { fixture, component } = await openWithQuery();
      const events: string[] = [];
      component.rowPicked.subscribe(item => events.push(`row:${item.id}`));
      component.valueChange.subscribe(id => events.push(`value:${id}`));

      const row = page(116, 1).items[0];
      component.select(row);
      fixture.componentRef.setInput('value', row.id);

      expect(events).toEqual(['row:companies/117', 'value:companies/117']);
      expect(component.displayValue()).toBe('Company 117');
    });
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

  it('styles its trigger input as a form control inside a bs-form, red when invalid', () => {
    // ng-bootstrap adds .form-control through BsFormControlDirective, which only matches inputs in
    // templates that import it: the parent form's <bs-form> does not cover the picker's own input.
    @Component({
      template: `<bs-form><spark-reference-picker inputId="Org" [isInvalid]="invalid()" /></bs-form>`,
      imports: [BsFormComponent, SparkReferencePickerComponent],
    })
    class HostComponent {
      readonly invalid = signal(false);
    }
    TestBed.configureTestingModule({
      providers: [
        provideNoopAnimations(),
        { provide: SparkService, useValue: { getEntityTypes: vi.fn().mockResolvedValue([companyType]) } },
        { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
      ],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const trigger = (fixture.nativeElement as HTMLElement).querySelector('#Org')!;
    expect(trigger.classList).toContain('form-control');
    expect(trigger.classList).not.toContain('is-invalid');

    fixture.componentInstance.invalid.set(true);
    fixture.detectChanges();
    expect(trigger.classList).toContain('is-invalid');
  });
});
