import { Component, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { By } from '@angular/platform-browser';
import { describe, expect, it, vi } from 'vitest';

import { SparkPoFormComponent } from './spark-po-form.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityAttributeDefinition, EntityType, ShowedOn, translated } from '@mintplayer/ng-spark/models';

/**
 * X15 (commerce PRD §5): a single (non-collection) AsDetail value object, such as a venue's Address,
 * edited in place with `editMode: "inline"` — its fields, their rules, the server's errors on them, and
 * what "no address" means — and a renderer registered for such an attribute actually being used.
 */
function attr(partial: Partial<EntityAttributeDefinition>): EntityAttributeDefinition {
  return {
    id: partial.name || 'a',
    name: 'a',
    dataType: 'string',
    isRequired: false,
    isReadOnly: false,
    order: 1,
    showedOn: ShowedOn.PersistentObject,
    rules: [],
    ...partial,
  } as EntityAttributeDefinition;
}

const addressType: EntityType = {
  id: 't-address',
  name: 'Address',
  clrType: 'Test.Address',
  attributes: [
    attr({ id: 'a-street', name: 'Street', isRequired: true, order: 1 }),
    attr({ id: 'a-city', name: 'City', order: 2 }),
  ],
};

function venueType(address: Partial<EntityAttributeDefinition> = {}): EntityType {
  return {
    id: 't-venue',
    name: 'Venue',
    clrType: 'Test.Venue',
    attributes: [
      attr({ id: 'a-name', name: 'Name', order: 1 }),
      attr({ id: 'a-address', name: 'Address', dataType: 'AsDetail', asDetailType: 'Test.Address', isArray: false, editMode: 'inline', order: 2, ...address }),
    ],
  };
}

function createComponent(rendererRegistry: any[] = []) {
  const service: any = {
    executeQueryByName: vi.fn().mockResolvedValue({ columns: [], items: [], totalItems: 0 }),
    // The row type is not in the Query-gated catalogue; it arrives as one of the parent's detailTypes.
    getEntityTypes: vi.fn().mockResolvedValue([]),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getLookupReference: vi.fn(),
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
  return { fixture, component: fixture.componentInstance, service };
}

async function flush(): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await new Promise<void>(r => setTimeout(r, 0));
  }
}

async function render(fixture: any, formData: Record<string, any>, address: Partial<EntityAttributeDefinition> = {}): Promise<void> {
  fixture.componentRef.setInput('entityType', { ...venueType(address), detailTypes: [addressType] });
  fixture.componentRef.setInput('formData', formData);
  fixture.detectChanges();
  await flush();
  fixture.detectChanges();
  await flush();
}

function embeddedForms(fixture: any, host: SparkPoFormComponent): SparkPoFormComponent[] {
  return fixture.debugElement.queryAll(By.directive(SparkPoFormComponent))
    .map((d: any) => d.componentInstance as SparkPoFormComponent)
    .filter((c: SparkPoFormComponent) => c !== host);
}

describe('SparkPoFormComponent — a single AsDetail object edited inline (X15)', () => {
  it('renders the object\'s fields in place, not the summary box and modal', async () => {
    const { fixture, component } = createComponent();
    await render(fixture, { Name: 'Sportpaleis', Address: { Street: 'Schijnpoortweg 119', City: 'Antwerpen' } });

    expect(embeddedForms(fixture, component)).toHaveLength(1);
    const street = fixture.nativeElement.querySelector('input#Street') as HTMLInputElement;
    expect(street).not.toBeNull();
    await fixture.whenStable();
    expect(street.value).toBe('Schijnpoortweg 119');
    expect(fixture.nativeElement.querySelector('input#Address[readonly]')).toBeNull();
  });

  it('writes an edit inside the object back into the parent\'s formData', async () => {
    const { fixture, component } = createComponent();
    await render(fixture, { Address: { Street: '', City: null } });

    const street = fixture.nativeElement.querySelector('input#Street') as HTMLInputElement;
    street.value = 'Rue Neuve 1';
    street.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await flush();

    expect(component.formData()['Address']['Street']).toBe('Rue Neuve 1');
  });

  it('shows an Add button for a null object, and Add starts an empty one', async () => {
    const { fixture, component } = createComponent();
    await render(fixture, { Address: null });

    expect(embeddedForms(fixture, component)).toHaveLength(0);
    const add = fixture.nativeElement.querySelector('.spark-inline-detail-empty button') as HTMLButtonElement;
    expect(add).not.toBeNull();

    add.click();
    fixture.detectChanges();
    await flush();

    expect(component.formData()['Address']).toEqual({});
    expect(embeddedForms(fixture, component)).toHaveLength(1);
  });

  it('Delete sets an optional object to null', async () => {
    const { fixture, component } = createComponent();
    await render(fixture, { Address: { Street: 'Main 1' } });

    (fixture.nativeElement.querySelector('.spark-inline-detail-actions button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(component.formData()['Address']).toBeNull();
  });

  it('offers no Delete for a required object', async () => {
    const { fixture } = createComponent();
    await render(fixture, { Address: { Street: 'Main 1' } }, { isRequired: true });

    expect(fixture.nativeElement.querySelector('.spark-inline-detail-actions')).toBeNull();
  });

  it('counts the object\'s field rules, under the server\'s Address.Street path, and save stops on them', async () => {
    const { fixture, component } = createComponent();
    await render(fixture, { Address: { Street: '', City: 'Gent' } });
    const saved = vi.fn();
    component.save.subscribe(saved);

    expect(component.clientRuleFailures().map(f => f.attributeName)).toEqual(['Address.Street']);
    await component.onSave();

    expect(saved).not.toHaveBeenCalled();
    expect(component.formData()['Address']).toEqual({ Street: '', City: 'Gent' });
  });

  it('saves an optional object left entirely blank as null, without judging its fields', async () => {
    const { fixture, component } = createComponent();
    await render(fixture, { Name: 'Vooruit', Address: { Street: '  ', City: null } });
    const saved = vi.fn();
    component.save.subscribe(saved);

    await component.onSave();

    expect(component.formData()['Address']).toBeNull();
    expect(saved).toHaveBeenCalledOnce();
  });

  it('keeps a blank required object, so its fields\' rules stop the save', async () => {
    const { fixture, component } = createComponent();
    await render(fixture, { Address: {} }, { isRequired: true });
    const saved = vi.fn();
    component.save.subscribe(saved);

    await component.onSave();

    expect(component.formData()['Address']).toEqual({});
    expect(saved).not.toHaveBeenCalled();
  });

  it('a required object that is null fails the attribute\'s own required rule', async () => {
    const { fixture, component } = createComponent();
    await render(fixture, { Address: null }, { isRequired: true });

    expect(component.hasError('Address')).toBe(true);
    expect(fixture.nativeElement.querySelector('.spark-inline-detail.is-invalid')).not.toBeNull();
  });

  it('hands the server\'s errors on the object\'s fields to the embedded form, named as it knows them', async () => {
    const { fixture, component } = createComponent();
    fixture.componentRef.setInput('validationErrors', [
      { attributeName: 'Address.Street', errorMessage: translated({ en: 'Unknown street' }), ruleType: 'error' },
      { attributeName: 'Name', errorMessage: translated({ en: 'Taken' }), ruleType: 'error' },
    ]);
    await render(fixture, { Address: { Street: 'Nowhere 0' } });

    const [embedded] = embeddedForms(fixture, component);
    expect(embedded.validationErrors().map(e => e.attributeName)).toEqual(['Street']);
    expect(fixture.nativeElement.textContent).toContain('Unknown street');
  });

  it('without editMode a single object keeps the summary box and modal', async () => {
    const { fixture, component } = createComponent();
    await render(fixture, { Address: { Street: 'Main 1' } }, { editMode: undefined });

    expect(embeddedForms(fixture, component)).toHaveLength(0);
    expect(fixture.nativeElement.querySelector('input#Address[readonly]')).not.toBeNull();
  });

  it('a renderer registered for a single AsDetail attribute is used, and its valueChange writes the object', async () => {
    @Component({ selector: 'spec-address-editor', standalone: true, template: '' })
    class AddressEditor {
      value = input<any>();
      valueChange = input<(value: any) => void>();
    }
    const { fixture, component } = createComponent([{ name: 'address-typeahead', editComponent: AddressEditor, columnComponent: null }]);
    await render(fixture, { Address: { Street: 'Main 1' } }, { editMode: undefined, renderer: 'address-typeahead' });

    const editor = fixture.debugElement.query(By.directive(AddressEditor));
    expect(editor).not.toBeNull();
    expect(editor.componentInstance.value()).toEqual({ Street: 'Main 1' });
    expect(fixture.nativeElement.querySelector('input#Address[readonly]')).toBeNull();

    editor.componentInstance.valueChange()({ Street: 'Picked 2', City: 'Brugge' });
    expect(component.formData()['Address']).toEqual({ Street: 'Picked 2', City: 'Brugge' });
  });
});
