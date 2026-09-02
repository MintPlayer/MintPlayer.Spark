import { Component, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';

import { SparkPoFormComponent } from './spark-po-form.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import {
  EntityAttributeDefinition,
  EntityType,
  EReferenceDisplayType,
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
  displayType: 0,
  values: [
    { key: 'admin', values: { en: 'Admin' } as any, isActive: true },
    { key: 'legacy', values: { en: 'Legacy' } as any, isActive: false },
  ] as LookupReferenceValue[],
} as any;

function createComponent(serviceOverrides: Partial<SparkService> = {}, rendererRegistry: any[] = []) {
  const service: any = {
    executeQueryByName: vi.fn().mockResolvedValue({ columns: [], items: allCompanies, totalItems: 1 }),
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getLookupReference: vi.fn().mockResolvedValue(rolesLookup),
    ...serviceOverrides,
  };

  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: rendererRegistry },
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

  describe('picker value-change handlers', () => {
    // The modal pick UI now lives in spark-reference-picker / spark-lookup-picker;
    // the form's job is only to write the emitted value back into formData.
    it('onReferenceValueChange writes the emitted id into formData', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const companyAttr = personType.attributes.find(a => a.name === 'Company')!;

      component.onReferenceValueChange(companyAttr, 'companies/1');
      expect(component.formData()['Company']).toBe('companies/1');

      component.onReferenceValueChange(companyAttr, null);
      expect(component.formData()['Company']).toBeNull();
    });

    it('onLookupValueChange writes the emitted key into formData', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const roleAttr = personType.attributes.find(a => a.name === 'Role')!;

      component.onLookupValueChange(roleAttr, 'admin');
      expect(component.formData()['Role']).toBe('admin');
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

  describe('AsDetail drag-reorder ([Sortable])', () => {
    const stepType: EntityType = {
      id: 't-step',
      name: 'Step',
      clrType: 'Test.Step',
      attributes: [attr({ id: 's-label', name: 'Label', order: 1 })],
    };
    const sortableParent = (isSortable: boolean): EntityType => ({
      id: 't-parent',
      name: 'Parent',
      clrType: 'Test.Parent',
      attributes: [
        attr({
          id: 'a-steps', name: 'Steps', dataType: 'AsDetail', isArray: true,
          editMode: 'inline', asDetailType: 'Test.Step', isSortable, order: 1,
        }),
      ],
    });

    it('onAsDetailReorder moves a row to the new index in formData', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const steps = attr({ name: 'Steps', dataType: 'AsDetail', isArray: true, isSortable: true });
      component.formData.set({ Steps: [{ Label: 'a' }, { Label: 'b' }, { Label: 'c' }] });

      component.onAsDetailReorder(steps, { previousIndex: 2, currentIndex: 0 } as any);

      expect(component.formData()['Steps'].map((r: any) => r.Label)).toEqual(['c', 'a', 'b']);
    });

    it('onAsDetailReorder is a no-op when the index is unchanged', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      const steps = attr({ name: 'Steps', dataType: 'AsDetail', isArray: true, isSortable: true });
      const before = [{ Label: 'a' }, { Label: 'b' }];
      component.formData.set({ Steps: before });

      component.onAsDetailReorder(steps, { previousIndex: 1, currentIndex: 1 } as any);

      // Reference is untouched — no needless re-emit / change-flag.
      expect(component.formData()['Steps']).toBe(before);
    });

    it('renders a drag handle per row when the AsDetail array is sortable', async () => {
      const { fixture, component } = createComponent({
        getEntityTypes: vi.fn().mockResolvedValue([sortableParent(true), stepType]),
      });
      component.formData.set({ Steps: [{ Label: 'a' }, { Label: 'b' }] });
      await setEntityType(fixture, sortableParent(true));
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelectorAll('.bi-grip-vertical').length).toBe(2);
    });

    it('renders no drag handle when the AsDetail array is not sortable', async () => {
      const { fixture, component } = createComponent({
        getEntityTypes: vi.fn().mockResolvedValue([sortableParent(false), stepType]),
      });
      component.formData.set({ Steps: [{ Label: 'a' }, { Label: 'b' }] });
      await setEntityType(fixture, sortableParent(false));
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelectorAll('.bi-grip-vertical').length).toBe(0);
    });
  });

  describe('inline AsDetail edit parity', () => {
    // A child type exercising the inline column kinds: scalar, read-only, lookup, custom renderer.
    const richChild: EntityType = {
      id: 't-rich',
      name: 'RichChild',
      clrType: 'Test.RichChild',
      attributes: [
        attr({ id: 'c-label', name: 'Label', order: 1 }),
        attr({ id: 'c-ro', name: 'Code', isReadOnly: true, order: 2 }),
        attr({ id: 'c-role', name: 'Role', lookupReferenceType: 'Roles', order: 3 }),
      ],
    };
    const inlineParent: EntityType = {
      id: 't-parent',
      name: 'Parent',
      clrType: 'Test.Parent',
      attributes: [
        attr({ id: 'a-items', name: 'Items', dataType: 'AsDetail', isArray: true, editMode: 'inline', asDetailType: 'Test.RichChild', order: 1 }),
      ],
    };

    it('B2: loadAsDetailTypes loads lookup options for child columns, and getLookupOptions resolves them', async () => {
      const { fixture, component, service } = createComponent({
        getEntityTypes: vi.fn().mockResolvedValue([inlineParent, richChild]),
      });
      await setEntityType(fixture, inlineParent);

      expect(service.getLookupReference).toHaveBeenCalledWith('Roles');
      expect(component.lookupReferenceOptions()['Roles']?.name).toBe('Roles');
      const roleCol = richChild.attributes.find(a => a.name === 'Role')!;
      expect(component.getLookupOptions(roleCol).map(o => o.key)).toEqual(['admin']);
    });

    it('B1: a read-only child column renders read-only text, not an input', async () => {
      const { fixture, component } = createComponent({
        getEntityTypes: vi.fn().mockResolvedValue([inlineParent, richChild]),
      });
      component.formData.set({ Items: [{ Label: 'a', Code: 'X1', Role: 'admin' }] });
      await setEntityType(fixture, inlineParent);
      fixture.detectChanges();

      const cells = fixture.nativeElement.querySelectorAll('tbody tr td');
      // Read-only "Code" cell shows a span and contains no editable <input>.
      const html = fixture.nativeElement.innerHTML as string;
      expect(html).toContain('X1');
      const inputs = fixture.nativeElement.querySelectorAll('tbody input');
      // Only the editable "Label" scalar is an <input>; Code (read-only) and Role (select) are not.
      expect(inputs.length).toBe(1);
      expect(cells.length).toBeGreaterThan(0);
    });

    it('B3: getAsDetailCellEditRenderer returns the registered edit component for a renderer column', async () => {
      @Component({ selector: 'spec-dummy-editor', standalone: true, template: '' })
      class DummyEditor {
        value = input<any>();
        valueChange = input<(value: any) => void>();
      }
      const registry = [{ name: 'my-editor', editComponent: DummyEditor, columnComponent: null }];
      const { fixture, component } = createComponent({}, registry);
      await setEntityType(fixture, personType);

      const col = attr({ name: 'Custom', renderer: 'my-editor' });
      expect(component.getAsDetailCellEditRenderer(col)).toBe(DummyEditor);
      // Edit-renderer inputs write back into the row and flag a change.
      const row: Record<string, any> = { Custom: 'old' };
      const inputs = component.getAsDetailCellEditRendererInputs(DummyEditor, row, col);
      inputs['valueChange']('new');
      expect(row['Custom']).toBe('new');
    });

    it('edit renderer without valueChange gets a filtered bag (write-back silently disabled)', async () => {
      @Component({ selector: 'spec-readonly-editor', standalone: true, template: '' })
      class ReadonlyEditor {
        value = input<any>();
      }
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);

      const col = attr({ name: 'Custom', renderer: 'my-editor' });
      component.formData.set({ Custom: 'x' });
      const inputs = component.getEditRendererInputs(ReadonlyEditor, col);
      expect(Object.keys(inputs)).toEqual(['value']);
      expect(inputs['value']).toBe('x');

      const cellInputs = component.getAsDetailCellEditRendererInputs(ReadonlyEditor, { Custom: 'y' }, col);
      expect(Object.keys(cellInputs)).toEqual(['value']);
    });

    it('B3: getAsDetailCellEditRenderer is null when the column has no renderer', async () => {
      const { fixture, component } = createComponent();
      await setEntityType(fixture, personType);
      expect(component.getAsDetailCellEditRenderer(attr({ name: 'Plain' }))).toBeNull();
    });

    it('B5: per-cell errors are keyed by "{attr}[{row}].{col}"', async () => {
      const { fixture, component } = createComponent();
      fixture.componentRef.setInput('validationErrors', [
        { attributeName: 'Items[1].Label', errorMessage: { en: 'Label required' } as any, ruleType: 'required' },
      ]);
      await setEntityType(fixture, personType);

      const items = attr({ name: 'Items', dataType: 'AsDetail', isArray: true });
      const label = attr({ name: 'Label' });
      expect(component.hasInlineError(items, 1, label)).toBe(true);
      expect(component.hasInlineError(items, 0, label)).toBe(false);
      expect(component.inlineErrorMessage(items, 1, label)).toBe('Label required');
      expect(component.inlineErrorMessage(items, 0, label)).toBeNull();
    });

    it('B6: an inline Reference column flagged Modal renders the reference picker, not a <bs-select>', async () => {
      const refChild: EntityType = {
        id: 't-refchild', name: 'RefChild', clrType: 'Test.RefChild',
        attributes: [attr({ id: 'c-co', name: 'CompanyId', dataType: 'Reference', order: 1, query: 'CompanyQuery', referenceType: 'Test.Company', referenceDisplayType: EReferenceDisplayType.Modal })],
      };
      const refParent: EntityType = {
        id: 't-refparent', name: 'RefParent', clrType: 'Test.RefParent',
        attributes: [attr({ id: 'a-rows', name: 'Rows', dataType: 'AsDetail', isArray: true, editMode: 'inline', asDetailType: 'Test.RefChild', order: 1 })],
      };
      const { fixture, component } = createComponent({
        getEntityTypes: vi.fn().mockResolvedValue([refParent, refChild]),
      });
      component.formData.set({ Rows: [{ CompanyId: null }] });
      await setEntityType(fixture, refParent);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('spark-reference-picker')).toBeTruthy();
      expect(fixture.nativeElement.querySelector('tbody bs-select')).toBeFalsy();
    });

    it('B6: an inline Reference column without the flag still renders a <bs-select>', async () => {
      const refChild: EntityType = {
        id: 't-refchild2', name: 'RefChild2', clrType: 'Test.RefChild2',
        attributes: [attr({ id: 'c-co', name: 'CompanyId', dataType: 'Reference', order: 1, query: 'CompanyQuery', referenceType: 'Test.Company' })],
      };
      const refParent: EntityType = {
        id: 't-refparent2', name: 'RefParent2', clrType: 'Test.RefParent2',
        attributes: [attr({ id: 'a-rows', name: 'Rows', dataType: 'AsDetail', isArray: true, editMode: 'inline', asDetailType: 'Test.RefChild2', order: 1 })],
      };
      const { fixture, component } = createComponent({
        getEntityTypes: vi.fn().mockResolvedValue([refParent, refChild]),
      });
      component.formData.set({ Rows: [{ CompanyId: null }] });
      await setEntityType(fixture, refParent);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('spark-reference-picker')).toBeFalsy();
      expect(fixture.nativeElement.querySelector('tbody bs-select')).toBeTruthy();
    });

    it('B7: an inline lookup column with a Modal-display lookup renders the lookup picker', async () => {
      const modalLookup = { ...rolesLookup, displayType: 1 } as LookupReference;
      const lkChild: EntityType = {
        id: 't-lkchild', name: 'LkChild', clrType: 'Test.LkChild',
        attributes: [attr({ id: 'c-role', name: 'Role', lookupReferenceType: 'Roles', order: 1 })],
      };
      const lkParent: EntityType = {
        id: 't-lkparent', name: 'LkParent', clrType: 'Test.LkParent',
        attributes: [attr({ id: 'a-rows', name: 'Rows', dataType: 'AsDetail', isArray: true, editMode: 'inline', asDetailType: 'Test.LkChild', order: 1 })],
      };
      const { fixture, component } = createComponent({
        getEntityTypes: vi.fn().mockResolvedValue([lkParent, lkChild]),
        getLookupReference: vi.fn().mockResolvedValue(modalLookup),
      });
      component.formData.set({ Rows: [{ Role: null }] });
      await setEntityType(fixture, lkParent);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('spark-lookup-picker')).toBeTruthy();
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

      // onSave is async now: it flushes any pending refresh first, so that a value typed and never
      // blurred still reshapes the object before it goes to the server.
      await component.onSave();
      component.onCancel();

      expect(saved).toHaveBeenCalled();
      expect(cancelled).toHaveBeenCalled();
    });
  });

  describe('attribute descriptions (#348)', () => {
    const describedType: EntityType = {
      ...personType,
      attributes: [
        attr({ id: 'a-first', name: 'FirstName', order: 1, description: { en: 'Given name.', nl: 'Voornaam.' } }),
        attr({ id: 'a-last', name: 'LastName', order: 2 }),
      ],
      tabs: [],
      groups: [],
    };

    it('renders the [i] only beside attributes that declare a description', async () => {
      const { fixture } = createComponent();
      await setEntityType(fixture, describedType);
      fixture.detectChanges();

      const labels: HTMLLabelElement[] = Array.from(fixture.nativeElement.querySelectorAll('label'));
      const first = labels.find(l => l.getAttribute('for') === 'FirstName')!;
      const last = labels.find(l => l.getAttribute('for') === 'LastName')!;

      expect(first.querySelector('spark-attribute-description button')).not.toBeNull();
      expect(first.querySelector('spark-attribute-description button')!.getAttribute('aria-label')).toBe('Given name.');
      expect(last.querySelector('spark-attribute-description button')).toBeNull();
    });
  });
});
