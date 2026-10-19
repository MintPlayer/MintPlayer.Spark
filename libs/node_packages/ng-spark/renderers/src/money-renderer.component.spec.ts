import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { SparkMoneyEditorComponent } from './money-editor.component';
import { SparkMoneyRendererComponent } from './money-renderer.component';
import { SPARK_ATTRIBUTE_RENDERERS } from './spark-attribute-renderer-registry';

/**
 * The core `money` renderer (commerce X4): available without registration, formatted with
 * Intl.NumberFormat, and an editor that emits the one wire shape without rounding.
 */
describe('money renderer', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('is a core renderer: display, cell, edit and filter label, with no app registration', () => {
    const registration = TestBed.inject(SPARK_ATTRIBUTE_RENDERERS).find(r => r.name === 'money');

    expect(registration?.detailComponent).toBe(SparkMoneyRendererComponent);
    expect(registration?.columnComponent).toBe(SparkMoneyRendererComponent);
    expect(registration?.editComponent).toBe(SparkMoneyEditorComponent);
    expect(registration?.filterLabel?.({ Amount: 12.3, Currency: 'EUR' })).toContain('12');
  });

  describe('display', () => {
    function render(value: unknown, options?: Record<string, any>) {
      const fixture = TestBed.createComponent(SparkMoneyRendererComponent);
      fixture.componentRef.setInput('value', value);
      if (options !== undefined) fixture.componentRef.setInput('options', options);
      fixture.detectChanges();
      return (fixture.nativeElement as HTMLElement).textContent?.trim();
    }

    it('formats the value in the pinned locale', () => {
      expect(render({ Amount: 12.3, Currency: 'EUR' }, { locale: 'en-US' })).toBe('€12.30');
    });

    it('renders nothing for no value', () => {
      expect(render(null)).toBe('');
    });
  });

  describe('editor', () => {
    function setup(value: unknown, options?: Record<string, any>) {
      const emitted: unknown[] = [];
      const fixture = TestBed.createComponent(SparkMoneyEditorComponent);
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
      const { amount } = setup({ Amount: 12.5, Currency: 'EUR' }, { currencies: ['EUR'] });
      expect(amount.value).toBe('12.5');
    });

    it('emits the wire shape in the starting currency when an amount is typed', () => {
      const { emitted, type } = setup(null, { currencies: ['USD'] });
      type('19.99');
      expect(emitted).toEqual([{ Amount: 19.99, Currency: 'USD' }]);
    });

    it('a cleared amount clears the value', () => {
      const { emitted, type } = setup({ Amount: 5, Currency: 'EUR' }, { currencies: ['EUR'] });
      type('');
      expect(emitted).toEqual([null]);
    });

    it('a single offered currency is a fixed label, not a picker', () => {
      const { host } = setup(null, { currencies: ['EUR'] });
      expect(host.querySelector('select')).toBeNull();
      expect(host.querySelector('.input-group-text')?.textContent?.trim()).toBe('EUR');
    });

    it('offers a picker when several currencies are configured', () => {
      const { host } = setup({ Amount: 1, Currency: 'EUR' }, { currencies: ['EUR', 'USD'] });
      const codes = [...host.querySelectorAll('option')].map(o => (o as HTMLOptionElement).value);
      expect(codes).toEqual(['EUR', 'USD']);
    });

    it('marks an amount more precise than the currency invalid instead of rounding it', () => {
      const { amount, emitted, type } = setup(null, { currencies: ['EUR'] });
      type('1.234');
      expect(amount.classList.contains('is-invalid')).toBe(true);
      // Sent as typed: the server refuses it with a message, nothing is rounded here.
      expect(emitted).toEqual([{ Amount: 1.234, Currency: 'EUR' }]);
    });
  });
});
