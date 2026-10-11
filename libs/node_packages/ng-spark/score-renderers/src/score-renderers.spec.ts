import { Component, input, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { beforeEach, describe, expect, it } from 'vitest';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsSparklineComponent } from '@mintplayer/ng-bootstrap/charts/sparkline';
import { BsTrendChartComponent } from '@mintplayer/ng-bootstrap/charts/trend';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import {
  formatSparkDelta,
  provideSparkScoreRenderers,
  resolveSparkScoreBands,
  SparkDeltaComponent,
  SparkGaugeComponent,
  sparkLetterGrades,
  sparkScoreBand,
  sparkScoreColor,
  SparkThresholdBarComponent,
  sparkTrafficLightBands,
  SparkTrendChartComponent,
  SparkTrendComponent,
  SparkTrendSparklineComponent,
  toSparkScoreNumber,
  toSparkTrendPoints,
} from '..';

// The chart wrappers side-effect-import Lit elements jsdom cannot host; these keep selector and inputs.
@Component({ selector: 'bs-sparkline', template: '' })
class StubSparkline {
  readonly points = input<(number | null)[]>([]);
  readonly yMin = input<number>();
  readonly yMax = input<number>();
  readonly inputLabel = input<string>();
}

@Component({ selector: 'bs-trend-chart', template: '' })
class StubTrendChart {
  readonly series = input<unknown[]>([]);
  readonly yMin = input<number>();
  readonly yMax = input<number>();
  readonly goal = input<number>();
  readonly goalLabel = input<string>();
  readonly inputLabel = input<string>();
}

function render<T>(type: Type<T>, value: unknown, options?: Record<string, unknown>): ComponentFixture<T> {
  const fixture = TestBed.createComponent(type);
  fixture.componentRef.setInput('value', value);
  if (options !== undefined) fixture.componentRef.setInput('options', options);
  fixture.detectChanges();
  return fixture;
}

const host = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;
const text = (fixture: ComponentFixture<unknown>) => host(fixture).textContent!.replace(/\s+/g, ' ').trim();

describe('score bands', () => {
  it('picks the band with the highest min the value reaches, in any order, else the floor', () => {
    const shuffled = [{ color: 'danger' as const }, { min: 80, color: 'success' as const }, { min: 60, color: 'warning' as const }];

    expect(sparkScoreBand(80, shuffled)?.color).toBe('success');
    expect(sparkScoreBand(79.999, shuffled)?.color).toBe('warning');
    expect(sparkScoreBand(60, shuffled)?.color).toBe('warning');
    expect(sparkScoreBand(-5, shuffled)?.color).toBe('danger');
  });

  it('has no band for no value, or below the lowest min without a floor', () => {
    expect(sparkScoreBand(null, sparkTrafficLightBands)).toBeNull();
    expect(sparkScoreBand(NaN, sparkTrafficLightBands)).toBeNull();
    expect(sparkScoreBand(10, [{ min: 50, color: 'success' }])).toBeNull();
  });

  it('grades a 0-100 score A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 50, else F', () => {
    const grade = (n: number) => sparkScoreBand(n, sparkLetterGrades)?.label;

    expect([100, 90, 89.9, 80, 79, 70, 69, 50, 49.9, 0].map(grade)).toEqual(['A', 'A', 'B', 'B', 'C', 'C', 'D', 'D', 'F', 'F']);
  });

  it('resolves presets by name, falls back for anything else', () => {
    expect(resolveSparkScoreBands('letter-grades')).toBe(sparkLetterGrades);
    expect(resolveSparkScoreBands('traffic-light')).toBe(sparkTrafficLightBands);
    expect(resolveSparkScoreBands(undefined)).toBe(sparkTrafficLightBands);
    expect(resolveSparkScoreBands([])).toBe(sparkTrafficLightBands);
    expect(resolveSparkScoreBands('nonsense')).toBe(sparkTrafficLightBands);
  });

  it('maps a band to the ng-bootstrap colour, secondary for no value', () => {
    expect(sparkScoreColor(80)).toBe(Color.success);
    expect(sparkScoreColor(60)).toBe(Color.warning);
    expect(sparkScoreColor(59.95)).toBe(Color.danger);
    expect(sparkScoreColor(null)).toBe(Color.secondary);
  });

  it('reads a cell value as a number, never turning "no value" into 0', () => {
    expect(toSparkScoreNumber(0)).toBe(0);
    expect(toSparkScoreNumber('1.5')).toBe(1.5);
    for (const value of [null, undefined, '', 'n/a', NaN]) expect(toSparkScoreNumber(value)).toBeNull();
  });
});

describe('formatSparkDelta', () => {
  it('renders a dash, not zero, when there is no reference', () => {
    expect(formatSparkDelta(null)).toEqual({ up: false, down: false, text: '—', tone: 'text-muted' });
    expect(formatSparkDelta(undefined).text).toBe('—');
    expect(formatSparkDelta('').text).toBe('—');
    expect(formatSparkDelta(null, { empty: 'n/a' }).text).toBe('n/a');
  });

  it('signs and rounds a change to one decimal by default', () => {
    expect(formatSparkDelta(1.26)).toEqual({ up: true, down: false, text: '+1.3', tone: 'text-success' });
    expect(formatSparkDelta(-0.34)).toEqual({ up: false, down: true, text: '-0.3', tone: 'text-danger' });
  });

  it('treats an exact zero as a real, neutral comparison', () => {
    expect(formatSparkDelta(0)).toEqual({ up: false, down: false, text: '0.0', tone: 'text-muted' });
  });

  it('accepts numeric strings from the grid', () => {
    expect(formatSparkDelta('2.5').text).toBe('+2.5');
  });

  it('honours decimals and suffix', () => {
    expect(formatSparkDelta(3, { decimals: 0, suffix: ' pt' }).text).toBe('+3 pt');
    expect(formatSparkDelta(-1.234, { decimals: 2 }).text).toBe('-1.23');
  });

  it('inverts the tone, never the sign, when lower is better', () => {
    expect(formatSparkDelta(120, { higherIsBetter: false })).toEqual({ up: true, down: false, text: '+120.0', tone: 'text-danger' });
    expect(formatSparkDelta(-5, { higherIsBetter: false }).tone).toBe('text-success');
    expect(formatSparkDelta(0, { higherIsBetter: false }).tone).toBe('text-muted');
  });
});

describe('SparkDeltaComponent', () => {
  it('draws one small span with the tone class', () => {
    const span = host(render(SparkDeltaComponent, -2)).querySelector('span')!;

    expect(span.textContent).toBe('-2.0');
    expect([...span.classList].sort()).toEqual(['small', 'text-danger']);
  });

  it('takes its options from rendererOptions', () => {
    const span = host(render(SparkDeltaComponent, -2, { higherIsBetter: false, suffix: ' ms', decimals: 0 })).querySelector('span')!;

    expect(span.textContent).toBe('-2 ms');
    expect([...span.classList].sort()).toEqual(['small', 'text-success']);
  });
});

describe('SparkThresholdBarComponent', () => {
  const bar = (fixture: ComponentFixture<unknown>) => host(fixture).querySelector('bs-progress-bar') as HTMLElement | null;

  it('draws the bar coloured by band, with the value to one decimal and a % by default', () => {
    const fixture = render(SparkThresholdBarComponent, 59.95);

    expect(text(fixture)).toBe('60.0%');
    expect(bar(fixture)!.classList.contains('bg-danger')).toBe(true);
    expect(bar(fixture)!.getAttribute('aria-valuetext')).toBe('60.0%');
    expect(bar(fixture)!.getAttribute('aria-label')).toBe('Value');
    expect((host(fixture).querySelector('bs-progress') as HTMLElement).style.cssText).toBe('width: 80px; height: 8px;');
  });

  it('draws a muted dash and no bar for no value, but a bar for a real 0', () => {
    for (const value of [null, undefined, '', 'abc']) {
      const fixture = render(SparkThresholdBarComponent, value);
      expect(bar(fixture)).toBeNull();
      expect(text(fixture)).toBe('—');
    }
    expect(bar(render(SparkThresholdBarComponent, 0))).not.toBeNull();
  });

  it('honours scale, bands, label, size, decimals, suffix and showValue', () => {
    const fixture = render(SparkThresholdBarComponent, 7, {
      min: 0, max: 10, bands: [{ min: 5, color: 'info' }, { color: 'dark' }],
      label: 'Rating', width: '120px', height: '4px', decimals: 0, suffix: '/10', showValue: false,
    });

    const b = bar(fixture)!;
    expect(b.classList.contains('bg-info')).toBe(true);
    expect(b.getAttribute('aria-valuemax')).toBe('10');
    expect(b.getAttribute('aria-valuetext')).toBe('7/10');
    expect(b.getAttribute('aria-label')).toBe('Rating');
    expect(b.style.width).toBe('70%');
    expect((host(fixture).querySelector('bs-progress') as HTMLElement).style.cssText).toBe('width: 120px; height: 4px;');
    expect(text(fixture)).toBe('');
  });
});

describe('SparkTrendComponent', () => {
  beforeEach(() => {
    TestBed.overrideComponent(SparkTrendSparklineComponent, { remove: { imports: [BsSparklineComponent] }, add: { imports: [StubSparkline] } });
    TestBed.overrideComponent(SparkTrendChartComponent, { remove: { imports: [BsTrendChartComponent] }, add: { imports: [StubTrendChart] } });
  });

  const sparkline = (fixture: ComponentFixture<unknown>) =>
    fixture.debugElement.query(By.directive(StubSparkline))?.componentInstance as StubSparkline | undefined;
  const chart = (fixture: ComponentFixture<unknown>) =>
    fixture.debugElement.query(By.directive(StubTrendChart))?.componentInstance as StubTrendChart | undefined;

  it('draws a 90x24 sparkline fitted to the data by default', () => {
    const fixture = render(SparkTrendComponent, [1, null, 3]);

    const spark = sparkline(fixture)!;
    expect(spark.points()).toEqual([1, null, 3]);
    expect(spark.yMin()).toBeUndefined();
    expect(spark.yMax()).toBeUndefined();
    const element = host(fixture).querySelector('bs-sparkline') as HTMLElement;
    expect(element.style.cssText).toBe('width: 90px; height: 24px;');
    expect(element.classList.contains('d-inline-block')).toBe(true);
  });

  it('passes the scale and label, and reads { x, y } points as their y', () => {
    const fixture = render(SparkTrendComponent, [{ x: 1, y: 40 }, { x: 2, y: 60 }], { yMin: 0, yMax: 100, label: 'Score trend' });

    const spark = sparkline(fixture)!;
    expect(spark.points()).toEqual([40, 60]);
    expect([spark.yMin(), spark.yMax(), spark.inputLabel()]).toEqual([0, 100, 'Score trend']);
  });

  it('draws the dash for no series, a single point, or a value that is not a series', () => {
    for (const value of [null, undefined, [], [42], 'abc', [1, 'x'], { y: 1 }]) {
      const fixture = render(SparkTrendComponent, value);
      expect(sparkline(fixture)).toBeUndefined();
      expect(text(fixture)).toBe('—');
    }
  });

  it('reads a series as points, numbers at their index', () => {
    expect(toSparkTrendPoints([5, null, { x: 7, y: 1 }])).toEqual([{ x: 0, y: 5 }, { x: 1, y: null }, { x: 7, y: 1 }]);
    expect(toSparkTrendPoints([5])).toBeNull();
    expect(toSparkTrendPoints([5], 1)).toEqual([{ x: 0, y: 5 }]);
    expect(toSparkTrendPoints(['5', 6])).toBeNull();
  });

  // Each half imports only its own chart, so an app drawing sparklines does not load the trend chart.
  it('offers the sparkline and the chart as components of their own', () => {
    expect(sparkline(render(SparkTrendSparklineComponent, [1, 2]))!.points()).toEqual([1, 2]);
    expect(chart(render(SparkTrendChartComponent, [1, 2]))!.series()).toEqual([{ id: 'value', label: '', points: [{ x: 0, y: 1 }, { x: 1, y: 2 }] }]);
    expect(text(render(SparkTrendChartComponent, [1]))).toBe('—');
  });

  it('honours minPoints', () => {
    expect(sparkline(render(SparkTrendComponent, [42], { minPoints: 1 }))).toBeDefined();
    expect(sparkline(render(SparkTrendComponent, [1, 2], { minPoints: 3 }))).toBeUndefined();
  });

  it("draws a trend chart with one series and a goal for display: 'chart'", () => {
    const when = new Date('2026-01-01T00:00:00Z');
    const fixture = render(SparkTrendComponent, [{ x: when, y: 50 }, 75], {
      display: 'chart', yMin: 0, yMax: 100, goal: 80, goalLabel: '80% goal', label: 'Over time', seriesId: 'score', seriesLabel: 'Score',
    });

    expect(sparkline(fixture)).toBeUndefined();
    const c = chart(fixture)!;
    expect(c.series()).toEqual([{ id: 'score', label: 'Score', points: [{ x: when, y: 50 }, { x: 1, y: 75 }] }]);
    expect([c.yMin(), c.yMax(), c.goal(), c.goalLabel(), c.inputLabel()]).toEqual([0, 100, 80, '80% goal', 'Over time']);
  });
});

describe('SparkGaugeComponent', () => {
  const meter = (fixture: ComponentFixture<unknown>) => host(fixture).querySelector('[role="meter"]') as HTMLElement | null;
  const arc = (fixture: ComponentFixture<unknown>) => host(fixture).querySelector('.spark-gauge-arc')!;

  it('draws the score in a ring filled to its share and coloured by band, without a grade by default', () => {
    const fixture = render(SparkGaugeComponent, 72.4);

    expect(text(fixture)).toBe('72');
    expect(arc(fixture).getAttribute('stroke-dasharray')).toBe('72.4 100');
    expect(arc(fixture).getAttribute('stroke')).toBe('var(--bs-warning)');
    expect(host(fixture).querySelector('.badge')).toBeNull();
    expect(meter(fixture)!.getAttribute('aria-label')).toBe('Score');
    expect(meter(fixture)!.getAttribute('aria-valuetext')).toBe('72');
  });

  it('shows the letter grade with letter-grades', () => {
    const fixture = render(SparkGaugeComponent, 72, { bands: 'letter-grades' });

    // ng-bootstrap's badge: a bare .badge class is unstyled, since the badge CSS ships with the component, not
    // globally (seen in SeoTester, Q-M6d: a black "C" on the dark page with no badge behind it).
    const badge = host(fixture).querySelector('bs-badge .badge')!;
    expect(badge.textContent!.trim()).toBe('C');
    expect([...badge.classList].some(c => c.endsWith('-warning'))).toBe(true);
    expect(meter(fixture)!.getAttribute('aria-valuetext')).toBe('72 (C)');
    expect(arc(fixture).getAttribute('stroke')).toBe('var(--bs-warning)');
  });

  it('grades the edges: 90 is an A, 49.9 an F', () => {
    expect(text(render(SparkGaugeComponent, 90, { bands: 'letter-grades' }))).toBe('90A');
    expect(text(render(SparkGaugeComponent, 49.9, { bands: 'letter-grades', decimals: 1 }))).toBe('49.9F');
  });

  it('clamps the ring to the scale and honours min, max, suffix and size', () => {
    const over = render(SparkGaugeComponent, 12, { max: 10, suffix: '/10', size: 64 });

    expect(arc(over).getAttribute('stroke-dasharray')).toBe('100 100');
    expect(text(over)).toBe('12/10');
    expect(host(over).querySelector('svg')!.getAttribute('width')).toBe('64');
    expect(arc(render(SparkGaugeComponent, -3)).getAttribute('stroke-dasharray')).toBe('0 100');
  });

  it('draws a muted dash and no ring for no value, but a ring for a real 0', () => {
    for (const value of [null, undefined, '', 'abc']) {
      const fixture = render(SparkGaugeComponent, value);
      expect(meter(fixture)).toBeNull();
      expect(text(fixture)).toBe('—');
    }
    expect(meter(render(SparkGaugeComponent, 0))).not.toBeNull();
  });
});

describe('provideSparkScoreRenderers', () => {
  it('registers the four renderers after the app ones, so an app name wins', () => {
    TestBed.configureTestingModule({
      providers: [provideSparkScoreRenderers([{ name: 'spark-delta', columnComponent: SparkGaugeComponent }])],
    });

    const registry = TestBed.inject(SPARK_ATTRIBUTE_RENDERERS);
    const names = registry.map(r => r.name);
    expect(names).toEqual(expect.arrayContaining(['spark-gauge', 'spark-threshold-bar', 'spark-trend', 'spark-delta', 'paragraph']));
    expect(registry.find(r => r.name === 'spark-delta')!.columnComponent).toBe(SparkGaugeComponent);
    expect(registry.find(r => r.name === 'spark-trend')!.detailComponent).toBe(SparkTrendComponent);
  });
});
