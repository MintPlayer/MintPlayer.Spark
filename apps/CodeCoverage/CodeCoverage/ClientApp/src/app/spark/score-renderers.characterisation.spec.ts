import { provideZonelessChangeDetection, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { PersistentObject, QueryResultItem } from '@mintplayer/ng-spark/models';
import { createBrowseStub, provideBrowseStub, settle, StubSparkline, stubSparkTrendCharts } from '../../testing/test-utils';
import { CoverageBarComponent } from '../components/coverage-bar/coverage-bar.component';
import { clearSparklineCacheForTesting } from './coverage-sparkline-renderer.component';
import { codeCoverageRenderers } from './renderers';

/**
 * Characterisation of what production draws for the coverage renderers (#268 X5).
 *
 * Written against the app's own components BEFORE they were moved onto ng-spark's score renderers,
 * then kept unchanged across that switch: each case renders the component the app registers for a
 * model `renderer` name, exactly as the grid and the detail page resolve it, and compares the DOM
 * that reaches the screen. Component host elements (`app-*`, `spark-*`) are transparent here, being
 * the one thing a refactor may legitimately add or remove; every other element, its classes, inline
 * style, aria attributes and text must stay identical.
 */

function po(attributes: Record<string, unknown>): PersistentObject {
  return {
    attributes: Object.entries(attributes).map(([name, value]) => ({ name, value })),
  } as unknown as PersistentObject;
}

function row(id: string): QueryResultItem {
  return { id, values: [] } as unknown as QueryResultItem;
}

const IGNORED_ATTRIBUTE = /^(_ng|ng-reflect-|ng-version$)/;

/** The DOM as the user sees it: transparent component hosts, sorted classes, normalised style. */
function signature(node: Node): string {
  if (node.nodeType === Node.TEXT_NODE) return (node.textContent ?? '').replace(/\s+/g, ' ').trim();
  if (node.nodeType !== Node.ELEMENT_NODE) return '';
  const element = node as HTMLElement;
  const children = Array.from(element.childNodes).map(signature).filter(s => s.length > 0).join('');
  const tag = element.tagName.toLowerCase();
  if (tag.startsWith('app-') || tag.startsWith('spark-')) return children;

  const attributes = Array.from(element.attributes)
    .filter(a => !IGNORED_ATTRIBUTE.test(a.name))
    .map(a => {
      if (a.name === 'class') return `class="${[...element.classList].sort().join(' ')}"`;
      if (a.name === 'style') return `style="${element.style.cssText}"`;
      return `${a.name}="${a.value}"`;
    })
    .sort();
  return `<${[tag, ...attributes].join(' ')}>${children}</${tag}>`;
}

function rendered(fixture: ComponentFixture<unknown>): string {
  // The fixture's own host element (a `div#rootN`) is TestBed's, not the renderer's.
  return Array.from((fixture.nativeElement as HTMLElement).childNodes).map(signature).join('');
}

function registration(name: string) {
  const found = codeCoverageRenderers.find(r => r.name === name);
  if (!found) throw new Error(`No renderer registered as '${name}'`);
  return found;
}

function render(component: Type<unknown>, inputs: Record<string, unknown>): ComponentFixture<unknown> {
  const fixture = TestBed.createComponent(component);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.detectChanges();
  return fixture;
}

const DASH_SMALL = '<span class="small text-muted">—</span>';
const DASH = '<span class="text-muted">—</span>';

/** The compact bar as production draws it at `percent`, coloured `color`. */
function bar(percent: string, now: string, width: string, color: string): string {
  return '<div class="align-items-center d-flex gap-2">'
    + '<bs-progress class="d-block overflow-hidden" style="width: 80px; height: 8px;"><div class="progress">'
    + `<bs-progress-bar aria-label="Coverage" aria-valuemax="100" aria-valuemin="0" aria-valuenow="${now}" aria-valuetext="${percent}%" class="${color} progress-bar" role="progressbar" style="width: ${width};"></bs-progress-bar>`
    + '</div></bs-progress>'
    + `<span class="small text-nowrap">${percent}%</span></div>`;
}

describe('coverage renderers, characterised (#268 X5)', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] }).compileComponents();
  });

  describe("'coverage-bar' column", () => {
    const column = () => registration('coverage-bar').columnComponent!;

    const cases: [string, unknown, string][] = [
      ['null', null, DASH_SMALL],
      ['undefined', undefined, DASH_SMALL],
      ['nothing coverable and no files', po({ LinesCoverable: 0, FilesCount: 0 }), DASH_SMALL],
      // A summary exists (one file) but 0/0 is no percentage: still the dash, not a 0% bar.
      ['files but nothing coverable', po({ LinesCovered: 0, LinesCoverable: 0, FilesCount: 1 }), DASH_SMALL],
      ['0%', po({ LinesCovered: 0, LinesCoverable: 100, FilesCount: 1 }), bar('0.0', '0', '0%', 'bg-danger')],
      // Shown as 60.0% but still below the 60 threshold: the colour uses the unrounded value.
      ['59.95%', po({ LinesCovered: 1199, LinesCoverable: 2000, FilesCount: 1 }), bar('60.0', '59.95', '59.95%', 'bg-danger')],
      ['60% edge', po({ LinesCovered: 60, LinesCoverable: 100, FilesCount: 1 }), bar('60.0', '60', '60%', 'bg-warning')],
      ['79.99%', po({ LinesCovered: 7999, LinesCoverable: 10000, FilesCount: 1 }), bar('80.0', '79.99000000000001', '79.99000000000001%', 'bg-warning')],
      ['80% edge', po({ LinesCovered: 80, LinesCoverable: 100, FilesCount: 1 }), bar('80.0', '80', '80%', 'bg-success')],
      ['100%', po({ LinesCovered: 100, LinesCoverable: 100, FilesCount: 1 }), bar('100.0', '100', '100%', 'bg-success')],
      ['flat /api dict', { linesCovered: 3, linesCoverable: 4, branchesCovered: 0, branchesTotal: 0, filesCount: 1 }, bar('75.0', '75', '75%', 'bg-warning')],
    ];

    it.each(cases)('%s', (_label, value, expected) => {
      expect(rendered(render(column(), { value }))).toBe(expected);
    });
  });

  describe("'coverage-bar' detail", () => {
    const detail = () => registration('coverage-bar').detailComponent!;

    it.each([null, undefined, { unrelated: true }, po({ LinesCoverable: 0, FilesCount: 0 })])('no summary (%#) draws only the dash', (value) => {
      expect(rendered(render(detail(), { value }))).toBe(DASH);
    });

    it('draws the bar plus the counts', () => {
      const value = po({ LinesCovered: 50, LinesCoverable: 200, BranchesCovered: 3, BranchesTotal: 8, FilesCount: 12 });

      expect(rendered(render(detail(), { value }))).toBe(
        `<div>${bar('25.0', '25', '25%', 'bg-danger')}<div class="small text-muted">50/200 lines · 3/8 branches · 12 files</div></div>`);
    });

    it('draws a dash bar with counts for files but nothing coverable', () => {
      const value = po({ LinesCovered: 0, LinesCoverable: 0, BranchesCovered: 0, BranchesTotal: 0, FilesCount: 2 });

      expect(rendered(render(detail(), { value }))).toBe(
        `<div>${DASH_SMALL}<div class="small text-muted">0/0 lines · 0/0 branches · 2 files</div></div>`);
    });
  });

  // The bar the commit page's per-file table draws directly, outside any renderer.
  describe('app-coverage-bar', () => {
    it.each([
      [null, DASH_SMALL],
      [{ linesCovered: 0, linesCoverable: 0, branchesCovered: 0, branchesTotal: 0, filesCount: 0 }, DASH_SMALL],
      [{ linesCovered: 2, linesCoverable: 3, branchesCovered: 0, branchesTotal: 0, filesCount: 0 }, bar('66.7', '66.66666666666666', '66.66666666666666%', 'bg-warning')],
    ])('summary %#', (summary, expected) => {
      expect(rendered(render(CoverageBarComponent, { summary }))).toBe(expected);
    });
  });

  describe("'coverage-delta'", () => {
    const span = (text: string, tone: string) => `<span class="small ${tone}">${text}</span>`;

    const cases: [string, unknown, string][] = [
      ['null', null, span('—', 'text-muted')],
      ['undefined', undefined, span('—', 'text-muted')],
      ['empty string', '', span('—', 'text-muted')],
      ['non-number', 'n/a', span('—', 'text-muted')],
      ['zero', 0, span('0.0', 'text-muted')],
      ['a rise', 2.345, span('+2.3', 'text-success')],
      ['a drop', -1, span('-1.0', 'text-danger')],
      // Rounds to zero but keeps its direction: the sign and colour come from the raw value.
      ['a tiny rise', 0.04, span('+0.0', 'text-success')],
      ['a tiny drop', -0.04, span('-0.0', 'text-danger')],
      ['a numeric string', '1.5', span('+1.5', 'text-success')],
      ['100 points', 100, span('+100.0', 'text-success')],
    ];

    it.each(cases)('column: %s', (_label, value, expected) => {
      expect(rendered(render(registration('coverage-delta').columnComponent!, { value }))).toBe(expected);
    });

    it.each(cases)('detail: %s', (_label, value, expected) => {
      expect(rendered(render(registration('coverage-delta').detailComponent!, { value }))).toBe(expected);
    });
  });

  describe("'coverage-sparkline'", () => {
    // The real sparkline hosts a Lit element jsdom cannot; the stub has its selector and inputs.
    // The one line that names an implementation: the component that imports BsSparklineComponent
    // was CoverageSparklineRendererComponent until #268 X5 and is ng-spark's SparkTrendSparklineComponent since.
    function configure(batch: Record<string, number[]>): void {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [provideZonelessChangeDetection(), provideBrowseStub(createBrowseStub({ getSparklines: () => Promise.resolve(batch) }))],
      });
      stubSparkTrendCharts();
    }

    async function draw(component: Type<unknown>, value: unknown): Promise<{ dom: string; stub: StubSparkline | undefined }> {
      const fixture = TestBed.createComponent(component);
      fixture.componentRef.setInput('value', value);
      fixture.componentRef.setInput('item', row('Repositories/github/1'));
      await settle(fixture);
      const stub = fixture.debugElement.query(By.directive(StubSparkline))?.componentInstance as StubSparkline | undefined;
      return { dom: rendered(fixture), stub };
    }

    beforeEach(() => clearSparklineCacheForTesting());
    afterEach(() => clearSparklineCacheForTesting());

    for (const slot of ['columnComponent', 'detailComponent'] as const) {
      it(`${slot}: a series draws the 90x24 sparkline on a fixed 0-100 scale`, async () => {
        configure({ 'acme/widgets': [10, 20.5, 30] });

        const { dom, stub } = await draw(registration('coverage-sparkline')[slot]!, 'acme/widgets');

        expect(dom).toBe('<bs-sparkline class="d-inline-block" style="width: 90px; height: 24px;"></bs-sparkline>');
        expect(stub!.points()).toEqual([10, 20.5, 30]);
        expect(stub!.yMin()).toBe(0);
        expect(stub!.yMax()).toBe(100);
        expect(stub!.inputLabel()).toBe('Coverage trend for acme/widgets');
      });

      it(`${slot}: an empty series, a single point or a missing repository draws the dash`, async () => {
        configure({ 'acme/empty': [], 'acme/one': [42] });

        for (const value of ['acme/empty', 'acme/one', 'acme/missing', null]) {
          const { dom, stub } = await draw(registration('coverage-sparkline')[slot]!, value);
          expect(dom).toBe(DASH_SMALL);
          expect(stub).toBeUndefined();
        }
      });
    }
  });
});
