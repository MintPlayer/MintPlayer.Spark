import { TestBed } from '@angular/core/testing';
import { provideRouter, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { Subject } from 'rxjs';

import { SparkPoEditComponent } from './spark-po-edit.component';
import { SparkService, SparkLanguageService, SparkLiveService } from '@mintplayer/ng-spark/services';
import { EntityType, PersistentObject, ShowedOn, resolveTranslation } from '@mintplayer/ng-spark/models';
import { settle, StubComponent } from '../../src/test-utils';

/**
 * `spark-po-edit` on a live type (X4): a version someone else saves while the form is open is adopted when
 * the form is untouched, and offered through a banner and the existing three-way merge when it is not.
 * SparkLiveService is stubbed.
 */

const personType: EntityType = {
  id: 't-person', name: 'Person', alias: 'person', clrType: 'Test.Person', live: true,
  attributes: [
    { id: 'a-first', name: 'FirstName', dataType: 'string', isRequired: true, isReadOnly: false, order: 1, showedOn: ShowedOn.PersistentObject } as any,
    { id: 'a-last', name: 'LastName', dataType: 'string', isRequired: false, isReadOnly: false, order: 2, showedOn: ShowedOn.PersistentObject } as any,
  ],
} as any;

const person = (etag: string, first: string, last: string): PersistentObject => ({
  id: 'people/1', name: `${first} ${last}`, objectTypeId: 't-person', etag,
  attributes: [
    { id: 'a-first', name: 'FirstName', value: first } as any,
    { id: 'a-last', name: 'LastName', value: last } as any,
  ],
} as any);

const routes: Routes = [
  { path: 'po/:type/:id/edit', component: SparkPoEditComponent },
  { path: 'po/:type/:id', component: StubComponent },
];

async function setup(type: EntityType = personType, overrides: Record<string, unknown> = {}) {
  const events = new Subject<any>();
  const live = { watchPersistentObject: vi.fn(() => events.asObservable()) };
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([type]),
    get: vi.fn().mockResolvedValue(person('A:1', 'Alice', 'Smith')),
    update: vi.fn().mockResolvedValue({ id: 'people/1' }),
    ...overrides,
  };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter(routes),
      { provide: SparkService, useValue: service },
      { provide: SparkLiveService, useValue: live },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k, resolve: (ts: any) => resolveTranslation(ts, 'en') } },
    ],
  });
  const harness = await RouterTestingHarness.create();
  const c = await harness.navigateByUrl('/po/person/people%2F1/edit', SparkPoEditComponent);
  await settle(harness.fixture);
  return { harness, c, service, live, events };
}

const text = (harness: RouterTestingHarness) => harness.fixture.nativeElement.textContent as string;

describe('SparkPoEditComponent live', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('watches the object while editing a live type', async () => {
    const { live } = await setup();
    expect(live.watchPersistentObject).toHaveBeenCalledWith('t-person', 'people/1');
  });

  it('does not watch a type that is not live', async () => {
    const { live } = await setup({ ...personType, live: false } as any);
    expect(live.watchPersistentObject).not.toHaveBeenCalled();
  });

  it('ignores a push of the version it already holds', async () => {
    const { c, harness, events } = await setup();
    c.formData.set({ ...c.formData(), FirstName: 'Mine' });
    events.next({ type: 'po', po: person('A:1', 'Alice', 'Smith') });
    await settle(harness.fixture);

    expect(c.liveTheirs()).toBeNull();
    expect(c.formData()['FirstName']).toBe('Mine');
  });

  it('adopts a newer version silently while the form is untouched; the next save posts its etag', async () => {
    const { c, harness, events, service } = await setup();
    events.next({ type: 'po', po: person('A:2', 'Alice', 'Jones') });
    await settle(harness.fixture);

    expect(c.item()?.etag).toBe('A:2');
    expect(c.formData()['LastName']).toBe('Jones');
    expect(c.liveTheirs()).toBeNull();
    expect(harness.fixture.nativeElement.querySelector('.spark-live-changed')).toBeNull();

    await c.onSave();
    expect(service.update.mock.calls[0][2].etag).toBe('A:2');
  });

  it('leaves a dirty form alone and shows the banner', async () => {
    const { c, harness, events } = await setup();
    c.formData.set({ ...c.formData(), FirstName: 'Mine' });
    events.next({ type: 'po', po: person('A:2', 'Alice', 'Jones') });
    await settle(harness.fixture);

    expect(c.formData()['FirstName']).toBe('Mine');
    expect(c.formData()['LastName']).toBe('Smith');
    expect(c.item()?.etag).toBe('A:1');
    expect(c.liveTheirs()?.etag).toBe('A:2');
    expect(harness.fixture.nativeElement.querySelector('.spark-live-changed')).not.toBeNull();
    expect(text(harness)).toContain('Changed by someone else while you edit.');
  });

  it('Review merges disjoint edits and adopts their etag', async () => {
    const { c, harness, events, service } = await setup();
    c.formData.set({ ...c.formData(), FirstName: 'Mine' });
    events.next({ type: 'po', po: person('A:2', 'Alice', 'Jones') });
    await settle(harness.fixture);

    (harness.fixture.nativeElement.querySelector('.spark-live-review') as HTMLButtonElement).click();
    await settle(harness.fixture);

    expect(c.liveTheirs()).toBeNull();
    expect(c.item()?.etag).toBe('A:2');
    expect(c.formData()['FirstName']).toBe('Mine');
    expect(c.formData()['LastName']).toBe('Jones');
    expect(c.pendingConflict()).toBeNull();
    // The existing merged notice, naming what they changed. Nothing is saved.
    expect(text(harness)).toContain('common.conflictMerged');
    expect(service.update).not.toHaveBeenCalled();
  });

  it('Review opens the conflict dialog when both changed the same field', async () => {
    const { c, harness, events } = await setup();
    c.formData.set({ ...c.formData(), FirstName: 'Mine' });
    events.next({ type: 'po', po: person('A:2', 'Theirs', 'Smith') });
    await settle(harness.fixture);

    c.reviewLiveChange();
    await settle(harness.fixture);

    expect(c.pendingConflict()?.result.conflicts.map(x => x.attribute.name)).toEqual(['FirstName']);
    expect(c.item()?.etag).toBe('A:1');
    expect(c.formData()['FirstName']).toBe('Mine');

    // Choosing theirs applies it on top of their version, whose etag the form now holds.
    c.onConflictResolved({ FirstName: 'theirs' } as any);
    await settle(harness.fixture);
    expect(c.item()?.etag).toBe('A:2');
    expect(c.formData()['FirstName']).toBe('Theirs');
  });

  it('gone while editing reads as a 409 deleted', async () => {
    const { c, harness, events } = await setup();
    events.next({ type: 'gone' });
    await settle(harness.fixture);

    expect(c.validationErrors().map(e => e.errorMessage.Entries[0].Value)).toEqual(['common.deletedByAnotherUser']);
  });

  it('sets a push aside during a save and looks again when the save fails', async () => {
    let fail!: (e: unknown) => void;
    const { c, harness, events } = await setup(personType, {
      update: vi.fn(() => new Promise((_, reject) => { fail = reject; })),
    });
    c.formData.set({ ...c.formData(), FirstName: 'Mine' });
    const saving = c.onSave();
    events.next({ type: 'po', po: person('A:2', 'Alice', 'Jones') });
    await settle(harness.fixture);
    expect(c.liveTheirs()).toBeNull();

    fail(new HttpErrorResponse({ status: 500, error: { error: 'boom' } }));
    await saving;
    await settle(harness.fixture);
    expect(c.liveTheirs()?.etag).toBe('A:2');
  });

  it('the 409 path is unchanged: a save conflict still merges against a fresh read', async () => {
    const theirs = person('A:3', 'Alice', 'Brown');
    const { c, harness, service } = await setup(personType, {
      update: vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 409, error: { result: { reason: 'changed' } } })),
    });
    service.get.mockResolvedValueOnce(theirs);
    c.formData.set({ ...c.formData(), FirstName: 'Mine' });
    await c.onSave();
    await settle(harness.fixture);

    expect(c.item()?.etag).toBe('A:3');
    expect(c.formData()['LastName']).toBe('Brown');
    expect(c.formData()['FirstName']).toBe('Mine');
  });
});
