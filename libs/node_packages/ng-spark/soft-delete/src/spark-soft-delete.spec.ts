import { Location } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SPARK_RETURN_URL_STATE_KEY, SparkLanguageService, SparkReturnNavigationService, SparkService } from '@mintplayer/ng-spark/services';
import { SPARK_DETAIL_ACTIONS, SPARK_QUERY_LIST_ACTIONS, SparkDetailContext, SparkQueryListContext } from '@mintplayer/ng-spark/panels';
import { SparkSoftDeleteService } from './spark-soft-delete.service';
import { SparkDeletedToggleComponent } from './spark-deleted-toggle.component';
import { SparkSoftDeleteActionsComponent } from './spark-soft-delete-actions.component';
import { provideSparkSoftDelete } from './provide-spark-soft-delete';

function listContext(overrides: Partial<SparkQueryListContext> = {}): SparkQueryListContext {
  return {
    query: { id: 'q', name: 'People' } as any,
    entityType: { id: 't', name: 'Person' } as any,
    permissions: { canViewDeleted: true } as any,
    deleted: 'exclude',
    setDeleted: vi.fn(),
    reload: vi.fn(),
    ...overrides,
  };
}

function detailContext(overrides: Partial<SparkDetailContext> = {}): SparkDetailContext {
  return {
    type: 'person',
    id: 'people/1',
    item: { id: 'people/1', attributes: [] } as any,
    entityType: { id: 't', name: 'Person' } as any,
    permissions: { canRestore: true, canPurge: true } as any,
    deleted: 'only',
    reload: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
}

describe('soft-delete entry point (#460)', () => {
  let spark: { postEnvelope: ReturnType<typeof vi.fn> };
  const confirmSpy = vi.spyOn(globalThis, 'confirm');

  beforeEach(() => {
    spark = { postEnvelope: vi.fn().mockResolvedValue(undefined) };
    confirmSpy.mockReset().mockReturnValue(true);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: SparkService, useValue: spark },
        { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
        provideSparkSoftDelete(),
      ],
    });
  });
  afterEach(() => confirmSpy.mockReset());

  it('registers the toggle as a query-list action and Restore/Purge as a detail action', () => {
    expect(TestBed.inject(SPARK_QUERY_LIST_ACTIONS).map(a => a.component)).toEqual([SparkDeletedToggleComponent]);
    expect(TestBed.inject(SPARK_DETAIL_ACTIONS).map(a => a.component)).toEqual([SparkSoftDeleteActionsComponent]);
  });

  it('service posts restore and purge with the id in the body', async () => {
    const service = TestBed.inject(SparkSoftDeleteService);
    await service.restore('person', 'people/1');
    await service.purge('person', 'people/1');
    expect(spark.postEnvelope).toHaveBeenNthCalledWith(1, '/po/restore', { objectTypeId: 'person', id: 'people/1' });
    expect(spark.postEnvelope).toHaveBeenNthCalledWith(2, '/po/purge', { objectTypeId: 'person', id: 'people/1' });
  });

  describe('SparkDeletedToggleComponent', () => {
    function render(ctx: SparkQueryListContext) {
      const fixture = TestBed.createComponent(SparkDeletedToggleComponent);
      fixture.componentRef.setInput('context', ctx);
      fixture.detectChanges();
      return fixture;
    }

    it('is absent without ViewDeleted', () => {
      const fixture = render(listContext({ permissions: { canViewDeleted: false } as any }));
      expect(fixture.nativeElement.querySelector('button')).toBeNull();
      const older = render(listContext({ permissions: {} as any }));
      expect(older.nativeElement.querySelector('button')).toBeNull();
    });

    it('switches to the recycle bin and back', () => {
      const ctx = listContext();
      const fixture = render(ctx);
      const button: HTMLButtonElement = fixture.nativeElement.querySelector('button');
      expect(button.getAttribute('aria-pressed')).toBe('false');
      button.click();
      expect(ctx.setDeleted).toHaveBeenCalledWith('only');

      const active = listContext({ deleted: 'only' });
      const activeFixture = render(active);
      const activeButton: HTMLButtonElement = activeFixture.nativeElement.querySelector('button');
      expect(activeButton.getAttribute('aria-pressed')).toBe('true');
      activeButton.click();
      expect(active.setDeleted).toHaveBeenCalledWith('exclude');
    });
  });

  describe('SparkSoftDeleteActionsComponent', () => {
    function render(ctx: SparkDetailContext) {
      const fixture = TestBed.createComponent(SparkSoftDeleteActionsComponent);
      fixture.componentRef.setInput('context', ctx);
      fixture.detectChanges();
      return fixture;
    }

    it('shows nothing on a live row', () => {
      const fixture = render(detailContext({ deleted: null }));
      expect(fixture.nativeElement.querySelector('button')).toBeNull();
    });

    it('shows Restore and Purge per right', () => {
      expect(render(detailContext()).nativeElement.querySelectorAll('button').length).toBe(2);
      const restoreOnly = render(detailContext({ permissions: { canRestore: true } as any }));
      expect(restoreOnly.nativeElement.querySelector('.spark-restore')).not.toBeNull();
      expect(restoreOnly.nativeElement.querySelector('.spark-purge')).toBeNull();
    });

    it('Restore posts, then drops ?deleted from the route', async () => {
      const router = TestBed.inject(Router);
      const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
      const fixture = render(detailContext());
      await fixture.componentInstance.restore();
      expect(spark.postEnvelope).toHaveBeenCalledWith('/po/restore', { objectTypeId: 'person', id: 'people/1' });
      expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({
        relativeTo: TestBed.inject(ActivatedRoute),
        queryParams: { deleted: null },
        queryParamsHandling: 'merge',
      }));
    });

    it('Purge asks first, then posts and returns to the recycle bin, not the previous page', async () => {
      const back = vi.spyOn(TestBed.inject(Location), 'back').mockImplementation(() => undefined);
      const returnToList = vi.spyOn(TestBed.inject(SparkReturnNavigationService), 'returnToList').mockResolvedValue(undefined);
      const fixture = render(detailContext());

      confirmSpy.mockReturnValueOnce(false);
      await fixture.componentInstance.purge();
      expect(spark.postEnvelope).not.toHaveBeenCalled();

      await fixture.componentInstance.purge();
      expect(spark.postEnvelope).toHaveBeenCalledWith('/po/purge', { objectTypeId: 'person', id: 'people/1' });
      expect(returnToList).toHaveBeenCalledWith('Person', { deletedView: true });
      expect(back).not.toHaveBeenCalled();
    });

    it('returns to the list a row link recorded, else to the type\'s list in the recycle bin', async () => {
      const router = TestBed.inject(Router);
      const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
      const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
      const returns = TestBed.inject(SparkReturnNavigationService);
      (spark as any).getQueries = vi.fn().mockResolvedValue([{ id: 'q-people', alias: 'people', entityType: 'Person' }]);
      (spark as any).getProgramUnits = vi.fn().mockResolvedValue({ programUnitGroups: [] });

      history.replaceState({ [SPARK_RETURN_URL_STATE_KEY]: '/query/people?deleted=only' }, '');
      await returns.returnToList('Person', { deletedView: true });
      expect(navigateByUrl).toHaveBeenCalledWith('/query/people?deleted=only', { replaceUrl: true });

      history.replaceState({ [SPARK_RETURN_URL_STATE_KEY]: '//elsewhere.example' }, '');
      await returns.returnToList('Person', { deletedView: true });
      expect(navigate).toHaveBeenCalledWith(['/query', 'people'], { replaceUrl: true, queryParams: { deleted: 'only' } });
      history.replaceState(null, '');
    });

    it('surfaces a refusal inline', async () => {
      spark.postEnvelope.mockRejectedValue({ status: 404 });
      const fixture = render(detailContext());
      await fixture.componentInstance.restore();
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.spark-soft-delete-error')?.textContent).toContain('softDelete.unavailable');
    });
  });
});
