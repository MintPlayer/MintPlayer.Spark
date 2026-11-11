import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it, vi } from 'vitest';

import { SparkObjectUrlService, SparkSeoService, SparkService } from '@mintplayer/ng-spark/services';
import { EntityType, PersistentObject } from '@mintplayer/ng-spark/models';
import { StubComponent } from '../../src/test-utils';
import { sparkObjectPage } from './spark-object-page';

/** A custom page for one type, as an app writes it (tier (a), #601 D19). */
@Component({ selector: 'spec-song-page', template: '{{ page.item()?.name }}' })
class SongPage {
  readonly page = sparkObjectPage();
}

const songType = { id: 't-song', name: 'Song', alias: 'song', attributes: [] } as unknown as EntityType;
const song = { id: 'songs/1', name: 'Lift', href: '/po/song/songs%2F1/lift', attributes: [] } as unknown as PersistentObject;

const routes: Routes = [
  // sparkObjectRoute names the type in the route's data instead of a `:type` segment.
  { path: 'po/song/:id', component: SongPage, data: { sparkType: 'song' } },
  { path: '', component: StubComponent },
];

async function setup(get: () => Promise<PersistentObject>) {
  const service = { getEntityTypes: vi.fn().mockResolvedValue([songType]), get: vi.fn(get) };
  const seo = { forObject: vi.fn(), noIndex: vi.fn(), reset: vi.fn() };
  const objectUrl = { apply: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      { provide: SparkService, useValue: service },
      { provide: SparkSeoService, useValue: seo },
      { provide: SparkObjectUrlService, useValue: objectUrl },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, service, seo, objectUrl };
}

describe('sparkObjectPage', () => {
  it("loads the object of a type's own route, and writes its canonical URL and SEO", async () => {
    const { harness, service, seo, objectUrl } = await setup(() => Promise.resolve(song));
    const c = await harness.navigateByUrl('/po/song/songs%2F1', SongPage);
    await harness.fixture.whenStable();

    expect(service.get).toHaveBeenCalledWith('song', 'songs/1');
    expect(c.page.item()).toBe(song);
    expect(c.page.entityType()).toBe(songType);
    expect(objectUrl.apply).toHaveBeenCalledWith(song);
    expect(seo.forObject).toHaveBeenCalledWith(songType, song, '/po/song/songs%2F1/lift');
    expect(seo.noIndex).not.toHaveBeenCalled();
  });

  it('keeps a refused object out of the index and says why', async () => {
    const { harness, seo, objectUrl } = await setup(() =>
      Promise.reject(new HttpErrorResponse({ status: 404, error: { error: 'Not found' } })));
    const c = await harness.navigateByUrl('/po/song/songs%2F9', SongPage);
    await harness.fixture.whenStable();

    expect(c.page.item()).toBeNull();
    expect(c.page.errorMessage()).toBe('Not found');
    expect(seo.noIndex).toHaveBeenCalled();
    expect(seo.forObject).not.toHaveBeenCalled();
    expect(objectUrl.apply).not.toHaveBeenCalled();
  });

  it('resets the SEO tags when the page goes away', async () => {
    const { harness, seo } = await setup(() => Promise.resolve(song));
    await harness.navigateByUrl('/po/song/songs%2F1', SongPage);
    await harness.fixture.whenStable();
    await harness.navigateByUrl('/');

    expect(seo.reset).toHaveBeenCalled();
  });

  it('publishes nothing from a load overtaken by a move to another object', async () => {
    let answerFirst!: (po: PersistentObject) => void;
    const second = { ...song, id: 'songs/2', name: 'Second' } as PersistentObject;
    const get = vi.fn()
      .mockImplementationOnce(() => new Promise<PersistentObject>(resolve => { answerFirst = resolve; }))
      .mockImplementationOnce(() => Promise.resolve(second));
    const { harness } = await setup(get);
    // The first load never settles on its own, so the harness must not wait for the app to be stable.
    void harness.navigateByUrl('/po/song/songs%2F1');
    await vi.waitFor(() => expect(get).toHaveBeenCalledTimes(1));
    void harness.navigateByUrl('/po/song/songs%2F2');
    await vi.waitFor(() => expect(get).toHaveBeenCalledTimes(2));
    const c = harness.routeDebugElement!.componentInstance as SongPage;
    await vi.waitFor(() => expect(c.page.item()?.id).toBe('songs/2'));

    answerFirst(song);
    await harness.fixture.whenStable();

    expect(c.page.item()?.id).toBe('songs/2');
  });
});
