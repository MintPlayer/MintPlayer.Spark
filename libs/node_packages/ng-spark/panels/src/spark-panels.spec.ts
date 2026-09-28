import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import {
  SPARK_DETAIL_PANELS,
  SPARK_QUERY_LIST_ACTIONS,
  orderSparkExtensions,
  parseSparkDeletedParam,
  provideSparkDetailPanels,
  provideSparkQueryListActions,
} from './spark-panels';

@Component({ standalone: true, template: 'a' })
class PanelA {}
@Component({ standalone: true, template: 'b' })
class PanelB {}

describe('orderSparkExtensions', () => {
  it('sorts by key, unordered last, stable among equals', () => {
    const ordered = orderSparkExtensions([
      { id: 'late', order: undefined },
      { id: 'second', order: 20 },
      { id: 'first', order: 10 },
      { id: 'alsoLate', order: undefined },
    ], e => e.order);
    expect(ordered.map(e => e.id)).toEqual(['first', 'second', 'late', 'alsoLate']);
  });

  it('lets a later registration replace an id in its original slot', () => {
    const ordered = orderSparkExtensions([
      { id: 'a', v: 1 },
      { id: 'b', v: 2 },
      { id: 'a', v: 3 },
    ], () => undefined);
    expect(ordered).toEqual([{ id: 'a', v: 3 }, { id: 'b', v: 2 }]);
  });

  it('treats null and empty as no extensions', () => {
    expect(orderSparkExtensions(null, () => 0)).toEqual([]);
    expect(orderSparkExtensions([], () => 0)).toEqual([]);
  });
});

describe('parseSparkDeletedParam', () => {
  it('accepts the three modes case-insensitively and nothing else', () => {
    expect(parseSparkDeletedParam('only')).toBe('only');
    expect(parseSparkDeletedParam('Include')).toBe('include');
    expect(parseSparkDeletedParam('exclude')).toBe('exclude');
    expect(parseSparkDeletedParam('all')).toBeNull();
    expect(parseSparkDeletedParam(null)).toBeNull();
    expect(parseSparkDeletedParam(undefined)).toBeNull();
  });
});

describe('provide* helpers', () => {
  it('multi-provide: every call adds', () => {
    TestBed.configureTestingModule({
      providers: [
        provideSparkDetailPanels({ id: 'a', component: PanelA }),
        provideSparkDetailPanels({ id: 'b', component: PanelB, order: 1 }),
        provideSparkQueryListActions({ id: 'x', component: PanelA }),
      ],
    });
    expect(TestBed.inject(SPARK_DETAIL_PANELS).map(p => p.id)).toEqual(['a', 'b']);
    expect(TestBed.inject(SPARK_QUERY_LIST_ACTIONS).map(p => p.id)).toEqual(['x']);
  });
});
