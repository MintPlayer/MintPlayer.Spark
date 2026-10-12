import { Component, input, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { EntityType, PersistentObject, ShowedOn } from '@mintplayer/ng-spark/models';
import { SPARK_DETAIL_LAYOUTS, SparkDetailContext } from '@mintplayer/ng-spark/panels';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import {
  formatSparkBytes,
  formatSparkDashboardValue,
  isSparkDashboard,
  provideSparkDashboards,
  SparkDashboardComponent,
  sparkDashboardRenderers,
  SparkDashboardTableComponent,
  SparkMeterComponent,
  SparkStatComponent,
  sparkThresholdColor,
  toSparkTableRows,
} from '..';

const translations: Record<string, string> = {
  'dashboards.collection': 'Collection',
  'dashboards.updated': 'Updated',
  'dashboards.empty': 'None',
  'status.Completed': 'Done',
};
const language = { t: (key: string, fallback?: string) => translations[key] ?? fallback ?? key };

function render<T>(type: Type<T>, value: unknown, options?: Record<string, unknown>): ComponentFixture<T> {
  const fixture = TestBed.createComponent(type);
  fixture.componentRef.setInput('value', value);
  if (options !== undefined) fixture.componentRef.setInput('options', options);
  fixture.detectChanges();
  return fixture;
}

const host = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;
const text = (fixture: ComponentFixture<unknown>) => host(fixture).textContent!.replace(/\s+/g, ' ').trim();

beforeEach(() => {
  TestBed.configureTestingModule({
    providers: [{ provide: SparkLanguageService, useValue: language }],
  });
});

describe('dashboard formats', () => {
  it('writes bytes in binary units with one decimal from KB up', () => {
    expect(formatSparkBytes(0)).toBe('0 B');
    expect(formatSparkBytes(1536)).toBe('1.5 KB');
    expect(formatSparkBytes(150 * 1024 ** 3)).toBe('150.0 GB');
    expect(formatSparkBytes(null)).toBe('—');
  });

  it('writes numbers, percentages and no value', () => {
    expect(formatSparkDashboardValue(12345, 'number')).toBe('12,345');
    expect(formatSparkDashboardValue('7', undefined)).toBe('7');
    expect(formatSparkDashboardValue(42.25, 'percent')).toBe('42.3%');
    expect(formatSparkDashboardValue(null, 'bytes')).toBe('—');
    expect(formatSparkDashboardValue('Acme', undefined)).toBe('Acme');
  });

  it('colours past a threshold in either direction', () => {
    expect(sparkThresholdColor(50, { warning: 75, danger: 90 })).toBeNull();
    expect(sparkThresholdColor(80, { warning: 75, danger: 90 })).toBe('warning');
    expect(sparkThresholdColor(95, { warning: 75, danger: 90 })).toBe('danger');
    expect(sparkThresholdColor(5, { warning: 20, danger: 10, direction: 'below' })).toBe('danger');
    expect(sparkThresholdColor(null, { danger: 1 })).toBeNull();
  });
});

describe('spark-stat', () => {
  it('shows the figure, formatted', () => {
    expect(text(render(SparkStatComponent, 2048, { format: 'bytes' }))).toBe('2.0 KB');
    expect(text(render(SparkStatComponent, 3, { suffix: ' sites' }))).toBe('3 sites');
  });

  it('turns red past the danger threshold and shows a dash for no value', () => {
    const red = render(SparkStatComponent, 95, { warning: 75, danger: 90 });
    expect(host(red).querySelector('[data-threshold]')?.getAttribute('data-threshold')).toBe('danger');
    expect(host(red).querySelector('.text-danger')).not.toBeNull();
    expect(text(render(SparkStatComponent, null))).toBe('—');
  });
});

describe('spark-meter', () => {
  it('shows used of total and the share, coloured by it', () => {
    const fixture = render(SparkMeterComponent, { value: 40 * 1024 ** 3, max: 160 * 1024 ** 3 }, { format: 'bytes' });
    expect(host(fixture).querySelector('[data-meter-text]')?.textContent?.trim()).toBe('40.0 GB / 160.0 GB (25%)');
  });

  it('reads a plain number against the max option, and needs a ceiling', () => {
    expect(text(render(SparkMeterComponent, 95, { max: 100 }))).toContain('(95%)');
    expect(text(render(SparkMeterComponent, { value: 5, max: null }))).toBe('—');
    expect(text(render(SparkMeterComponent, undefined))).toBe('—');
  });
});

describe('spark-dashboard-table', () => {
  it('reads rows, a dictionary or a JSON text', () => {
    expect(toSparkTableRows([{ a: 1 }, null, 3])).toEqual([{ a: 1 }]);
    expect(toSparkTableRows({ Completed: 3 })).toEqual([{ key: 'Completed', value: 3 }]);
    expect(toSparkTableRows('[{"a":1}]')).toEqual([{ a: 1 }]);
    expect(toSparkTableRows(42)).toEqual([]);
  });

  it('draws the declared columns with translated headers and formatted cells', () => {
    const fixture = render(SparkDashboardTableComponent, [{ name: 'Sites', documents: 1200, sizeBytes: 2048 }], {
      columns: [
        { key: 'name', label: 'dashboards.collection' },
        { key: 'documents', label: 'Documents', format: 'number' },
        { key: 'sizeBytes', format: 'bytes' },
      ],
    });
    const headers = [...host(fixture).querySelectorAll('th')].map(th => th.textContent!.trim());
    const cells = [...host(fixture).querySelectorAll('td')].map(td => td.textContent!.trim());
    expect(headers).toEqual(['Collection', 'Documents', 'sizeBytes']);
    expect(cells).toEqual(['Sites', '1,200', '2.0 KB']);
    expect(host(fixture).querySelectorAll('td.text-end').length).toBe(2);
  });

  it('translates a cell through its prefix and says when there are no rows', () => {
    const fixture = render(SparkDashboardTableComponent, { Completed: 3, Failed: 1 }, {
      columns: [{ key: 'key', translate: 'status.' }, { key: 'value', format: 'number' }],
    });
    expect([...host(fixture).querySelectorAll('td')].map(td => td.textContent!.trim())).toEqual(['Done', '3', 'Failed', '1']);
    expect(text(render(SparkDashboardTableComponent, []))).toBe('None');
  });
});

@Component({ selector: 'test-renderer', template: '<span class="test-renderer">{{ value() }}</span>' })
class TestRenderer {
  readonly value = input<unknown>();
}

describe('spark-dashboard (tile layout)', () => {
  const entityType: EntityType = {
    id: 't-dash', name: 'OpsDashboard', live: true,
    groups: [{ id: 'g-db', name: 'Database', label: { en: 'Database' }, order: 2 }],
    attributes: [
      { id: 'a1', name: 'Users', label: { en: 'Users' }, dataType: 'number', order: 1, showedOn: ShowedOn.PersistentObject, renderer: 'test' },
      { id: 'a2', name: 'Size', label: { en: 'Size' }, dataType: 'number', order: 2, group: 'g-db' },
      { id: 'a3', name: 'Collections', dataType: 'string', order: 3, group: 'g-db', renderer: 'spark-dashboard-table' },
      { id: 'a4', name: 'Hidden', dataType: 'number', order: 4 },
      { id: 'a5', name: 'QueryOnly', dataType: 'number', order: 5, showedOn: ShowedOn.Query },
    ],
  } as unknown as EntityType;

  const item: PersistentObject = {
    id: 'main', name: 'OpsDashboard', objectTypeId: 't-dash',
    metadata: { dashboard: { generatedAtUtc: '2026-10-11T12:00:00Z' } },
    attributes: [
      { id: 'a1', name: 'Users', value: 7 },
      { id: 'a2', name: 'Size', value: 1234 },
      { id: 'a3', name: 'Collections', value: [] },
      { id: 'a5', name: 'QueryOnly', value: 1, showedOn: ShowedOn.Query },
    ],
  } as unknown as PersistentObject;

  function renderLayout(): ComponentFixture<SparkDashboardComponent> {
    TestBed.overrideProvider(SPARK_ATTRIBUTE_RENDERERS, { useValue: [{ name: 'test', detailComponent: TestRenderer }, ...sparkDashboardRenderers] });
    const fixture = TestBed.createComponent(SparkDashboardComponent);
    const context: SparkDetailContext = { type: 'ops', id: 'main', item, entityType, permissions: null, deleted: null, reload: async () => {} };
    fixture.componentRef.setInput('context', context);
    fixture.detectChanges();
    return fixture;
  }

  it('draws one tile per attribute on the object, ungrouped first then by group, with its renderer or its value', () => {
    const el = host(renderLayout());
    const tiles = [...el.querySelectorAll('[data-tile]')].map(t => t.getAttribute('data-tile'));
    expect(tiles).toEqual(['Users', 'Size', 'Collections']);
    expect(el.querySelector('[data-tile="Users"] .test-renderer')?.textContent).toBe('7');
    expect(el.querySelector('[data-tile="Size"]')?.textContent).toContain('1,234');
    expect(el.querySelector('[data-section="Database"] h3')?.textContent?.trim()).toBe('Database');
    expect(el.querySelector('[data-tile="Collections"]')?.classList).toContain('spark-dashboard-span-row');
    expect(el.querySelector('[data-generated]')?.textContent).toContain('Updated');
  });

  it('recognises a dashboard by its metadata, and provideSparkDashboards registers the layout for it', () => {
    expect(isSparkDashboard(item)).toBe(true);
    expect(isSparkDashboard({ ...item, metadata: undefined })).toBe(false);
    expect(isSparkDashboard(null)).toBe(false);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideSparkDashboards()] });
    const [layout] = TestBed.inject(SPARK_DETAIL_LAYOUTS);
    expect(layout.component).toBe(SparkDashboardComponent);
    expect(layout.matches(item, entityType)).toBe(true);
    expect(layout.matches({ ...item, metadata: {} }, entityType)).toBe(false);
  });
});
