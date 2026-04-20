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
  LookupReferenceValue,
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

const personType: EntityType = {
  id: 't-person',
  name: 'Person',
  clrType: 'Test.Person',
  attributes: [
    attr({ id: 'a-first', name: 'FirstName', order: 2 }),
    attr({ id: 'a-nick', name: 'Nickname', order: 1, group: 'g-names' }),
    attr({ id: 'a-hidden', name: 'Hidden', isVisible: false, order: 3 }),
    attr({ id: 'a-readonly', name: 'Readonly', isReadOnly: true, order: 4 }),
    attr({ id: 'a-detail-only', name: 'DetailOnly', order: 5, showedOn: ShowedOn.Query }),
    attr({ id: 'a-orphaned', name: 'Orphaned', order: 6, group: 'g-missing' }),
    attr({ id: 'a-company', name: 'Company', dataType: 'Reference', order: 7, query: 'CompanyQuery', referenceType: 'Test.Company' }),
    attr({ id: 'a-role', name: 'Role', order: 8, lookupReferenceType: 'Roles' }),
    attr({ id: 'a-status', name: 'Status', order: 9, lookupReferenceType: 'Roles' }),
  ],
  tabs: [
    { id: 'tab-names', name: 'Names', order: 1 },
  ],
  groups: [
    { id: 'g-names', name: 'Names', tab: 'tab-names', order: 1 },
    { id: 'g-orphans', name: 'Orphans', order: 2 },
  ],
};

const allCompanies: PersistentObject[] = [
  { id: 'companies/1', name: 'Acme', objectTypeId: 't-company', attributes: [] } as any,
];

const rolesLookup: LookupReference = {
  name: 'Roles',
  isTransient: false,
  displayType: 1,
  values: [
    { key: 'admin', values: { en: 'Admin' } as any, isActive: true },
    { key: 'legacy', values: { en: 'Legacy' } as any, isActive: false },
  ] as LookupReferenceValue[],
} as any;

function createComponent(serviceOverrides: Partial<SparkService> = {}) {
  const service: any = {
    executeQueryByName: vi.fn().mockResolvedValue({ data: allCompanies, totalRecords: 1 }),
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    getPermissions: vi.fn().mockResolvedValue({ canRead: true, canCreate: true, canUpdate: true, canDelete: true }),
    getLookupReference: vi.fn().mockResolvedValue(rolesLookup),
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
  return { fixture, component, service };
}

async function flush(): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await new Promise<void>(r => setTimeout(r, 0));
  }
}

async function setEntityType(fixture: any, et: EntityType): Promise<void> {
  fixture.componentRef.setInput('entityType', et);
  fixture.detectChanges();
  await flush();
}

describe('SparkPoFormComponent', () => {
  describe('attribute filtering and grouping', () => {
    it('editableAttributes excludes hidden, read-only, and detail-only attributes, sorted by order', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);

      const names = component.editableAttributes().map(a => a.name);
      expect(names).not.toContain('Hidden');
      expect(names).not.toContain('Readonly');
      expect(names).not.toContain('DetailOnly');
      expect(names[0]).toBe('Nickname');
      expect(names[1]).toBe('FirstName');
    });

    it('ungroupedAttributes includes attrs without a group AND attrs with an unknown group', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);

      const ungrouped = component.ungroupedAttributes().map(a => a.name);
      expect(ungrouped).toContain('FirstName');
      expect(ungrouped).toContain('Orphaned');
      expect(ungrouped).not.toContain('Nickname');
    });

    it('resolvedTabs prepends the default tab when ungrouped attrs exist', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);

      const tabs = component.resolvedTabs();
      expect(tabs[0].id).toBe('__default__');
      expect(tabs.map(t => t.id)).toContain('tab-names');
    });

    it('resolvedTabs returns only defined tabs when all groups are tabbed and no ungrouped attrs exist', async () => {
      const et: EntityType = {
        ...personType,
        attributes: [attr({ id: 'a-nick', name: 'Nickname', order: 1, group: 'g-names' })],
        groups: [{ id: 'g-names', name: 'Names', tab: 'tab-names', order: 1 }],
      };
      const { fixture, component } = createComponent();
      await setEntityType(fixture, et);

      const tabs = component.resolvedTabs();
      expect(tabs).toHaveLength(1);
      expect(tabs[0].id).toBe('tab-names');
    });

    it('groupsForTab for the default tab returns only untabbed groups', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);

      const defaultTab = component.resolvedTabs().find(t => t.id === '__default__')!;
      const groups = component.groupsForTab(defaultTab).map(g => g.id);
      expect(groups).toEqual(['g-orphans']);
    });

    it('attrsForGroup returns only editable attributes assigned to the group', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);

      const namesGroup = personType.groups!.find(g => g.id === 'g-names')!;
      const attrs = component.attrsForGroup(namesGroup).map(a => a.name);
      expect(attrs).toEqual(['Nickname']);
    });
  });

  describe('reference and lookup loading', () => {
    it('loadReferenceOptions calls executeQueryByName once per Reference attribute with a query', async () => {
      const { fixture, component, service } = createComponent();
      await setEntityType(fixture, personType);

      expect(service.executeQueryByName).toHaveBeenCalledWith('CompanyQuery', expect.any(Object));
      expect(component.referenceOptions()['Company']).toEqual(allCompanies);
    });

    it('loadLookupReferenceOptions deduplicates across attributes sharing the same lookupReferenceType', async () => {
      const { fixture, component, service } = createComponent();
      await setEntityType(fixture, personType);

      expect(service.getLookupReference).toHaveBeenCalledTimes(1);
      expect(service.getLookupReference).toHaveBeenCalledWith('Roles');
      expect(component.lookupReferenceOptions()['Roles'].name).toBe('Roles');
    });

    it('getLookupOptions returns only active values', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);

      const options = component.getLookupOptions(personType.attributes.find(a => a.name === 'Role')!);
      expect(options.map(o => o.key)).toEqual(['admin']);
    });
  });

  describe('lookup modal', () => {
    it('openLookupSelector seeds items and opens the modal; selectLookupItem updates formData and closes', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const roleAttr = personType.attributes.find(a => a.name === 'Role')!;

      component.openLookupSelector(roleAttr);
      expect(component.showLookupModal()).toBe(true);
      expect(component.lookupModalItems()).toHaveLength(1);

      component.selectLookupItem({ key: 'admin', values: { en: 'Admin' } as any, isActive: true });

      expect(component.formData()['Role']).toBe('admin');
      expect(component.showLookupModal()).toBe(false);
      expect(component.editingLookupAttr()).toBeNull();
    });

    it('filteredLookupItems narrows items by search term (case-insensitive, matches key or translation)', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const roleAttr = personType.attributes.find(a => a.name === 'Role')!;

      component.openLookupSelector(roleAttr);
      component.lookupSearchTerm.set('adm');

      expect(component.filteredLookupItems().map(i => i.key)).toEqual(['admin']);

      component.lookupSearchTerm.set('nope');
      expect(component.filteredLookupItems()).toHaveLength(0);
    });
  });

  describe('reference modal', () => {
    it('openReferenceSelector loads the matching entity type and seeds paginationData', async () => {
      const companyType: EntityType = { id: 't-company', name: 'Company', clrType: 'Test.Company', attributes: [] };
      const { fixture, component, service } = createComponent({
        getEntityTypes: vi.fn().mockResolvedValue([personType, companyType]),
      });
      await setEntityType(fixture, personType);
      const companyAttr = personType.attributes.find(a => a.name === 'Company')!;

      await component.openReferenceSelector(companyAttr);

      expect(service.getEntityTypes).toHaveBeenCalled();
      expect(component.referenceModalEntityType()?.clrType).toBe('Test.Company');
      expect(component.showReferenceModal()).toBe(true);
      expect(component.referenceModalPagination()?.totalRecords).toBe(1);
    });

    it('selectReferenceItem writes the item id into formData and closes the modal', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const companyAttr = personType.attributes.find(a => a.name === 'Company')!;
      await component.openReferenceSelector(companyAttr);

      component.selectReferenceItem(allCompanies[0]);

      expect(component.formData()['Company']).toBe('companies/1');
      expect(component.showReferenceModal()).toBe(false);
    });

    it('onReferenceSearchChange narrows paginationData to matching items by name', async () => {
      const more = [
        { id: 'companies/1', name: 'Acme', objectTypeId: 't-company', attributes: [] } as any,
        { id: 'companies/2', name: 'Globex', objectTypeId: 't-company', attributes: [] } as any,
      ];
      const { fixture, component } = createComponent({
        executeQueryByName: vi.fn().mockResolvedValue({ data: more, totalRecords: 2 }),
      });
      await setEntityType(fixture, personType);
      const companyAttr = personType.attributes.find(a => a.name === 'Company')!;
      await component.openReferenceSelector(companyAttr);

      component.referenceSearchTerm = 'glob';
      component.onReferenceSearchChange();

      expect(component.referenceModalPagination()?.data).toHaveLength(1);
      expect(component.referenceModalPagination()?.data[0].name).toBe('Globex');
    });
  });

  describe('AsDetail object modal', () => {
    it('openAsDetailEditor seeds asDetailFormData from formData for single-object attrs', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const nested = attr({ id: 'a-addr', name: 'Address', dataType: 'AsDetail' });
      component.formData.set({ Address: { Street: 'Main' } });

      component.openAsDetailEditor(nested);

      expect(component.showAsDetailModal()).toBe(true);
      expect(component.asDetailFormData()).toEqual({ Street: 'Main' });
      expect(component.editingArrayIndex()).toBeNull();
    });

    it('saveAsDetailObject writes single-object value back into formData and closes the modal', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const nested = attr({ id: 'a-addr', name: 'Address', dataType: 'AsDetail' });
      component.openAsDetailEditor(nested);
      component.asDetailFormData.set({ Street: 'Broadway' });

      component.saveAsDetailObject();

      expect(component.formData()['Address']).toEqual({ Street: 'Broadway' });
      expect(component.showAsDetailModal()).toBe(false);
    });

    it('addArrayItem + saveAsDetailObject appends to the array; editArrayItem updates in place', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const jobs = attr({ id: 'a-jobs', name: 'Jobs', dataType: 'AsDetail', isArray: true });
      component.formData.set({ Jobs: [] });

      component.addArrayItem(jobs);
      component.asDetailFormData.set({ Title: 'Dev' });
      component.saveAsDetailObject();
      expect(component.formData()['Jobs']).toEqual([{ Title: 'Dev' }]);

      component.editArrayItem(jobs, 0);
      expect(component.editingArrayIndex()).toBe(0);
      component.asDetailFormData.set({ Title: 'Senior Dev' });
      component.saveAsDetailObject();

      expect(component.formData()['Jobs']).toEqual([{ Title: 'Senior Dev' }]);
    });

    it('removeArrayItem splices the indexed entry out of the array', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const jobs = attr({ id: 'a-jobs', name: 'Jobs', dataType: 'AsDetail', isArray: true });
      component.formData.set({ Jobs: [{ Title: 'A' }, { Title: 'B' }] });

      component.removeArrayItem(jobs, 0);

      expect(component.formData()['Jobs']).toEqual([{ Title: 'B' }]);
    });

    it('addInlineRow appends an empty object without opening the modal', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const jobs = attr({ id: 'a-jobs', name: 'Jobs', dataType: 'AsDetail', isArray: true, editMode: 'inline' });
      component.formData.set({ Jobs: [] });

      component.addInlineRow(jobs);

      expect(component.formData()['Jobs']).toEqual([{}]);
      expect(component.showAsDetailModal()).toBe(false);
    });
  });

  describe('outputs and helpers', () => {
    it('hasError returns true when validationErrors contain the given attribute', async () => {
      const { fixture, component } = createComponent();
      fixture.componentRef.setInput('validationErrors', [
        { attributeName: 'FirstName', errorMessage: { en: 'Required' } as any, ruleType: 'required' },
      ]);
      await setEntityType(fixture, personType);

      expect(component.hasError('FirstName')).toBe(true);
      expect(component.hasError('LastName')).toBe(false);
    });

    it('onSave and onCancel emit their outputs', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);

      const saved = vi.fn();
      const cancelled = vi.fn();
      component.save.subscribe(saved);
      component.cancel.subscribe(cancelled);

      component.onSave();
      component.onCancel();

      expect(saved).toHaveBeenCalled();
      expect(cancelled).toHaveBeenCalled();
    });
  });
});
