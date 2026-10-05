import { Component, ComponentRef } from '@angular/core';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi, beforeEach } from 'vitest';

import { By } from '@angular/platform-browser';
import { SparkQueryCardComponent } from './spark-query-card.component';
import { SparkSearchBoxComponent } from './spark-search-box.component';
import {
  SparkQueryActionsDirective,
  SparkQueryCaptionDirective,
  SparkQueryIconDirective,
} from './spark-query-slots';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityType, ShowedOn } from '@mintplayer/ng-spark/models';
import { settle } from '../../src/test-utils';

/**
 * The card's contract in one sentence: a slot the host does not supply renders the default.
 *
 * That is the whole reason this design works for the auto-rendered sub-query, where nobody
 * projects anything at all — so the "absent" half of each pair matters as much as the "supplied"
 * half, and both are asserted for every slot.
 */

const personType: EntityType = {
  id: 't-person',
  name: 'Person',
  alias: 'person',
  clrType: 'Test.Person',
  attributes: [
    {
      id: 'a-first', name: 'FirstName', dataType: 'string',
      isReadOnly: false, isRequired: false,
      order: 1, showedOn: ShowedOn.Query | ShowedOn.PersistentObject,
    } as any,
  ],
} as any;

const carsQuery = {
  id: 'q-cars', name: 'GetCars', source: 'Database.People', alias: 'cars',
  entityType: 'Person', sortColumns: [], isStreamingQuery: false,
  label: { en: 'All the cars' },
} as any;

const exportAction = {
  name: 'Export', label: { en: 'Export' }, offset: 0,
  showedOn: 'query', selectionRule: undefined, refreshOnCompleted: false,
} as any;

const langStub = { t: (k: string) => k, resolve: (v: any) => (typeof v === 'string' ? v : v?.en ?? '') };

function configure(actions: unknown[] = []) {
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    getQueries: vi.fn().mockResolvedValue([carsQuery]),
    getQuery: vi.fn().mockResolvedValue(carsQuery),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getCustomActions: vi.fn().mockResolvedValue(actions),
    executeQuery: vi.fn().mockResolvedValue({ columns: [], items: [], totalItems: 0, skip: 0, take: 50 }),
    executeCustomAction: vi.fn().mockResolvedValue(undefined),
    getLookupReference: vi.fn().mockResolvedValue({ values: [] }),
  };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: langStub },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });
  return service;
}

/** A card with no host: exactly the shape `spark-po-detail` auto-renders. */
async function bare(actions: unknown[] = []) {
  const service = configure(actions);
  const fixture = TestBed.createComponent(SparkQueryCardComponent);
  fixture.componentRef.setInput('queryId', 'q-cars');
  fixture.detectChanges();
  await settle(fixture);
  return { fixture, service, text: () => fixture.nativeElement.textContent as string };
}

async function hosted<T>(host: new (...a: any[]) => T, actions: unknown[] = []) {
  const service = configure(actions);
  const fixture = TestBed.createComponent(host);
  fixture.detectChanges();
  await settle(fixture);
  return { fixture, service, text: () => fixture.nativeElement.textContent as string };
}

describe('SparkQueryCardComponent', () => {
  beforeEach(() => TestBed.resetTestingModule());

  describe('with no slots supplied — the auto-rendered case', () => {
    it('renders the query description as the caption', async () => {
      const { text } = await bare();

      expect(text()).toContain('All the cars');
    });

    it('renders the actions the server declares', async () => {
      const { text } = await bare([exportAction]);

      expect(text()).toContain('Export');
    });

    it('labels the overflow with the translated "more", as the list and detail pages do', async () => {
      const { fixture } = await bare([exportAction]);

      const label = fixture.nativeElement.querySelector('bs-priority-nav .priority-nav-more-label');
      expect(label?.textContent?.trim()).toBe('common.more');
    });

    it('renders the header action buttons with square corners', async () => {
      const { fixture } = await bare([exportAction]);

      const buttons = Array.from(fixture.nativeElement.querySelectorAll('bs-card-header button[data-action]')) as HTMLElement[];
      expect(buttons.length).toBeGreaterThan(0);
      for (const b of buttons) expect(b.classList).toContain('rounded-0');
    });

    it('renders no action bar when the type declares none', async () => {
      const { fixture } = await bare([]);

      expect(fixture.nativeElement.querySelector('bs-priority-nav')).toBeNull();
    });
  });

  describe('caption slot', () => {
    @Component({
      standalone: true,
      imports: [SparkQueryCardComponent, SparkQueryCaptionDirective],
      template: `<spark-query-card queryId="q-cars">
        <span *sparkQueryCaption="let caption">custom:{{ caption }}</span>
      </spark-query-card>`,
    })
    class Host {}

    it('replaces the default caption and receives it as context', async () => {
      const { text } = await hosted(Host);

      expect(text()).toContain('custom:All the cars');
    });
  });

  describe('icon slot', () => {
    @Component({
      standalone: true,
      imports: [SparkQueryCardComponent, SparkQueryIconDirective],
      template: `<spark-query-card queryId="q-cars">
        <span *sparkQueryIcon>ICON</span>
      </spark-query-card>`,
    })
    class Host {}

    it('renders the icon and leaves the other slots at their defaults', async () => {
      const { text } = await hosted(Host, [exportAction]);

      expect(text()).toContain('ICON');
      expect(text()).toContain('All the cars');
      expect(text()).toContain('Export');
    });
  });

  describe('actions slot', () => {
    @Component({
      standalone: true,
      imports: [SparkQueryCardComponent, SparkQueryActionsDirective],
      template: `<spark-query-card queryId="q-cars">
        <ng-container *sparkQueryActions="let actions">
          <button>Mine</button>
          <span>server:{{ actions.length }}</span>
        </ng-container>
      </spark-query-card>`,
    })
    class Host {}

    /**
     * Replacing, not appending — and the context is what makes that safe. Without the server's
     * actions in hand, a host adding one button would silently drop every action the type
     * declares, and those are the ones carrying `selectionRule` and the permission filter.
     */
    it('replaces the default bar but hands the server actions to the template', async () => {
      const { text } = await hosted(Host, [exportAction]);

      expect(text()).toContain('Mine');
      expect(text()).toContain('server:1');
      expect(text()).not.toContain('Export');
    });
  });

  describe('targeting', () => {
    @Component({
      standalone: true,
      imports: [SparkQueryCardComponent, SparkQueryIconDirective],
      template: `<spark-query-card queryId="q-cars">
        <span *sparkQueryIcon="'cars'">TARGETED</span>
        <span *sparkQueryIcon>FALLBACK</span>
      </spark-query-card>`,
    })
    class MatchingHost {}

    @Component({
      standalone: true,
      imports: [SparkQueryCardComponent, SparkQueryIconDirective],
      template: `<spark-query-card queryId="q-cars">
        <span *sparkQueryIcon="'other'">TARGETED</span>
        <span *sparkQueryIcon>FALLBACK</span>
      </spark-query-card>`,
    })
    class NonMatchingHost {}

    it('a slot targeting this query wins over the catch-all', async () => {
      const { text } = await hosted(MatchingHost);

      expect(text()).toContain('TARGETED');
      expect(text()).not.toContain('FALLBACK');
    });

    it('a slot targeting another query leaves the catch-all in place', async () => {
      const { text } = await hosted(NonMatchingHost);

      expect(text()).toContain('FALLBACK');
      expect(text()).not.toContain('TARGETED');
    });
  });

  describe('forwarded templates — the path spark-po-detail uses', () => {
    @Component({
      standalone: true,
      imports: [SparkQueryCardComponent],
      template: `
        <ng-template #icon>FORWARDED</ng-template>
        <spark-query-card queryId="q-cars" [iconTemplate]="icon" />`,
    })
    class Host {}

    /**
     * A structural directive cannot cross a component boundary, and `spark-po-detail` is created
     * by the router — there is no tag to project into. Its `TemplateRef` can cross.
     */
    it('renders a slot handed in as an input', async () => {
      const { text } = await hosted(Host);

      expect(text()).toContain('FORWARDED');
    });
  });
  describe('header layout', () => {
    /** Where each element sits in the header, left to right. */
    function headerOrder(fixture: ComponentFixture<unknown>): string[] {
      const header = (fixture.nativeElement as HTMLElement).querySelector('bs-card-header div')!;
      return Array.from(header.children).map(el => {
        switch (el.tagName.toLowerCase()) {
          case 'bs-priority-nav': return 'actions';
          case 'spark-search-box': return 'search';
          default: return 'caption';
        }
      });
    }

    function caption(fixture: ComponentFixture<unknown>): HTMLElement {
      return (fixture.nativeElement as HTMLElement).querySelector('.spark-query-card-caption')!;
    }

    // #460 M15 moved the caption to the leading edge, as query-grid-card-PRD.md always specified and
    // Vidyano's sub-query tabs have it; these cases asserted the old actions-first order.
    it('renders the caption, then the actions, then the search box', async () => {
      const { fixture } = await bare([exportAction]);

      expect(headerOrder(fixture)).toEqual(['caption', 'actions', 'search']);
    });

    it('pushes the actions and the search to the trailing edge', async () => {
      const { fixture } = await bare([exportAction]);

      expect(caption(fixture).classList.contains('me-auto')).toBe(true);
      expect(caption(fixture).classList.contains('ms-auto')).toBe(false);
    });

    it('keeps the search box on a card without actions', async () => {
      const { fixture } = await bare();

      expect(headerOrder(fixture)).toEqual(['caption', 'search']);
      expect(caption(fixture).classList.contains('me-auto')).toBe(true);
    });
  });

  describe('search box (#460 M15, D17 addendum)', () => {
    function searchBox(fixture: ComponentFixture<unknown>): SparkSearchBoxComponent {
      return fixture.debugElement.query(By.directive(SparkSearchBoxComponent)).componentInstance;
    }

    function lastExecute(service: any): any {
      return service.executeQuery.mock.calls.at(-1)?.[1];
    }

    it('sends the typed term to the grid, together with the parent', async () => {
      const { fixture, service } = await bare();
      fixture.componentRef.setInput('parentId', 'companies/1');
      fixture.componentRef.setInput('parentType', 'Company');
      await settle(fixture);

      searchBox(fixture).term.set('ali');
      await settle(fixture);

      expect(lastExecute(service)).toEqual(expect.objectContaining({
        search: 'ali', parentId: 'companies/1', parentType: 'Company',
      }));
    });

    it('sends parentDeleted: include when its parent is deleted, and leaves the rows\' deleted alone (#460)', async () => {
      const { fixture, service } = await bare();
      fixture.componentRef.setInput('parentId', 'companies/1');
      fixture.componentRef.setInput('parentType', 'Company');
      fixture.componentRef.setInput('parentDeleted', true);
      await settle(fixture);

      const body = lastExecute(service);
      expect(body).toEqual(expect.objectContaining({
        parentId: 'companies/1', parentType: 'Company', parentDeleted: 'include',
      }));
      expect(body).not.toHaveProperty('deleted');
    });

    it('sends no parentDeleted under a live parent', async () => {
      const { fixture, service } = await bare();
      fixture.componentRef.setInput('parentId', 'companies/1');
      fixture.componentRef.setInput('parentType', 'Company');
      await settle(fixture);

      expect(lastExecute(service)).toEqual(expect.objectContaining({ parentId: 'companies/1' }));
      expect(lastExecute(service)).not.toHaveProperty('parentDeleted');
    });

    it('clearing the box searches for nothing again', async () => {
      const { fixture, service } = await bare();
      searchBox(fixture).term.set('ali');
      await settle(fixture);

      searchBox(fixture).clear();
      await settle(fixture);

      expect(lastExecute(service).search).toBeUndefined();
    });

    it('starts from the host search input', async () => {
      const service = configure();
      const fixture = TestBed.createComponent(SparkQueryCardComponent);
      fixture.componentRef.setInput('queryId', 'q-cars');
      fixture.componentRef.setInput('search', 'bob');
      fixture.detectChanges();
      await settle(fixture);

      expect(searchBox(fixture).term()).toBe('bob');
      expect(lastExecute(service).search).toBe('bob');
    });

    it('is absent when the host opts out', async () => {
      configure();
      const fixture = TestBed.createComponent(SparkQueryCardComponent);
      fixture.componentRef.setInput('queryId', 'q-cars');
      fixture.componentRef.setInput('searchable', false);
      fixture.detectChanges();
      await settle(fixture);

      expect(fixture.nativeElement.querySelector('spark-search-box')).toBeNull();
    });

    it('is absent over bound data, which the grid never refetches', async () => {
      configure();
      const fixture = TestBed.createComponent(SparkQueryCardComponent);
      fixture.componentRef.setInput('queryId', 'q-cars');
      fixture.componentRef.setInput('data', []);
      fixture.detectChanges();
      await settle(fixture);

      expect(fixture.nativeElement.querySelector('spark-search-box')).toBeNull();
    });
  });

});
