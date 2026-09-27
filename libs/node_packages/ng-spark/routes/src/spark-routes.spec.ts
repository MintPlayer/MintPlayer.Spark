import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { sparkRoutes } from './spark-routes';

@Component({ standalone: true, template: 'list' })
class ListStub {}
@Component({ standalone: true, template: 'create' })
class CreateStub {}
@Component({ standalone: true, template: 'edit' })
class EditStub {}
@Component({ standalone: true, template: 'detail' })
class DetailStub {}

// The factories run only when a loader's dynamic import fires, inside a test, by which time the
// stubs above exist — so referencing them from the hoisted mocks is safe.
vi.mock('@mintplayer/ng-spark/query-list', () => ({ SparkQueryListComponent: ListStub }));
vi.mock('@mintplayer/ng-spark/po-create', () => ({ SparkPoCreateComponent: CreateStub }));
vi.mock('@mintplayer/ng-spark/po-edit', () => ({ SparkPoEditComponent: EditStub }));
vi.mock('@mintplayer/ng-spark/po-detail', () => ({ SparkPoDetailComponent: DetailStub }));

/**
 * The route table. Order is load-bearing: the router takes the first match, so `po/:type/new` must
 * precede `po/:type/:id` or "new" would open a detail page for an object whose id is "new".
 */
describe('sparkRoutes', () => {
  it('declares the five routes in matching order', () => {
    expect(sparkRoutes().map(r => r.path)).toEqual([
      'query/:queryId',
      'po/:type/new',
      'po/:type/:id/edit',
      'po/:type/:id',
      'po/:type',
    ]);
  });

  it('lazy-loads the library components by default', async () => {
    // The entry points are mocked (below): importing the real ones pulls the whole component tree
    // and takes longer than a test is allowed. The mocks still prove each loader imports the right
    // entry point and picks the right export from it.
    // Sequential on purpose: two concurrent dynamic imports of the same mocked specifier were seen
    // to let the second one through to the real module.
    const loaded: unknown[] = [];
    for (const route of sparkRoutes()) loaded.push(await (route.loadComponent as () => Promise<unknown>)());
    expect(loaded).toEqual([ListStub, CreateStub, EditStub, DetailStub, ListStub]);
  });

  it('uses each override for its own routes, and the query-list override for both list routes', () => {
    const queryList = () => ListStub;
    const poCreate = () => CreateStub;
    const poEdit = () => EditStub;
    const poDetail = () => DetailStub;

    expect(sparkRoutes({ queryList, poCreate, poEdit, poDetail }).map(r => r.loadComponent)).toEqual([
      queryList, poCreate, poEdit, poDetail, queryList,
    ]);
  });

  it('keeps the default loaders for the routes that were not overridden', () => {
    const poDetail = () => DetailStub;
    const routes = sparkRoutes({ poDetail });
    const defaults = sparkRoutes();

    expect(routes[3].loadComponent).toBe(poDetail);
    for (const i of [0, 1, 2, 4]) {
      expect(routes[i].loadComponent).not.toBe(poDetail);
      expect(routes[i].loadComponent!.toString()).toBe(defaults[i].loadComponent!.toString());
    }
  });

  describe('navigation', () => {
    let harness: RouterTestingHarness;

    beforeEach(async () => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [provideRouter(sparkRoutes({
          queryList: () => ListStub,
          poCreate: () => CreateStub,
          poEdit: () => EditStub,
          poDetail: () => DetailStub,
        }))],
      });
      harness = await RouterTestingHarness.create();
    });

    it.each([
      ['/query/cars', ListStub],
      ['/po/Car/new', CreateStub],
      ['/po/Car/cars-1/edit', EditStub],
      ['/po/Car/cars-1', DetailStub],
      ['/po/Car', ListStub],
    ])('%s opens the right page', async (url, expected) => {
      const component = await harness.navigateByUrl(url);
      expect(component).toBeInstanceOf(expected);
    });

    it('binds the route parameters', async () => {
      await harness.navigateByUrl('/po/Car/cars-1/edit');
      const params = TestBed.inject(Router).routerState.snapshot.root.firstChild!.params;
      expect(params).toEqual({ type: 'Car', id: 'cars-1' });
    });
  });
});
