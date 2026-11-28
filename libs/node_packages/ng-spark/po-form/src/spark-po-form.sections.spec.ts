import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BsModalComponent } from '@mintplayer/ng-bootstrap/modal';
import { BsTabControlComponent } from '@mintplayer/ng-bootstrap/tab-control';
import { describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityType, SPARK_REVEAL_EVENT, ShowedOn } from '@mintplayer/ng-spark/models';
import { SparkPoFormComponent } from './spark-po-form.component';

/**
 * #721 (`docs/section_fragment_links_PRD.md`): the edit form carries the same section ids as the detail page, so a
 * `#fragment` link to an edit page reaches its group cards and tabs. None inside a modal (Dec4), none on the default tab
 * (Dec3), and a `spark-reveal` event from a section opens the tab that holds it.
 */
const personType = {
  id: 't-person', name: 'Person', alias: 'person', clrType: 'Test.Person',
  tabs: [{ id: 't-emp', name: 'Employment', anchor: 'employment', order: 1 }],
  groups: [
    { id: 'g-contact', name: 'Contact', anchor: 'contact', order: 0, label: { en: 'Contact' } },
    { id: 'g-career', name: 'Career', anchor: 'career', tab: 't-emp', order: 0, label: { en: 'Career' } },
  ],
  attributes: [
    { id: 'a-name', name: 'Name', dataType: 'string', order: 1, isRequired: false, isReadOnly: false, rules: [], showedOn: ShowedOn.PersistentObject },
    { id: 'a-phone', name: 'Phone', dataType: 'string', order: 2, group: 'g-contact', isRequired: false, isReadOnly: false, rules: [], showedOn: ShowedOn.PersistentObject },
    { id: 'a-employer', name: 'Employer', dataType: 'string', order: 3, group: 'g-career', isRequired: false, isReadOnly: false, rules: [], showedOn: ShowedOn.PersistentObject },
  ],
} as unknown as EntityType;

async function mount(options: { inModal?: boolean } = {}) {
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      {
        provide: SparkService,
        useValue: {
          executeQueryByName: vi.fn().mockResolvedValue({ columns: [], items: [], totalItems: 0 }),
          getEntityTypes: vi.fn().mockResolvedValue([personType]),
          getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
          getLookupReference: vi.fn(),
        },
      },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
      ...(options.inModal ? [{ provide: BsModalComponent, useValue: {} }] : []),
    ],
  });
  const fixture = TestBed.createComponent(SparkPoFormComponent);
  fixture.componentRef.setInput('entityType', personType);
  fixture.componentInstance.formData.set({ Name: 'Ada', Phone: '555', Employer: 'Analytical' });
  await fixture.whenStable();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('SparkPoFormComponent section ids (#721)', () => {
  it('gives each group card and tab its anchor, the default tab none', async () => {
    const { host } = await mount();

    expect(host.querySelector('#contact')?.tagName.toLowerCase()).toBe('bs-card');
    expect(host.querySelector('#career')?.tagName.toLowerCase()).toBe('bs-card');
    expect(host.querySelector('#employment')?.getAttribute('data-spark-tab')).toBe('t-emp');
    expect(host.querySelector('[data-spark-tab="__default__"]')?.hasAttribute('id')).toBe(false);
  });

  it('renders no ids inside a modal', async () => {
    const { host } = await mount({ inModal: true });

    expect(host.querySelector('#contact, #career, #employment')).toBeNull();
    expect(host.querySelector('[data-spark-tab="t-emp"]')).not.toBeNull();
  });

  it('opens the tab holding a section that asks to be revealed', async () => {
    const { fixture, host } = await mount();
    const tabControl = fixture.debugElement.query(By.directive(BsTabControlComponent)).componentInstance as BsTabControlComponent;

    host.querySelector('#career')!.dispatchEvent(new CustomEvent(SPARK_REVEAL_EVENT, { bubbles: true }));
    await fixture.whenStable();

    expect(tabControl.activeTab()).toBe('t-emp');
  });
});
