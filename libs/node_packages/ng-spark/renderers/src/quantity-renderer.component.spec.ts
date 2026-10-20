import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { SparkQuantityEditorComponent } from './quantity-editor.component';
import { SparkQuantityRendererComponent } from './quantity-renderer.component';
import { SPARK_ATTRIBUTE_RENDERERS } from './spark-attribute-renderer-registry';

/**
 * The core `quantity` renderer (warehouse WX8): available without registration, a localized number and
 * unit (or a packaging breakdown), and an editor that emits the one wire shape in the unit picked.
 */
describe('quantity renderer', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('is a core renderer: display, cell, edit and filter label, with no app registration', () => {
    const registration = TestBed.inject(SPARK_ATTRIBUTE_RENDERERS).find(r => r.name === 'quantity');

    expect(registration?.detailComponent).toBe(SparkQuantityRendererComponent);
    expect(registration?.columnComponent).toBe(SparkQuantityRendererComponent);
    expect(registration?.editComponent).toBe(SparkQuantityEditorComponent);
    expect(registration?.filterLabel?.({ Amount: 12, Unit: 'EA' })).toContain('EA');
  });

  describe('display', () => {
    function render(value: unknown, options?: Record<string, any>) {
      const fixture = TestBed.createComponent(SparkQuantityRendererComponent);
      fixture.componentRef.setInput('value', value);
      if (options !== undefined) fixture.componentRef.setInput('options', options);
      fixture.detectChanges();
      return (fixture.nativeElement as HTMLElement).textContent?.trim();
    }

    it('formats the value in the pinned locale', () => {
      expect(render({ Amount: 1234.5, Unit: 'EA' }, { locale: 'en-US' })).toBe('1,234.5 EA');
    });

    it('shows whole packages when the levels are configured', () => {
      expect(render({ Amount: 27, Unit: 'EA' }, { locale: 'en-US', baseUnit: 'EA', levels: [{ unit: 'CS', baseQuantity: 12 }] }))
        .toBe('2 CS + 3 EA');
    });

    it('renders nothing for no value', () => {
      expect(render(null)).toBe('');
    });
  });

  describe('editor', () => {
    function setup(value: unknown, options?: Record<string, any>) {
      const emitted: unknown[] = [];
      const fixture = TestBed.createComponent(SparkQuantityEditorComponent);
      fixture.componentRef.setInput('value', value);
      if (options !== undefined) fixture.componentRef.setInput('options', options);
      fixture.componentRef.setInput('valueChange', (v: unknown) => emitted.push(v));
      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;
      const amount = host.querySelector('input') as HTMLInputElement;
      const type = (text: string) => {
        amount.value = text;
        amount.dispatchEvent(new Event('input'));
        fixture.detectChanges();
      };
      return { fixture, host, amount, emitted, type };
    }

    it('shows the current amount', () => {
      const { amount } = setup({ Amount: 12.5, Unit: 'KG' }, { units: ['KG'] });
      expect(amount.value).toBe('12.5');
    });

    it('emits the wire shape in the starting unit when an amount is typed', () => {
      const { emitted, type } = setup(null, { units: ['CS', 'EA'] });
      type('3');
      expect(emitted).toEqual([{ Amount: 3, Unit: 'CS' }]);
    });

    it('a cleared amount clears the value', () => {
      const { emitted, type } = setup({ Amount: 5, Unit: 'EA' }, { units: ['EA'] });
      type('');
      expect(emitted).toEqual([null]);
    });

    it('a single offered unit is a fixed label, not a picker', () => {
      const { host } = setup(null, { units: ['EA'] });
      expect(host.querySelector('select')).toBeNull();
      expect(host.querySelector('.input-group-text')?.textContent?.trim()).toBe('EA');
    });

    it('offers the base unit and every packaging level when no units are listed', () => {
      const { host } = setup(null, { baseUnit: 'EA', levels: [{ unit: 'CS', baseQuantity: 12 }, { unit: 'PAL', baseQuantity: 480 }] });
      const codes = [...host.querySelectorAll('option')].map(o => (o as HTMLOptionElement).value);
      expect(codes).toEqual(['EA', 'CS', 'PAL']);
    });
  });
});
