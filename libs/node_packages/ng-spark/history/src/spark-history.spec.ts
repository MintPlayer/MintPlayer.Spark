import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { EntityType, PersistentObject, ShowedOn } from '@mintplayer/ng-spark/models';
import { SPARK_DETAIL_PANELS } from '@mintplayer/ng-spark/panels';
import { SparkHistoryService, SparkRevision } from './spark-history.service';
import { SparkPoHistoryComponent } from './spark-po-history.component';
import { SparkHistoryPanelComponent } from './spark-history-panel.component';
import { provideSparkHistory } from './provide-spark-history';
import { comparableValue, diffRevision } from './revision-diff';

const entityType: EntityType = {
  id: 't-doc', name: 'Doc', alias: 'doc', clrType: 'Test.Doc',
  attributes: [
    { id: 'a1', name: 'Title', dataType: 'string', isVisible: true, order: 1, showedOn: ShowedOn.PersistentObject } as any,
    { id: 'a2', name: 'Body', dataType: 'string', isVisible: true, order: 2, showedOn: ShowedOn.PersistentObject } as any,
    { id: 'a3', name: 'Hidden', dataType: 'string', isVisible: false, order: 3, showedOn: ShowedOn.PersistentObject } as any,
  ],
} as any;

function po(values: Record<string, unknown>): PersistentObject {
  return {
    id: 'docs/1', name: 'Doc', objectTypeId: 't-doc',
    attributes: Object.entries(values).map(([name, value]) => ({ id: name, name, value } as any)),
  } as any;
}

const revisions: SparkRevision[] = [
  { changeVector: 'A:3', lastModified: '2026-09-01T10:00:00+02:00', userName: 'Bob', isCurrent: true },
  { changeVector: 'A:2', lastModified: '2026-08-01T10:00:00+02:00', userId: 'users/1' },
  { changeVector: 'A:1', isDeleteRevision: true },
];

describe('revision diff', () => {
  it('reports only shown attributes whose content differs', () => {
    const changes = diffRevision(entityType, po({ Title: 'old', Body: 'same', Hidden: 'x' }), po({ Title: 'new', Body: 'same', Hidden: 'y' }));
    expect(changes.map(c => c.attribute.name)).toEqual(['Title']);
  });

  it('compares nested rows by their values, not their envelopes', () => {
    const row = (value: string, etag: string) => ({ id: 'r', etag, breadcrumb: etag, attributes: [{ name: 'Qty', value }] });
    const a = { name: 'Lines', objects: [row('1', 'e1')] } as any;
    const b = { name: 'Lines', objects: [row('1', 'e2')] } as any;
    const c = { name: 'Lines', objects: [row('2', 'e1')] } as any;
    expect(comparableValue(a)).toBe(comparableValue(b));
    expect(comparableValue(a)).not.toBe(comparableValue(c));
    expect(comparableValue(undefined)).not.toBe(comparableValue({ name: 'x', value: null } as any));
  });
});

describe('history entry point (#460)', () => {
  let spark: { postEnvelope: ReturnType<typeof vi.fn> };
  const confirmSpy = vi.spyOn(globalThis, 'confirm');

  beforeEach(() => {
    confirmSpy.mockReset().mockReturnValue(true);
    spark = {
      postEnvelope: vi.fn(async (path: string, body: any) => {
        if (path === '/po/revisions') return revisions;
        if (path === '/po/revision') return po({ Title: 'old ' + body.changeVector, Body: 'same' });
        if (path === '/po/revert') return po({ Title: 'reverted', Body: 'same' });
        return undefined;
      }),
    };
    TestBed.configureTestingModule({
      providers: [
        provideNoopAnimations(),
        { provide: SparkService, useValue: spark },
        { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
        provideSparkHistory(),
      ],
    });
  });
  afterEach(() => confirmSpy.mockReset());

  it('registers the History panel', () => {
    expect(TestBed.inject(SPARK_DETAIL_PANELS).map(p => p.component)).toEqual([SparkHistoryPanelComponent]);
  });

  it('service sends deleted only when widening, and paging only when set', async () => {
    const service = TestBed.inject(SparkHistoryService);
    await service.list('doc', 'docs/1');
    await service.list('doc', 'docs/1', { skip: 50, take: 50, deleted: 'only' });
    await service.get('doc', 'docs/1', 'A:2', { deleted: 'exclude' });
    await service.revert('doc', 'docs/1', 'A:2');
    expect(spark.postEnvelope.mock.calls).toEqual([
      ['/po/revisions', { objectTypeId: 'doc', id: 'docs/1' }],
      ['/po/revisions', { objectTypeId: 'doc', id: 'docs/1', skip: 50, take: 50, deleted: 'only' }],
      ['/po/revision', { objectTypeId: 'doc', id: 'docs/1', changeVector: 'A:2' }],
      ['/po/revert', { objectTypeId: 'doc', id: 'docs/1', changeVector: 'A:2' }],
    ]);
  });

  async function render(permissions: Record<string, boolean>, deleted: 'only' | null = null) {
    const fixture = TestBed.createComponent(SparkPoHistoryComponent);
    fixture.componentRef.setInput('type', 'doc');
    fixture.componentRef.setInput('id', 'docs/1');
    fixture.componentRef.setInput('entityType', entityType);
    fixture.componentRef.setInput('current', po({ Title: 'now', Body: 'same' }));
    fixture.componentRef.setInput('permissions', permissions);
    fixture.componentRef.setInput('deleted', deleted);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('lists nothing without canViewHistory', async () => {
    const fixture = await render({ canViewHistory: false });
    expect(spark.postEnvelope).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.spark-po-history')).toBeNull();
  });

  it('lists revisions newest first, with who and the current / delete markers', async () => {
    const fixture = await render({ canViewHistory: true });
    const rows = fixture.nativeElement.querySelectorAll('.spark-history-row');
    expect(rows.length).toBe(3);
    expect(rows[0].textContent).toContain('Bob');
    expect(rows[0].textContent).toContain('history.current');
    expect(rows[1].textContent).toContain('users/1');
    expect(rows[2].textContent).toContain('history.unknownUser');
    expect(rows[2].textContent).toContain('history.deleteRevision');
  });

  it('shows a diff against the current object and a read-only view', async () => {
    const fixture = await render({ canViewHistory: true });
    await fixture.componentInstance.select(revisions[1]);
    fixture.detectChanges();

    const changes = fixture.nativeElement.querySelectorAll('.spark-history-change');
    expect(changes.length).toBe(1);
    expect(changes[0].querySelector('.spark-history-old').textContent).toContain('old A:2');
    expect(changes[0].querySelector('.spark-history-new').textContent).toContain('now');

    fixture.nativeElement.querySelector('.spark-history-view-tab').click();
    fixture.detectChanges();
    const view: HTMLElement = fixture.nativeElement.querySelector('.spark-history-view');
    expect(view.textContent).toContain('old A:2');
    expect(view.textContent).not.toContain('Hidden');
  });

  it('renders values like the detail page: reference labels, formatted dates, checkboxes, a dash for empty', async () => {
    const typed: EntityType = {
      ...entityType,
      attributes: [
        { id: 'b1', name: 'Question', dataType: 'Reference', referenceType: 'Test.Question', isVisible: true, order: 1, showedOn: ShowedOn.PersistentObject } as any,
        { id: 'b2', name: 'PostedAt', dataType: 'datetime', isVisible: true, order: 2, showedOn: ShowedOn.PersistentObject } as any,
        { id: 'b3', name: 'IsDeleted', dataType: 'boolean', isVisible: true, order: 3, showedOn: ShowedOn.PersistentObject } as any,
        { id: 'b4', name: 'Reason', dataType: 'string', isVisible: true, order: 4, showedOn: ShowedOn.PersistentObject } as any,
      ],
    } as any;
    const revision = {
      id: 'docs/1', name: 'Doc', objectTypeId: 't-doc',
      attributes: [
        { id: 'q', name: 'Question', dataType: 'Reference', value: 'Questions/1', breadcrumb: 'How do I revert?' },
        { id: 'p', name: 'PostedAt', dataType: 'datetime', value: '2026-09-29T05:39:08.6735907+00:00' },
        { id: 'd', name: 'IsDeleted', dataType: 'boolean', value: false },
        { id: 'r', name: 'Reason', dataType: 'string', value: null },
      ],
    } as any;
    spark.postEnvelope.mockImplementation(async (path: string) => path === '/po/revisions' ? revisions : revision);
    const fixture = TestBed.createComponent(SparkPoHistoryComponent);
    fixture.componentRef.setInput('type', 'doc');
    fixture.componentRef.setInput('id', 'docs/1');
    fixture.componentRef.setInput('entityType', typed);
    fixture.componentRef.setInput('current', revision);
    fixture.componentRef.setInput('permissions', { canViewHistory: true });
    fixture.detectChanges();
    await fixture.whenStable();
    await fixture.componentInstance.select(revisions[1]);
    fixture.componentInstance['view'].set('view');
    fixture.detectChanges();

    const values = [...fixture.nativeElement.querySelectorAll('.spark-history-view dd')] as HTMLElement[];
    expect(values[0].textContent!.trim()).toBe('How do I revert?');
    expect(values[1].textContent).not.toContain('2026-09-29T');
    expect(values[1].textContent).toMatch(/\d/);
    const box = values[2].querySelector('input[type=checkbox]') as HTMLInputElement;
    expect(box).not.toBeNull();
    expect(box.checked).toBe(false);
    expect(values[3].textContent!.trim()).toBe('-');
  });

  it('offers Revert only with canRevert, never for the current or a delete revision, nor from the recycle bin', async () => {
    const without = await render({ canViewHistory: true, canRevert: false });
    await without.componentInstance.select(revisions[1]);
    without.detectChanges();
    expect(without.nativeElement.querySelector('.spark-history-revert')).toBeNull();

    const withRight = await render({ canViewHistory: true, canRevert: true });
    await withRight.componentInstance.select(revisions[0]);
    withRight.detectChanges();
    expect(withRight.nativeElement.querySelector('.spark-history-revert')).toBeNull();
    await withRight.componentInstance.select(revisions[2]);
    withRight.detectChanges();
    expect(withRight.nativeElement.querySelector('.spark-history-revert')).toBeNull();
    await withRight.componentInstance.select(revisions[1]);
    withRight.detectChanges();
    expect(withRight.nativeElement.querySelector('.spark-history-revert')).not.toBeNull();

    const recycleBin = await render({ canViewHistory: true, canRevert: true }, 'only');
    await recycleBin.componentInstance.select(revisions[1]);
    recycleBin.detectChanges();
    expect(recycleBin.nativeElement.querySelector('.spark-history-revert')).toBeNull();
    expect(spark.postEnvelope).toHaveBeenCalledWith('/po/revisions', { objectTypeId: 'doc', id: 'docs/1', take: 50, deleted: 'only' });
  });

  it('Revert confirms, posts, emits the saved object and reloads the list', async () => {
    const fixture = await render({ canViewHistory: true, canRevert: true });
    const emitted: PersistentObject[] = [];
    fixture.componentInstance.reverted.subscribe(p => emitted.push(p));
    await fixture.componentInstance.select(revisions[1]);

    await fixture.componentInstance.revert();
    expect(spark.postEnvelope).toHaveBeenCalledWith('/po/revert', { objectTypeId: 'doc', id: 'docs/1', changeVector: 'A:2' });
    expect(emitted[0].attributes[0].value).toBe('reverted');
    expect(spark.postEnvelope.mock.calls.filter(c => c[0] === '/po/revisions').length).toBe(2);
  });

  it('maps a 409 to the conflict message', async () => {
    const fixture = await render({ canViewHistory: true, canRevert: true });
    await fixture.componentInstance.select(revisions[1]);
    spark.postEnvelope.mockRejectedValueOnce({ status: 409 });
    await fixture.componentInstance.revert();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('history.conflict');
  });
});
