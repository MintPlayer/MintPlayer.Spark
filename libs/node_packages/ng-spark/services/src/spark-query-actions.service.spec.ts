import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkQueryActionsService } from './spark-query-actions.service';
import { SparkService } from './spark.service';

/**
 * #327 §9.7. A query's custom actions used to be obtainable only by rendering
 * `<spark-query-grid>`, which resolved the query, its entity type and the action list privately.
 * A page wanting the same buttons elsewhere had to duplicate that resolution — and duplicate the
 * `showedOn` predicate with it, which is exactly the thing that had already been got wrong once
 * (both grids tested for `"list"`, a value nothing emits, so correctly authored actions rendered
 * nowhere).
 */

const carsQuery = { id: 'q-cars', name: 'AllCars', source: 'Database.Cars', entityType: 'Car' } as any;
const carType = { id: 'type-car', name: 'Car', alias: 'car', attributes: [] } as any;
const personType = { id: 'type-person', name: 'Person', alias: 'person', attributes: [] } as any;

const action = (name: string, showedOn: string) => ({ name, displayName: { en: name }, showedOn } as any);

function setup(overrides: Record<string, unknown> = {}) {
  const service = {
    getQuery: vi.fn().mockResolvedValue(carsQuery),
    getEntityTypes: vi.fn().mockResolvedValue([personType, carType]),
    getCustomActions: vi.fn().mockResolvedValue([]),
    executeCustomAction: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };

  TestBed.configureTestingModule({
    providers: [SparkQueryActionsService, { provide: SparkService, useValue: service }],
  });

  return { sut: TestBed.inject(SparkQueryActionsService), service };
}

describe('SparkQueryActionsService', () => {
  beforeEach(() => TestBed.resetTestingModule());

  describe('actionsFor', () => {
    it('resolves the query, its entity type, and that type’s actions', async () => {
      const { sut, service } = setup({
        getCustomActions: vi.fn().mockResolvedValue([action('Export', 'query')]),
      });

      const actions = await sut.actionsFor('cars');

      expect(service.getQuery).toHaveBeenCalledWith('cars');
      expect(service.getCustomActions).toHaveBeenCalledWith('type-car');
      expect(actions.map(a => a.name)).toEqual(['Export']);
    });

    it('keeps "query" and "both", drops "detail"', async () => {
      // Shares filterQueryActions with the grid rather than re-deriving the predicate. A second
      // copy of this rule is what previously rendered every action nowhere.
      const { sut } = setup({
        getCustomActions: vi.fn().mockResolvedValue([
          action('OnQuery', 'query'),
          action('OnBoth', 'both'),
          action('OnDetail', 'detail'),
        ]),
      });

      const actions = await sut.actionsFor('cars');

      expect(actions.map(a => a.name)).toEqual(['OnQuery', 'OnBoth']);
    });

    it('matches the entity type by name, not by position', async () => {
      // getEntityTypes returns Person first; picking [0] would resolve actions for the wrong type.
      const { sut, service } = setup();

      await sut.actionsFor('cars');

      expect(service.getCustomActions).toHaveBeenCalledWith('type-car');
    });

    it('falls back to matching the entity type by id', async () => {
      const byId = { ...carsQuery, entityType: 'type-car' };
      const { sut, service } = setup({ getQuery: vi.fn().mockResolvedValue(byId) });

      await sut.actionsFor('cars');

      expect(service.getCustomActions).toHaveBeenCalledWith('type-car');
    });

    it('returns no buttons — not an error — when the query resolves to no entity type', async () => {
      // A caller rendering a toolbar wants "nothing to show", not a broken page.
      const { sut, service } = setup({
        getQuery: vi.fn().mockResolvedValue({ ...carsQuery, entityType: 'Nonexistent' }),
      });

      await expect(sut.actionsFor('cars')).resolves.toEqual([]);
      expect(service.getCustomActions).not.toHaveBeenCalled();
    });

    it('returns no buttons when the query itself does not resolve', async () => {
      const { sut } = setup({ getQuery: vi.fn().mockResolvedValue(null) });

      await expect(sut.actionsFor('nope')).resolves.toEqual([]);
    });

    it('does not cache across calls', async () => {
      // Actions depend on the caller. A memo keyed by type id would survive a sign-out and keep
      // offering buttons the new principal may not have.
      const { sut, service } = setup();

      await sut.actionsFor('cars');
      await sut.actionsFor('cars');

      expect(service.getCustomActions).toHaveBeenCalledTimes(2);
    });
  });

  describe('execute', () => {
    it('runs the action against the resolved entity type, passing ids straight through', async () => {
      const { sut, service } = setup();

      await sut.execute('cars', 'Export', { selectedItemIds: ['cars/1', 'cars/2'] });

      expect(service.executeCustomAction).toHaveBeenCalledWith(
        'type-car', 'Export', undefined, ['cars/1', 'cars/2'], undefined, 'q-cars');
    });

    it('passes the parent through when one is given', async () => {
      const parent = { id: 'companies/1', name: 'Company', objectTypeId: 't', attributes: [] } as any;
      const { sut, service } = setup();

      await sut.execute('cars', 'Export', { parent });

      expect(service.executeCustomAction).toHaveBeenCalledWith('type-car', 'Export', parent, undefined, undefined, 'q-cars');
    });

    it('forwards a sub-query container when one is given', async () => {
      // Same distinction the grid draws: the container is a DIFFERENT type from the action's, so
      // it travels as id + type rather than in the `parent` slot.
      const { sut, service } = setup();

      await sut.execute('cars', 'Export', {
        selectedItemIds: ['cars/1'],
        queryParent: { id: 'companies/1', type: 'Company' },
      });

      expect(service.executeCustomAction).toHaveBeenCalledWith(
        'type-car', 'Export', undefined, ['cars/1'], { id: 'companies/1', type: 'Company' }, 'q-cars');
    });

    it('throws — rather than silently doing nothing — when there is nothing to execute against', async () => {
      // The opposite stance from actionsFor. An empty toolbar is a reasonable answer; a button
      // press that quietly does nothing is not.
      const { sut, service } = setup({
        getQuery: vi.fn().mockResolvedValue({ ...carsQuery, entityType: 'Nonexistent' }),
      });

      await expect(sut.execute('cars', 'Export')).rejects.toThrow(/Export/);
      expect(service.executeCustomAction).not.toHaveBeenCalled();
    });
  });
});
