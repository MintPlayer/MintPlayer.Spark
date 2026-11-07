import { ApplicationRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-modal emits synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityAttributeDefinition, EntityType, PersistentObject, ShowedOn } from '@mintplayer/ng-spark/models';
import { AddNewReferenceRequest, SparkPoNewModalComponent } from './spark-po-new-modal.component';
import { SparkPoNewModalService } from './spark-po-new-modal.service';

function attr(partial: Partial<EntityAttributeDefinition>): EntityAttributeDefinition {
  return { id: partial.name, dataType: 'string', isRequired: false, isReadOnly: false, order: 1, showedOn: ShowedOn.PersistentObject, rules: [], ...partial } as EntityAttributeDefinition;
}

const artist: EntityType = {
  id: 't-artist',
  name: 'Artist',
  clrType: 'Demo.Artist',
  attributes: [attr({ name: 'Name', order: 1 }), attr({ name: 'Code', order: 2, isReadOnly: true })],
} as EntityType;

const owner = { id: 'songs/1', name: 'Song', objectTypeId: 't-song', attributes: [] } as unknown as PersistentObject;

const request: AddNewReferenceRequest = { target: artist, ownerType: 't-song', referenceAttribute: 'Artist', parent: owner, searchTerm: 'Queen' };

function blank(): PersistentObject {
  return {
    id: null, name: 'Artist', objectTypeId: 't-artist',
    attributes: [{ name: 'Name', value: 'Queen' }, { name: 'Code', value: 'Q' }],
  } as unknown as PersistentObject;
}

const flush = async () => { for (let i = 0; i < 5; i++) await new Promise<void>((r) => setTimeout(r, 0)); };

function configure(overrides: Record<string, unknown> = {}) {
  const service = {
    getEntityTypes: vi.fn().mockResolvedValue([artist]),
    getEntityType: vi.fn().mockResolvedValue(artist),
    newObject: vi.fn().mockResolvedValue(blank()),
    create: vi.fn(),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    ...overrides,
  };
  TestBed.configureTestingModule({
    providers: [
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });
  return service;
}

async function open(overrides: Record<string, unknown> = {}, req: AddNewReferenceRequest = request) {
  const service = configure(overrides);
  const fixture = TestBed.createComponent(SparkPoNewModalComponent);
  fixture.componentRef.setInput('request', req);
  const closed: (PersistentObject | null)[] = [];
  fixture.componentInstance.closed.subscribe((r) => closed.push(r));
  fixture.detectChanges();
  await flush();
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, service, closed };
}

const badRequest = (errors: unknown) => new HttpErrorResponse({ status: 400, error: { errors } });

describe('SparkPoNewModalComponent', () => {
  afterEach(() => vi.restoreAllMocks());

  it('asks /po/new with the picker context and fills the form from what OnNewAsync returned', async () => {
    const { component, service } = await open();

    expect(service.getEntityType).toHaveBeenCalledWith('t-artist', 'new');
    expect(service.newObject).toHaveBeenCalledWith('t-artist', {
      parentType: 't-song', referenceAttribute: 'Artist', parent: owner, parameters: { searchTerm: 'Queen' },
    });
    expect(component.entityType()).toBe(artist);
    expect(component.formData()).toMatchObject({ Name: 'Queen' });
  });

  it('without a search term sends no parameters', async () => {
    const { service } = await open({}, { ...request, searchTerm: undefined });
    expect(service.newObject.mock.calls[0][1].parameters).toBeUndefined();
  });

  it('creates with only the writable attributes and closes with the created object, once', async () => {
    const created = { ...blank(), id: 'artists/1' } as PersistentObject;
    const { component, service, closed } = await open({ create: vi.fn().mockResolvedValue(created) });

    await component.save();
    component.cancel();

    const sent = service.create.mock.calls[0][1] as PersistentObject;
    expect(service.create.mock.calls[0][0]).toBe('t-artist');
    expect(sent.name).toBe('Artist');
    expect(sent.attributes.map((a) => a.name)).toEqual(['Name']);
    expect(closed).toEqual([created]);
    expect(component.isOpen()).toBe(false);
  });

  it('stays open when an interceptor created nothing, and shows the refusal\'s errors beside the form', async () => {
    const create = vi.fn()
      .mockResolvedValueOnce(null)
      .mockRejectedValueOnce(badRequest([
        { attributeName: 'Name', errorMessage: { en: 'Required' }, ruleType: 'error' },
        { attributeName: '', errorMessage: { en: 'Not now' }, ruleType: 'error' },
      ]))
      .mockRejectedValueOnce(new HttpErrorResponse({ status: 500, statusText: 'boom' }));
    const { fixture, component, closed } = await open({ create });

    await component.save();
    expect(component.cancelledByServer()).toBe(true);
    expect(component.isOpen()).toBe(true);

    await component.save();
    fixture.detectChanges();
    expect(component.cancelledByServer()).toBe(false);
    expect(component.validationErrors()).toHaveLength(2);
    expect(component.generalErrors().map((e) => e.errorMessage)).toEqual([{ en: 'Not now' }]);

    await component.save();
    expect(component.generalErrors()).toHaveLength(1);
    expect(component.generalErrors()[0].errorMessage.en).toContain('500');
    expect(component.isSaving()).toBe(false);
    expect(closed).toEqual([]);
  });

  it('a /po/new that fails shows its error and cannot be saved', async () => {
    const { component, service } = await open({ newObject: vi.fn().mockRejectedValue(new Error('offline')) });

    expect(component.loadFailed()).toBe(true);
    expect(component.generalErrors()[0].errorMessage).toEqual({ en: 'offline' });
    await component.save();
    expect(service.create).not.toHaveBeenCalled();
  });
});

describe('SparkPoNewModalService', () => {
  it('renders a popup per call and resolves with null when it is cancelled', async () => {
    configure();
    const modals = TestBed.inject(SparkPoNewModalService);
    const app = TestBed.inject(ApplicationRef);

    const result = modals.open(request);
    await flush();
    app.tick();
    const host = document.body.querySelector('spark-po-new-modal');
    expect(host).not.toBeNull();

    (document.body.querySelector('.spark-po-new-cancel') as HTMLButtonElement).click();
    await expect(result).resolves.toBeNull();
    expect(document.body.contains(host)).toBe(false);
  });
});

// Keeps the ComponentFixture type import used when the file is type-checked on its own.
export type _Fixture = ComponentFixture<SparkPoNewModalComponent>;
