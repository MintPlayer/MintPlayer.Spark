import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, input, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { withShareRoutes } from '@mintplayer/ng-spark/share';
import { SparkSharedObjectComponent } from './spark-shared-object.component';
import { translated } from '@mintplayer/ng-spark/models';

@Component({ selector: 'spec-score', standalone: true, template: '<b class="spec-score">score {{ value() }}</b>' })
class SpecScoreComponent {
  value = input<unknown>();
}

@Component({ selector: 'spec-rows', standalone: true, template: '<i class="spec-rows">{{ count() }} rows</i>' })
class SpecRowsComponent {
  value = input<unknown>();
  count = () => (Array.isArray(this.value()) ? (this.value() as unknown[]).length : 'no');
}

const loaded = {
  id: 'Sites/1',
  type: 'Site',
  attributes: [
    { id: 'a2', name: 'Score', label: translated({ en: 'Score' }), value: 87, dataType: 'number', order: 2, renderer: 'spec-score', isRequired: false, isReadOnly: true, rules: [] },
    { id: 'a1', name: 'Host', label: translated({ en: 'Host' }), value: 'example.com', dataType: 'string', order: 1, isRequired: false, isReadOnly: true, rules: [] },
  ],
  queries: {
    Runs: {
      columns: [{ name: 'Started', label: translated({ en: 'Started' }), dataType: 'string', order: 1 }],
      items: [{ id: 'Runs/1', values: [{ key: 'Started', value: 'monday' }] }, { id: 'Runs/2', values: [{ key: 'Started', value: 'tuesday' }] }],
      totalItems: 3,
      skip: 0,
      take: 2,
    },
  },
};

function render(post: (url: string, body: any) => Observable<unknown>, params = { type: 'Site', token: 'tok' }, id: string | null = 'Sites/1') {
  const postSpy = vi.fn(post);
  TestBed.configureTestingModule({
    imports: [SparkSharedObjectComponent],
    providers: [
      provideRouter([]),
      provideNoopAnimations(),
      { provide: HttpClient, useValue: { post: postSpy } },
      { provide: SparkService, useValue: {} },
      { provide: SparkLanguageService, useValue: { t: (key: string, fallback?: string) => `${key}|${fallback}`, language: signal('en') } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [{ name: 'spec-score', columnComponent: SpecScoreComponent, detailComponent: SpecScoreComponent }, { name: 'spec-rows', columnComponent: SpecRowsComponent }] },
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { paramMap: convertToParamMap(params), queryParamMap: convertToParamMap(id ? { id } : {}) } },
      },
    ],
  });
  const fixture = TestBed.createComponent(SparkSharedObjectComponent);
  return { fixture, post: postSpy };
}

async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
}

describe('SparkSharedObjectComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('loads with the link\'s type, id and token, anonymously', async () => {
    const { fixture, post } = render(() => of(loaded));
    await settle(fixture);
    expect(post).toHaveBeenCalledWith('/spark/share/load', { type: 'Site', id: 'Sites/1', token: 'tok' });
  });

  it('renders exactly the attributes the server returned, in order, through their renderers', async () => {
    const { fixture } = render(() => of(loaded));
    await settle(fixture);
    const element: HTMLElement = fixture.nativeElement;

    const names = Array.from(element.querySelectorAll('.spark-shared-attributes dd')).map(d => d.getAttribute('data-attribute'));
    expect(names).toEqual(['Host', 'Score']);
    expect(element.querySelector('[data-attribute="Host"]')!.textContent!.trim()).toBe('example.com');
    expect(element.querySelector('[data-attribute="Score"] .spec-score')!.textContent!.trim()).toBe('score 87');
    expect(Array.from(element.querySelectorAll('.spark-shared-attributes dt')).map(d => d.textContent!.trim())).toEqual(['Host', 'Score']);
    // ng-bootstrap's grid: Bootstrap's .row and .col-* rules ship with <bs-grid>, not globally (D84).
    expect(element.querySelector('bs-grid dl.row.spark-shared-attributes > dt.col-sm-4 + dd.col-sm-8')).not.toBeNull();
  });

  // An AsDetail array travels in `objects`, not `value` (as on the detail page, rendererValue): the share page handed its
  // renderer undefined, so SeoTester's shared run showed "no results" for a run with 99 (M11).
  it('hands an AsDetail array attribute its rows, as the detail page does', async () => {
    const withRows = { ...loaded, attributes: [...loaded.attributes, {
      id: 'a3', name: 'Results', label: translated({ en: 'Results' }), dataType: 'AsDetail', isArray: true, order: 3, renderer: 'spec-rows',
      objects: [{ id: 'r1', name: 'Row', attributes: [] }, { id: 'r2', name: 'Row', attributes: [] }], isRequired: false, isReadOnly: true, rules: [],
    }] };
    const { fixture } = render(() => of(withRows));
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-attribute="Results"] .spec-rows')!.textContent!.trim()).toBe('2 rows');
  });

  it('renders one read-only table per child query', async () => {
    const { fixture } = render(() => of(loaded));
    await settle(fixture);
    const element: HTMLElement = fixture.nativeElement;

    const sections = element.querySelectorAll('.spark-shared-query');
    expect(sections.length).toBe(1);
    expect(Array.from(sections[0].querySelectorAll('tbody tr')).map(r => r.getAttribute('data-id'))).toEqual(['Runs/1', 'Runs/2']);
    expect(sections[0].querySelector('.spark-shared-range')!.textContent!.trim()).toBe('1–2 / 3');
    expect(element.querySelector('a')).toBeNull();
    // ng-bootstrap's table: the .table rules ship with <bs-table>, not globally (D84).
    expect(sections[0].querySelector('bs-table table.table tbody tr[data-id="Runs/1"]')).not.toBeNull();
  });

  it('heads the object with its type label and each table with its query label', async () => {
    const { fixture } = render(() => of({ ...loaded, label: translated({ en: 'Website', nl: 'Website (nl)' }), queryLabels: { Runs: translated({ en: 'Audit runs' }) } }));
    await settle(fixture);
    const element: HTMLElement = fixture.nativeElement;

    expect(element.querySelector('.spark-shared-title')!.textContent!.trim()).toBe('Website');
    expect(element.querySelector('[data-query="Runs"] h2')!.textContent!.trim()).toBe('Audit runs');
  });

  it('falls back to the type and query names when the server sends no labels', async () => {
    const { fixture } = render(() => of(loaded));
    await settle(fixture);
    const element: HTMLElement = fixture.nativeElement;

    expect(element.querySelector('.spark-shared-title')!.textContent!.trim()).toBe('Site');
    expect(element.querySelector('[data-query="Runs"] h2')!.textContent!.trim()).toBe('Runs');
  });

  it('pages a child query through /share/query', async () => {
    const { fixture, post } = render((url) => url.endsWith('/load')
      ? of(loaded)
      : of({ ...loaded.queries.Runs, items: [{ id: 'Runs/3', values: [{ key: 'Started', value: 'wednesday' }] }], skip: 2 }));
    await settle(fixture);

    (fixture.nativeElement.querySelector('.spark-shared-next') as HTMLButtonElement).click();
    await settle(fixture);

    expect(post).toHaveBeenLastCalledWith('/spark/share/query', { type: 'Site', id: 'Sites/1', token: 'tok', query: 'Runs', skip: 2, take: 2 });
    const rows = Array.from(fixture.nativeElement.querySelectorAll('.spark-shared-query tbody tr')) as HTMLElement[];
    expect(rows.map(r => r.getAttribute('data-id'))).toEqual(['Runs/3']);
    expect(fixture.nativeElement.querySelector('.spark-shared-range').textContent.trim()).toBe('3–3 / 3');
    expect((fixture.nativeElement.querySelector('.spark-shared-next') as HTMLButtonElement).disabled).toBe(true);
    expect((fixture.nativeElement.querySelector('.spark-shared-previous') as HTMLButtonElement).disabled).toBe(false);
  });

  it('shows the translated "link does not work" for a 404, and nothing of the object', async () => {
    const { fixture } = render(() => throwError(() => new HttpErrorResponse({ status: 404, error: { error: 'Not found' } })));
    await settle(fixture);
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.spark-shared-not-found')!.textContent!.trim()).toBe('share.notFound|This link does not work (any more).');
    expect(element.querySelector('.spark-shared-attributes')).toBeNull();
  });

  it('reads a link without an id as not working, without asking the server', async () => {
    const { fixture, post } = render(() => of(loaded), { type: 'Site', token: 'tok' }, null);
    await settle(fixture);
    expect(post).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.spark-shared-not-found')).not.toBeNull();
  });

  it('keeps the page and says so when paging is rate-limited', async () => {
    const { fixture } = render((url) => url.endsWith('/load') ? of(loaded) : throwError(() => new HttpErrorResponse({ status: 429 })));
    await settle(fixture);
    (fixture.nativeElement.querySelector('.spark-shared-next') as HTMLButtonElement).click();
    await settle(fixture);
    expect(fixture.nativeElement.querySelector('.spark-shared-notice').textContent).toContain('share.rateLimited');
    expect(fixture.nativeElement.querySelectorAll('.spark-shared-query tbody tr').length).toBe(2);
  });
});

describe('withShareRoutes', () => {
  it('routes shared/:type/:token by default, without a guard, to the shared page', async () => {
    const [route] = withShareRoutes();
    expect(route.path).toBe('shared/:type/:token');
    expect(route.canActivate).toBeUndefined();
    expect(await (route.loadComponent as () => Promise<unknown>)()).toBe(SparkSharedObjectComponent);
  });

  it('takes another path and page', async () => {
    const [route] = withShareRoutes({ path: 'public/:type/:token', component: SpecScoreComponent });
    expect(route.path).toBe('public/:type/:token');
    expect(await (route.loadComponent as () => Promise<unknown>)()).toBe(SpecScoreComponent);
  });
});
