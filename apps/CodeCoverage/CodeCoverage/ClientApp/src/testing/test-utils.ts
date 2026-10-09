import { Component, input, model, output, Provider } from '@angular/core';
import { ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, Params } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { vi } from 'vitest';
import { BrowseService } from '../app/services/browse.service';

/**
 * Settles a fixture: drains change detection and the microtask queue until quiet, then takes one
 * macrotask turn. Components here self-fetch and await more than once, so a single `whenStable()`
 * returns while the second level is still pending. The macrotask turn keeps any custom-element
 * registration (ng-bootstrap's Lit wrappers register from `afterNextRender`) inside the fixture's
 * lifetime; ending a test before it lands makes jsdom throw `_namespaceURI` from an unhandled
 * rejection that fails the run while every assertion passes. Same helper as ng-spark's test-utils.
 */
export async function settle(fixture: ComponentFixture<unknown>, rounds = 5): Promise<void> {
  for (let i = 0; i < rounds; i++) {
    await fixture.whenStable();
    await Promise.resolve();
    fixture.detectChanges();
  }
  await new Promise(resolve => setTimeout(resolve, 0));
  await fixture.whenStable();
  fixture.detectChanges();
}

export type BrowseServiceStub = { [K in keyof BrowseService]: ReturnType<typeof vi.fn> };

/**
 * A `BrowseService` whose every method is a `vi.fn()` that rejects unless overridden. Every panel
 * and page self-fetches through this service, so stubbing it is far cheaper than routing each
 * spec through HttpTestingController (browse.service.spec.ts itself does that). A call nobody set
 * up fails loudly instead of hanging.
 */
export function createBrowseStub(overrides: Partial<Record<keyof BrowseService, (...args: any[]) => any>> = {}): BrowseServiceStub {
  const methods: (keyof BrowseService)[] = [
    'getAccount', 'getAccountRepos', 'getDependencyGraph', 'getRepo', 'getHistory', 'getSparklines', 'getBranches',
    'getCommits', 'getCommit', 'getTree', 'getHierarchy', 'getFile', 'rotateBadgeToken',
  ];
  const stub = {} as BrowseServiceStub;
  for (const name of methods) {
    const impl = overrides[name] ?? (() => Promise.reject(new Error(`BrowseService.${String(name)} not stubbed`)));
    stub[name] = vi.fn(impl);
  }
  return stub;
}

export function provideBrowseStub(stub: BrowseServiceStub): Provider {
  return { provide: BrowseService, useValue: stub };
}

/**
 * An `ActivatedRoute` whose `paramMap`, `queryParamMap` and `fragment` are driven by
 * BehaviorSubjects, with `snapshot` kept in step. Call `setParams` / `setQueryParams` /
 * `setFragment` to simulate a navigation that reuses the component.
 */
export class ActivatedRouteStub {
  private readonly params$ = new BehaviorSubject(convertToParamMap({}));
  private readonly queryParams$ = new BehaviorSubject(convertToParamMap({}));
  private readonly fragment$ = new BehaviorSubject<string | null>(null);

  readonly paramMap = this.params$.asObservable();
  readonly queryParamMap = this.queryParams$.asObservable();
  readonly fragment = this.fragment$.asObservable();
  readonly snapshot = {
    paramMap: convertToParamMap({}),
    queryParamMap: convertToParamMap({}),
    params: {} as Params,
    queryParams: {} as Params,
    fragment: null as string | null,
  };

  constructor(init: { params?: Params; queryParams?: Params; fragment?: string | null } = {}) {
    if (init.params) this.setParams(init.params);
    if (init.queryParams) this.setQueryParams(init.queryParams);
    if (init.fragment !== undefined) this.setFragment(init.fragment);
  }

  setParams(params: Params): void {
    this.snapshot.params = params;
    this.snapshot.paramMap = convertToParamMap(params);
    this.params$.next(this.snapshot.paramMap);
  }

  setQueryParams(queryParams: Params): void {
    this.snapshot.queryParams = queryParams;
    this.snapshot.queryParamMap = convertToParamMap(queryParams);
    this.queryParams$.next(this.snapshot.queryParamMap);
  }

  setFragment(fragment: string | null): void {
    this.snapshot.fragment = fragment;
    this.fragment$.next(fragment);
  }
}

export function provideActivatedRouteStub(stub: ActivatedRouteStub): Provider {
  return { provide: ActivatedRoute, useValue: stub };
}

/*
 * Stand-ins for ng-bootstrap's chart wrappers. The real ones side-effect-import Lit elements that
 * jsdom cannot host (canvas, ResizeObserver, and the `_namespaceURI` teardown rejection). Swap them
 * in with `TestBed.overrideComponent(Host, { remove: { imports: [BsXChartComponent] }, add: {
 * imports: [StubXChart] } })`. Same selector and the inputs the app binds, so a spec can read what
 * the host passed via `fixture.debugElement.query(By.directive(StubXChart)).componentInstance`.
 */

@Component({ selector: 'bs-hierarchy-chart', template: '' })
export class StubHierarchyChart {
  readonly data = input<unknown>();
  readonly layout = input<string>();
  readonly rootId = model<string | undefined>();
  readonly maxDepth = input<number | 'auto' | undefined>();
  readonly colorMin = input<number>();
  readonly colorMax = input<number>();
  readonly inputLabel = input<string>();
  readonly valueUnitLabel = input<string>();
  readonly zoom = output<any>();
  readonly nodeSelect = output<any>();
}

@Component({ selector: 'bs-sparkline', template: '' })
export class StubSparkline {
  readonly points = input<(number | null)[]>([]);
  readonly yMin = input<number>();
  readonly yMax = input<number>();
  readonly inputLabel = input<string>();
}

@Component({ selector: 'bs-trend-chart', template: '' })
export class StubTrendChart {
  readonly series = input<unknown[]>([]);
  readonly yMin = input<number>();
  readonly yMax = input<number>();
  readonly goal = input<number>();
  readonly goalLabel = input<string>();
  readonly inputLabel = input<string>();
}
