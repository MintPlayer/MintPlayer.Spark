import { Component, input } from '@angular/core';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, beforeEach } from 'vitest';

import { SparkGridCellComponent } from './spark-grid-cell.component';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityAttributeDefinition } from '@mintplayer/ng-spark/models';

/**
 * The cell is shared by the query grid and the AsDetail table on the PO detail page.
 *
 * Three of these pin behaviour the AsDetail copy did NOT have before it was extracted: a boolean
 * rendered as the text "true", a colour rendered as its hex string, and a custom renderer was
 * dispatched by a second hand-copied lookup. The same column therefore said different things
 * depending on which table it appeared in.
 */

const col = (over: Partial<EntityAttributeDefinition>): EntityAttributeDefinition => ({
  id: 'c1', name: 'Col', dataType: 'string', isVisible: true, isReadOnly: false,
  isRequired: false, isArray: false, order: 1,
  ...over,
} as any);

@Component({ selector: 'spec-renderer', standalone: true, template: '<i>R:{{ value() }}</i>' })
class SpecRenderer {
  value = input<any>();
}

function setup(inputs: Record<string, unknown>, renderers: unknown[] = []) {
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: renderers },
    ],
  });
  const fixture: ComponentFixture<SparkGridCellComponent> = TestBed.createComponent(SparkGridCellComponent);
  for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
  fixture.detectChanges();
  return fixture;
}

const html = (f: ComponentFixture<unknown>) => f.nativeElement as HTMLElement;

describe('SparkGridCellComponent', () => {
  beforeEach(() => TestBed.resetTestingModule());

  describe('boolean', () => {
    it('renders a checkbox, not the word "true"', () => {
      const f = setup({ column: col({ dataType: 'boolean' }), display: true });

      const box = html(f).querySelector('input[type=checkbox]') as HTMLInputElement;
      expect(box).toBeTruthy();
      expect(box.checked).toBe(true);
      expect(html(f).textContent).not.toContain('true');
    });

    /**
     * Without `[indeterminate]` an unset boolean renders as an unchecked box, which reads as an
     * explicit `false` — a different statement about the data.
     */
    it('shows a null boolean as indeterminate rather than unchecked', () => {
      const f = setup({ column: col({ dataType: 'boolean' }), display: null });

      const box = html(f).querySelector('input[type=checkbox]') as HTMLInputElement;
      expect(box.indeterminate).toBe(true);
      expect(box.checked).toBe(false);
    });

    it('shows an explicit false as unchecked and determinate', () => {
      const f = setup({ column: col({ dataType: 'boolean' }), display: false });

      const box = html(f).querySelector('input[type=checkbox]') as HTMLInputElement;
      expect(box.indeterminate).toBe(false);
      expect(box.checked).toBe(false);
    });
  });

  describe('color', () => {
    it('renders a swatch, not the hex string', () => {
      const f = setup({ column: col({ dataType: 'color' }), display: '#ff0000' });

      const swatch = html(f).querySelector('span[style*="background-color"]');
      expect(swatch).toBeTruthy();
      expect(html(f).textContent?.trim()).toBe('');
    });

    it('renders nothing for an unset colour', () => {
      const f = setup({ column: col({ dataType: 'color' }), display: null });

      expect(html(f).querySelector('span[style*="background-color"]')).toBeNull();
    });
  });

  describe('image (#327 §9.1)', () => {
    it('renders an <img>, not the URL as text', () => {
      const f = setup({ column: col({ dataType: 'image' }), display: 'https://cdn.example.com/a.png' });

      const img = html(f).querySelector('img') as HTMLImageElement;
      expect(img).toBeTruthy();
      expect(img.getAttribute('src')).toBe('https://cdn.example.com/a.png');
      expect(html(f).textContent?.trim()).toBe('');
    });

    it('sizes itself inline, because the grid cell lives in a shadow root', () => {
      // In a query grid this renders inside <mp-datatable>'s shadow root, which sees neither this
      // component's scoped rules nor Bootstrap's. A class-based height would silently do nothing
      // and a full-size image would blow the row height out.
      const f = setup({ column: col({ dataType: 'image' }), display: 'https://cdn.example.com/a.png' });

      const img = html(f).querySelector('img') as HTMLImageElement;
      expect(img.getAttribute('style')).toContain('max-height');
    });

    it('renders nothing when the value is empty', () => {
      const f = setup({ column: col({ dataType: 'image' }), display: null });

      expect(html(f).querySelector('img')).toBeNull();
    });
  });

  describe('url (#327 §9.1)', () => {
    it('renders an anchor to the value', () => {
      const f = setup({ column: col({ dataType: 'url' }), display: 'https://example.com/docs' });

      const a = html(f).querySelector('a') as HTMLAnchorElement;
      expect(a).toBeTruthy();
      expect(a.getAttribute('href')).toBe('https://example.com/docs');
      expect(a.textContent?.trim()).toBe('https://example.com/docs');
    });

    it('opens in a new tab WITHOUT handing it a window.opener handle', () => {
      // rel is not optional next to target=_blank: without noopener the opened page can reach back
      // through window.opener, and these hrefs are data the application did not author.
      const f = setup({ column: col({ dataType: 'url' }), display: 'https://example.com' });

      const a = html(f).querySelector('a') as HTMLAnchorElement;
      expect(a.getAttribute('target')).toBe('_blank');
      expect(a.getAttribute('rel')).toContain('noopener');
    });

    it('renders no anchor when the value is empty', () => {
      const f = setup({ column: col({ dataType: 'url' }), display: '' });

      expect(html(f).querySelector('a')).toBeNull();
    });

    it('is a plain href, not a routerLink', () => {
      // The reference-link branch below produces a routerLink; a url column is an EXTERNAL address
      // and must not be routed into the app.
      const f = setup({ column: col({ dataType: 'url' }), display: 'https://example.com' });

      const a = html(f).querySelector('a') as HTMLAnchorElement;
      expect(a.getAttribute('href')).toBe('https://example.com');
    });
  });

  describe('custom renderer', () => {
    it('takes precedence over every built-in branch', () => {
      const f = setup(
        { column: col({ dataType: 'boolean', renderer: 'spec' }), display: true, rendererValue: 42 },
        [{ name: 'spec', columnComponent: SpecRenderer }],
      );

      expect(html(f).textContent).toContain('R:42');
      expect(html(f).querySelector('input[type=checkbox]')).toBeNull();
    });

    /**
     * `NgComponentOutlet` throws on an input the target does not declare, which is what lets every
     * member of the renderer contract be optional. The filtering lives in one place now.
     */
    it('passes only the inputs the renderer declares', () => {
      const act = () => setup(
        { column: col({ renderer: 'spec', rendererOptions: { a: 1 } }), display: 'x', rendererValue: 'x', item: {} },
        [{ name: 'spec', columnComponent: SpecRenderer }],
      );

      expect(act).not.toThrow();
    });
  });

  describe('reference chips', () => {
    it('renders one badge per chip', () => {
      const f = setup({
        column: col({ dataType: 'Reference', isArray: true }),
        chips: [{ id: '1', label: 'Alpha' }, { id: '2', label: 'Beta' }],
      });

      const badges = [...html(f).querySelectorAll('.spark-chip')].map(b => b.textContent?.trim());
      expect(badges).toEqual(['Alpha', 'Beta']);
    });

    /**
     * An AsDetail row's `__sparkBreadcrumbs` is keyed by column, not by id, so it cannot label the
     * members of an array. That caller passes no chips and gets text — better than a row of ids.
     */
    it('falls through to text when no chips are supplied', () => {
      const f = setup({ column: col({ dataType: 'Reference', isArray: true }), display: 'a, b' });

      expect(html(f).querySelector('.spark-chip')).toBeNull();
      expect(html(f).textContent).toContain('a, b');
    });
  });

  describe('reference link', () => {
    it('wraps the text in an anchor when a route is supplied', () => {
      const f = setup({ column: col({ dataType: 'Reference' }), display: 'Acme', link: ['/po', 'company', 'c1'] });

      const a = html(f).querySelector('a');
      expect(a?.textContent?.trim()).toBe('Acme');
      expect(a?.getAttribute('href')).toBe('/po/company/c1');
    });

    /**
     * The grid's first-column link is a different rule and wraps this component from outside, so
     * it leaves `link` unset. Emitting one here too would nest anchors — invalid HTML.
     */
    it('emits no anchor when no route is supplied', () => {
      const f = setup({ column: col({ dataType: 'Reference' }), display: 'Acme' });

      expect(html(f).querySelector('a')).toBeNull();
      expect(html(f).textContent).toContain('Acme');
    });
  });

  it('renders plain text for an ordinary column', () => {
    const f = setup({ column: col({}), display: 'hello' });

    expect(html(f).textContent?.trim()).toBe('hello');
  });
});
