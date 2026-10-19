import { TestBed } from '@angular/core/testing';
import { provideRouter, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { Subject } from 'rxjs';

import { SparkPoDetailComponent } from './spark-po-detail.component';
import { SparkService, SparkLanguageService, SparkLiveService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { CustomActionDefinition, EntityType, PersistentObject, ShowedOn, translated, resolveTranslation } from '@mintplayer/ng-spark/models';
import { settle, StubComponent } from '../../src/test-utils';

/** `spark-po-detail` on a live type (X4): it follows the object it shows. SparkLiveService is stubbed. */

const liveType: EntityType = {
  id: 't-run', name: 'AuditRun', alias: 'auditrun', clrType: 'Test.AuditRun', live: true,
  attributes: [
    { id: 'a-done', name: 'PagesDone', dataType: 'number', isRequired: false, isReadOnly: true, order: 1, showedOn: ShowedOn.PersistentObject } as any,
  ],
} as any;

const run = (etag: string, done: number, extra: Partial<PersistentObject> = {}): PersistentObject => ({
  id: 'auditruns/1', name: 'Run 1', objectTypeId: 't-run', etag,
  attributes: [{ id: 'a-done', name: 'PagesDone', value: done } as any],
  ...extra,
} as any);

const builtInEdit: CustomActionDefinition = {
  name: 'Edit', label: translated({ en: 'Edit' }), showedOn: 'both', selectionRule: '=1', refreshOnCompleted: false, offset: 0, isDefault: true,
} as any;
const builtInDelete: CustomActionDefinition = {
  name: 'Delete', label: translated({ en: 'Delete' }), showedOn: 'both', selectionRule: '>0', refreshOnCompleted: false, offset: 0, isDefault: true,
} as any;

const routes: Routes = [
  { path: 'po/:type/:id', component: SparkPoDetailComponent },
  { path: '', component: StubComponent },
];

async function setup(type: EntityType = liveType) {
  const events = new Subject<any>();
  const live = { watchPersistentObject: vi.fn(() => events.asObservable()) };
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([type]),
    get: vi.fn().mockResolvedValue(run('A:1', 3)),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getCustomActions: vi.fn().mockResolvedValue([builtInEdit, builtInDelete]),
    getLookupReference: vi.fn().mockResolvedValue({ values: [] }),
  };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter(routes),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkService, useValue: service },
      { provide: SparkLiveService, useValue: live },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k, resolve: (v: any) => resolveTranslation(v, 'en') } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });
  const harness = await RouterTestingHarness.create();
  const c = await harness.navigateByUrl('/po/auditrun/auditruns%2F1', SparkPoDetailComponent);
  await settle(harness.fixture);
  return { harness, c, service, live, events };
}

describe('SparkPoDetailComponent live', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('subscribes after the load, with the type id and the object id', async () => {
    const { live, c } = await setup();
    expect(c.item()?.etag).toBe('A:1');
    expect(live.watchPersistentObject).toHaveBeenCalledWith('t-run', 'auditruns/1', { deleted: null });
  });

  it('does not subscribe for a type that is not live', async () => {
    const { live } = await setup({ ...liveType, live: false } as any);
    expect(live.watchPersistentObject).not.toHaveBeenCalled();
  });

  it('replaces the shown object on a push', async () => {
    const { c, harness, events } = await setup();
    events.next({ type: 'po', po: run('A:2', 7) });
    await settle(harness.fixture);

    expect(c.item()?.etag).toBe('A:2');
    expect(c.item()?.attributes[0].value).toBe(7);
  });

  it('re-derives Edit and Delete from the pushed object\'s can and disabledActions', async () => {
    const { c, harness, events } = await setup();
    expect(c.canEdit()).toBe(true);
    expect(c.canDelete()).toBe(true);

    events.next({ type: 'po', po: run('A:2', 7, { can: { read: true, edit: false, delete: true } as any, disabledActions: ['Delete'] }) });
    await settle(harness.fixture);

    expect(c.canEdit()).toBe(false);
    expect(c.canDelete()).toBe(false);
  });

  it('shows the unavailable state on gone', async () => {
    const { c, harness, events } = await setup();
    events.next({ type: 'gone' });
    await settle(harness.fixture);

    expect(c.item()).toBeNull();
    expect(c.errorMessage()).toBe('This item was deleted or is no longer available.');
    expect(harness.fixture.nativeElement.querySelector('bs-alert')?.textContent).toContain('no longer available');
  });

  it('keeps the loaded object when the subscription ends with an error', async () => {
    const { c, harness, events } = await setup();
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    events.next({ type: 'error', code: 'unavailable', message: 'x' });
    await settle(harness.fixture);

    expect(c.item()?.etag).toBe('A:1');
    expect(c.errorMessage()).toBeNull();
    warn.mockRestore();
  });

  it('keeps one subscription across context.reload()', async () => {
    const { c, harness, live } = await setup();
    await c.load();
    await settle(harness.fixture);
    expect(live.watchPersistentObject).toHaveBeenCalledTimes(1);
  });

  it('leaves the subscription when the page is destroyed', async () => {
    const { harness, events } = await setup();
    expect(events.observed).toBe(true);
    harness.fixture.destroy();
    expect(events.observed).toBe(false);
  });
});
