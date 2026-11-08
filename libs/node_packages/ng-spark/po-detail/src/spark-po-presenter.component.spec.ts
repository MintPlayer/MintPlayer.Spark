import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityType, PersistentObject, ShowedOn } from '@mintplayer/ng-spark/models';
import { SparkPoPresenterComponent } from './spark-po-presenter.component';

const songType = {
  id: 't-song', name: 'Song', alias: 'song', clrType: 'Test.Song',
  attributes: [
    { id: 'a-title', name: 'Title', label: { en: 'Title' }, dataType: 'string', order: 1, showedOn: ShowedOn.PersistentObject },
    { id: 'a-genre', name: 'Genre', dataType: 'string', lookupReferenceType: 'Genres', order: 2, showedOn: ShowedOn.PersistentObject },
    { id: 'a-thumb', name: 'Thumbnail', dataType: 'string', order: 3, showedOn: 'None' },
  ],
} as unknown as EntityType;

const song = {
  id: 'songs/1', objectTypeId: 't-song',
  attributes: [
    { id: 'a-title', name: 'Title', value: 'Lift' },
    { id: 'a-genre', name: 'Genre', value: 'pop' },
    { id: 'a-thumb', name: 'Thumbnail', value: 'https://i.ytimg.com/vi/x/hqdefault.jpg' },
  ],
} as unknown as PersistentObject;

function mount() {
  const getLookupReference = vi.fn().mockResolvedValue({ name: 'Genres', values: [{ key: 'pop', values: { en: 'Pop' }, isActive: true }] });
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SparkService, useValue: { getLookupReference, executeQueryByName: vi.fn() } },
      { provide: SparkLanguageService, useValue: { language: signal('en'), defaultLanguage: signal('en'), languages: signal({}), t: (k: string) => k } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });
  const fixture = TestBed.createComponent(SparkPoPresenterComponent);
  fixture.componentRef.setInput('entityType', songType);
  fixture.componentRef.setInput('item', song);
  return { fixture, getLookupReference };
}

describe('SparkPoPresenterComponent', () => {
  it("draws an object it is handed, without loading it, and only the attributes its detail page shows", async () => {
    const { fixture } = mount();
    await fixture.whenStable();

    expect(fixture.componentInstance.visibleAttributes().map(a => a.name)).toEqual(['Title', 'Genre']);
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Lift');
    // The lookup label, not the stored key: the presenter loads what labels its attributes.
    expect(text).toContain('Pop');
    expect(text).not.toContain('hqdefault');
  });

  it('asks for the labels once per object, not again when the same object is replaced (a live push)', async () => {
    const { fixture, getLookupReference } = mount();
    await fixture.whenStable();
    fixture.componentRef.setInput('item', { ...song, attributes: [...song.attributes] });
    await fixture.whenStable();

    expect(getLookupReference).toHaveBeenCalledTimes(1);
  });

  it('keeps its labels through a reload, which hands it a new type and catalogue with the same content', async () => {
    const { fixture, getLookupReference } = mount();
    fixture.componentRef.setInput('allEntityTypes', [songType]);
    await fixture.whenStable();
    const labels = fixture.componentInstance.lookupReferenceOptions();

    // What sparkObjectPage publishes on every load: the catalogue is fetched afresh, so every object is new.
    const reloadedType = structuredClone(songType);
    fixture.componentRef.setInput('entityType', reloadedType);
    fixture.componentRef.setInput('allEntityTypes', [reloadedType]);
    fixture.componentRef.setInput('item', structuredClone(song));
    fixture.detectChanges();

    // Not cleared, not even for a moment: clearing redraws every AsDetail cell and loses a click on its links.
    expect(fixture.componentInstance.lookupReferenceOptions()).toBe(labels);
    await fixture.whenStable();
    expect(getLookupReference).toHaveBeenCalledTimes(1);
  });

  it('asks again for another object', async () => {
    const { fixture, getLookupReference } = mount();
    await fixture.whenStable();
    fixture.componentRef.setInput('item', { ...song, id: 'songs/2' });
    await fixture.whenStable();

    expect(getLookupReference).toHaveBeenCalledTimes(2);
  });
});
