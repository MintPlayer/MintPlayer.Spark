import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';

import { SparkLookupPickerComponent } from './spark-lookup-picker.component';
import { LookupReferenceValue } from '@mintplayer/ng-spark/models';

const roles: LookupReferenceValue[] = [
  { key: 'admin', values: { en: 'Administrator' } as any, isActive: true },
  { key: 'user', values: { en: 'Standard User' } as any, isActive: true },
];

function createComponent() {
  TestBed.configureTestingModule({ providers: [provideNoopAnimations()] });
  const fixture = TestBed.createComponent(SparkLookupPickerComponent);
  return { fixture, component: fixture.componentInstance };
}

describe('SparkLookupPickerComponent', () => {
  it('displayValue resolves the translated label, falls back to the key, and is empty when unset', () => {
    const { fixture, component } = createComponent();
    fixture.componentRef.setInput('options', roles);

    fixture.componentRef.setInput('value', 'admin');
    expect(component.displayValue()).toBe('Administrator');

    fixture.componentRef.setInput('value', 'unknown');
    expect(component.displayValue()).toBe('unknown');

    fixture.componentRef.setInput('value', null);
    expect(component.displayValue()).toBe('');
  });

  it('filteredItems narrows by search term (case-insensitive, matches key or translation)', () => {
    const { fixture, component } = createComponent();
    fixture.componentRef.setInput('options', roles);

    component.searchTerm.set('admin');
    expect(component.filteredItems().map(i => i.key)).toEqual(['admin']);

    component.searchTerm.set('standard');
    expect(component.filteredItems().map(i => i.key)).toEqual(['user']);

    component.searchTerm.set('nope');
    expect(component.filteredItems()).toHaveLength(0);
  });

  it('select emits the picked key via valueChange and closes the modal', () => {
    const { fixture, component } = createComponent();
    fixture.componentRef.setInput('options', roles);
    component.open();

    const emitted = vi.fn();
    component.valueChange.subscribe(emitted);
    component.select(roles[0]);

    expect(emitted).toHaveBeenCalledWith('admin');
    expect(component.showModal()).toBe(false);
  });
});
