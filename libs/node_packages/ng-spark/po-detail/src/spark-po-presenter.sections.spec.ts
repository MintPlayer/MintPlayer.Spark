import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BsModalComponent } from '@mintplayer/ng-bootstrap/modal';
import { BsTabControlComponent } from '@mintplayer/ng-bootstrap/tab-control';
import { describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityType, PersistentObject, SPARK_REVEAL_EVENT, ShowedOn } from '@mintplayer/ng-spark/models';
import { SparkPoPresenterComponent } from './spark-po-presenter.component';

/**
 * #721 (`docs/section_fragment_links_PRD.md`): the detail page's group cards and tabs carry the section id the server
 * computed (`anchor`), so a `#fragment` link can reach them. The default tab and the ungrouped card have none (Dec3), nothing
 * inside a modal has one (Dec4), and a `spark-reveal` event from a section opens the tab that holds it.
 */
const personType = {
  id: 't-person', name: 'Person', alias: 'person', clrType: 'Test.Person',
  tabs: [{ id: 't-emp', name: 'Employment', anchor: 'employment', order: 1 }],
  groups: [
    { id: 'g-contact', name: 'Contact', anchor: 'contact', order: 0, label: { en: 'Contact' } },
    { id: 'g-career', name: 'Career', anchor: 'career', tab: 't-emp', order: 0, label: { en: 'Career' } },
  ],
  attributes: [
    { id: 'a-name', name: 'Name', dataType: 'string', order: 1, showedOn: ShowedOn.PersistentObject },
    { id: 'a-phone', name: 'Phone', dataType: 'string', order: 2, group: 'g-contact', showedOn: ShowedOn.PersistentObject },
    { id: 'a-employer', name: 'Employer', dataType: 'string', order: 3, group: 'g-career', showedOn: ShowedOn.PersistentObject },
  ],
} as unknown as EntityType;

const person = {
  id: 'people/1', objectTypeId: 't-person',
  attributes: [
    { id: 'a-name', name: 'Name', value: 'Ada' },
    { id: 'a-phone', name: 'Phone', value: '555' },
    { id: 'a-employer', name: 'Employer', value: 'Analytical' },
  ],
} as unknown as PersistentObject;

async function mount(options: { compact?: boolean; inModal?: boolean } = {}) {
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SparkService, useValue: { getLookupReference: vi.fn(), executeQueryByName: vi.fn() } },
      { provide: SparkLanguageService, useValue: { language: signal('en'), defaultLanguage: signal('en'), languages: signal({}), t: (k: string) => k } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
      // What a presenter inside <bs-modal> finds through its element injector (S5 proved that path on a real modal).
      ...(options.inModal ? [{ provide: BsModalComponent, useValue: {} }] : []),
    ],
  });
  const fixture = TestBed.createComponent(SparkPoPresenterComponent);
  fixture.componentRef.setInput('entityType', personType);
  fixture.componentRef.setInput('item', person);
  fixture.componentRef.setInput('compact', options.compact ?? false);
  await fixture.whenStable();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('SparkPoPresenterComponent section ids (#721)', () => {
  it('gives each group card and tab its anchor', async () => {
    const { host } = await mount();

    expect(host.querySelector('#contact')?.tagName.toLowerCase()).toBe('bs-card');
    expect(host.querySelector('#career')?.tagName.toLowerCase()).toBe('bs-card');
    const employment = host.querySelector('#employment');
    expect(employment).not.toBeNull();
    expect(employment!.getAttribute('data-spark-tab')).toBe('t-emp');
    expect(employment!.contains(host.querySelector('#career'))).toBe(true);
  });

  it('renders each id exactly once', async () => {
    const { host } = await mount();
    for (const id of ['contact', 'career', 'employment'])
      expect(host.querySelectorAll(`[id="${id}"]`)).toHaveLength(1);
  });

  it('gives the default tab and the ungrouped card no id', async () => {
    const { host } = await mount();

    const defaultTab = host.querySelector('[data-spark-tab="__default__"]');
    expect(defaultTab).not.toBeNull();
    expect(defaultTab!.hasAttribute('id')).toBe(false);
    const ungrouped = [...defaultTab!.querySelectorAll('bs-card')].find(card => card.textContent?.includes('Ada'));
    expect(ungrouped?.hasAttribute('id')).toBe(false);
    expect(host.querySelector('#algemeen, #general')).toBeNull();
  });

  it('renders no ids inside a modal', async () => {
    const { host } = await mount({ inModal: true });

    expect(host.querySelector('#contact, #career, #employment')).toBeNull();
    expect(host.querySelector('[data-spark-tab="t-emp"]')).not.toBeNull();
  });

  it('renders no ids in compact mode', async () => {
    const { host } = await mount({ compact: true });
    expect(host.querySelector('#contact, #career, #employment')).toBeNull();
  });

  it('opens the tab holding a section that asks to be revealed', async () => {
    const { fixture, host } = await mount();
    const tabControl = fixture.debugElement.query(By.directive(BsTabControlComponent)).componentInstance as BsTabControlComponent;

    host.querySelector('#career')!.dispatchEvent(new CustomEvent(SPARK_REVEAL_EVENT, { bubbles: true }));
    await fixture.whenStable();

    expect(tabControl.activeTab()).toBe('t-emp');
  });

  it('opens the default tab for a section on it', async () => {
    const { fixture, host } = await mount();
    const tabControl = fixture.debugElement.query(By.directive(BsTabControlComponent)).componentInstance as BsTabControlComponent;
    tabControl.activeTab.set('t-emp');

    host.querySelector('#contact')!.dispatchEvent(new CustomEvent(SPARK_REVEAL_EVENT, { bubbles: true }));
    await fixture.whenStable();

    expect(tabControl.activeTab()).toBe('__default__');
  });
});
