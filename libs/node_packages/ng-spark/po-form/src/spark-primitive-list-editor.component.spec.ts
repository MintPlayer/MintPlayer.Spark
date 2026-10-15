import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { By } from '@angular/platform-browser';
import { CdkDrag, CdkDropList } from '@angular/cdk/drag-drop';
import { describe, expect, it, vi } from 'vitest';

import { SparkPrimitiveListEditorComponent } from './spark-primitive-list-editor.component';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

function createComponent(inputs: Record<string, unknown> = {}) {
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
    ],
  });
  const fixture = TestBed.createComponent(SparkPrimitiveListEditorComponent);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.detectChanges();
  const emitted = vi.fn();
  fixture.componentInstance.valueChange.subscribe(emitted);
  return { fixture, component: fixture.componentInstance, emitted };
}

const inputs = (fixture: any): HTMLInputElement[] =>
  fixture.debugElement.queryAll(By.css('input')).map((d: any) => d.nativeElement);

const buttons = (fixture: any): HTMLButtonElement[] =>
  fixture.debugElement.queryAll(By.css('button')).map((d: any) => d.nativeElement);

describe('SparkPrimitiveListEditorComponent', () => {
  it('renders one input per item, each holding its value', () => {
    const { fixture } = createComponent({ value: ['ci/build', 'lint'] });

    expect(inputs(fixture).map(i => i.value)).toEqual(['ci/build', 'lint']);
  });

  it.each([null, '', undefined])('treats %j as an empty list', value => {
    const { fixture, component } = createComponent({ value });

    expect(component.items()).toEqual([]);
    expect(inputs(fixture)).toHaveLength(0);
  });

  it('gives the first input the attribute id, for the form label', () => {
    const { fixture } = createComponent({ value: ['a', 'b'], inputId: 'Exclusions' });

    expect(inputs(fixture).map(i => i.id)).toEqual(['Exclusions', '']);
  });

  it('add appends an empty item to a new array, leaving the bound one untouched', () => {
    const value = ['a'];
    const { component, emitted } = createComponent({ value });

    component.add();

    expect(emitted).toHaveBeenCalledWith(['a', '']);
    expect(value).toEqual(['a']);
  });

  it('add focuses the new item once it renders', async () => {
    const { fixture, component } = createComponent({ value: ['a'] });
    component.valueChange.subscribe(next => fixture.componentRef.setInput('value', next));

    component.add();
    fixture.detectChanges();
    await fixture.whenStable();
    // The focus waits a macrotask, until the item's input group is in the list (see the component).
    await new Promise(resolve => setTimeout(resolve));

    const all = inputs(fixture);
    expect(all).toHaveLength(2);
    expect(document.activeElement).toBe(all[1]);
  });

  it('add on a number list appends null, not an empty string', () => {
    const { component, emitted } = createComponent({ value: [1], dataType: 'number' });

    component.add();

    expect(emitted).toHaveBeenCalledWith([1, null]);
  });

  it('remove drops the item at that index', () => {
    const { component, emitted } = createComponent({ value: ['a', 'b', 'c'] });

    component.remove(1);

    expect(emitted).toHaveBeenCalledWith(['a', 'c']);
  });

  it('typing into an item emits the list with that item replaced', () => {
    const { fixture, emitted } = createComponent({ value: ['a', 'b'] });

    const second = inputs(fixture)[1];
    second.value = 'beta';
    second.dispatchEvent(new Event('input'));

    expect(emitted).toHaveBeenCalledWith(['a', 'beta']);
  });

  it('a number list emits numbers, and null for a cleared item', () => {
    const { component, emitted } = createComponent({ value: [1, 2], dataType: 'decimal' });

    component.update(0, '2.5');
    expect(emitted).toHaveBeenLastCalledWith([2.5, 2]);

    component.update(1, '');
    expect(emitted).toHaveBeenLastCalledWith([1, null]);
  });

  it('uses the single-value input type of the item data type', () => {
    const { fixture } = createComponent({ value: [3], dataType: 'number' });

    expect(inputs(fixture)[0].type).toBe('number');
  });

  it('offers add and one remove per item when editable', () => {
    const { fixture } = createComponent({ value: ['a', 'b'] });

    // Two trash buttons and the add button.
    expect(buttons(fixture)).toHaveLength(3);
  });

  it('read-only: no inputs, no add or remove buttons, the items shown as text', () => {
    const { fixture } = createComponent({ value: ['a', 'b'], readonly: true });

    expect(inputs(fixture)).toHaveLength(0);
    expect(buttons(fixture)).toHaveLength(0);
    const chips = fixture.debugElement.queryAll(By.css('.spark-chip')).map(d => d.nativeElement.textContent.trim());
    expect(chips).toEqual(['a', 'b']);
  });

  describe('reorder', () => {
    it('is enabled for a sortable attribute: a drag handle per item, and a drop moves the item', () => {
      const { fixture, component, emitted } = createComponent({ value: ['a', 'b', 'c'], sortable: true });

      expect(fixture.debugElement.queryAll(By.css('.spark-drag-handle'))).toHaveLength(3);
      const drags = fixture.debugElement.queryAll(By.directive(CdkDrag)).map(d => d.injector.get(CdkDrag));
      expect(drags.every(d => !d.disabled)).toBe(true);
      expect(fixture.debugElement.query(By.directive(CdkDropList)).injector.get(CdkDropList).disabled).toBe(false);

      component.reorder({ previousIndex: 0, currentIndex: 2 });

      expect(emitted).toHaveBeenCalledWith(['b', 'c', 'a']);
    });

    it('is disabled when the attribute is not sortable: no handle, and a drop does nothing', () => {
      const { fixture, component, emitted } = createComponent({ value: ['a', 'b'] });

      expect(fixture.debugElement.queryAll(By.css('.spark-drag-handle'))).toHaveLength(0);
      expect(fixture.debugElement.query(By.directive(CdkDropList)).injector.get(CdkDropList).disabled).toBe(true);

      component.reorder({ previousIndex: 0, currentIndex: 1 });

      expect(emitted).not.toHaveBeenCalled();
    });

    it('is disabled when read-only, even for a sortable attribute', () => {
      const { fixture, component, emitted } = createComponent({ value: ['a', 'b'], sortable: true, readonly: true });

      expect(component.canReorder()).toBe(false);
      expect(fixture.debugElement.queryAll(By.css('.spark-drag-handle'))).toHaveLength(0);

      component.reorder({ previousIndex: 0, currentIndex: 1 });

      expect(emitted).not.toHaveBeenCalled();
    });

    it('a drop on the same index emits nothing', () => {
      const { component, emitted } = createComponent({ value: ['a', 'b'], sortable: true });

      component.reorder({ previousIndex: 1, currentIndex: 1 });

      expect(emitted).not.toHaveBeenCalled();
    });
  });
});
