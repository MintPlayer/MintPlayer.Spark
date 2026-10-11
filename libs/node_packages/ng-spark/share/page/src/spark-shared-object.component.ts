import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkGridCellComponent } from '@mintplayer/ng-spark/grid';
import { PersistentObject, PersistentObjectAttribute, QueryColumn, QueryResult, QueryResultItem, TranslatedString, valueFor } from '@mintplayer/ng-spark/models';
import { QueryCellValuePipe, ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';
import { cellValue, rendererValue } from '@mintplayer/ng-spark/renderers';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

/** What `POST /spark/share/load` answers: the declared attributes and the first page of each declared child query. */
export interface SparkSharedObject {
  id: string;
  type: string;
  /** The type's label (the model's, translated); the page's heading. */
  label?: TranslatedString | null;
  attributes: PersistentObjectAttribute[];
  /** By query name. */
  queries: Record<string, QueryResult>;
  /** Each shown query's label, by query name; the tables' headings. */
  queryLabels?: Record<string, TranslatedString | null>;
}

/** English fallbacks, shown until (or where) the server's translations have none. */
const TEXTS: Record<string, string> = {
  notFound: 'This link does not work (any more).',
  rateLimited: 'Too many requests. Try again in a minute.',
  unavailable: 'This page could not be loaded. Try again later.',
};

type State = 'loading' | 'ready' | 'notFound' | 'failed';

/**
 * The read-only page of a share link (`docs/seo_analyzer_PRD.md` X6, D31), routed by `withShareRoutes()`:
 * exactly what `POST /spark/share/load` returned, the object's attributes through the same cells (and
 * custom renderers, such as the score renderers) as the rest of Spark, and one read-only table per
 * child query, paged through `POST /spark/share/query`. Every refusal reads as the one "this link does
 * not work" (`share.notFound`), since the server answers every refusal with the same 404. No signed-in
 * user, no links into the app, no assumptions about the surrounding chrome.
 */
@Component({
  selector: 'spark-shared-object',
  standalone: true,
  // Bootstrap's grid and table CSS are not global in ng-bootstrap (they ship with bs-grid / bs-table), so the
  // `row`/`col-sm-4`/`table` classes alone left the page unstyled and edge to edge (seen in SeoTester, M11).
  styles: [`
    .spark-shared-object { max-width: 72rem; margin: 0 auto; padding: 1.5rem 1rem; }
    .spark-shared-attributes { display: grid; grid-template-columns: minmax(8rem, max-content) minmax(0, 1fr); gap: .5rem 1.5rem; margin: 0; }
    .spark-shared-attributes dt { font-weight: 600; }
    .spark-shared-attributes dd { margin: 0; min-width: 0; }
    /* A phone stacks each label over its value: beside it, the label column took 40 % of 375 px. */
    @media (max-width: 575.98px) {
      .spark-shared-attributes { grid-template-columns: minmax(0, 1fr); gap: .15rem; }
      .spark-shared-attributes dd { margin-bottom: .6rem; }
    }
    .spark-shared-query { overflow-x: auto; }
    .spark-shared-query table { width: 100%; border-collapse: collapse; }
    .spark-shared-query th, .spark-shared-query td { padding: .35rem .5rem; border-bottom: 1px solid var(--bs-border-color); text-align: start; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BsAlertComponent, BsSpinnerComponent, SparkGridCellComponent, ResolveTranslationPipe],
  template: `
    <div class="spark-shared-object">
      @if (notice(); as text) {
        <bs-alert [type]="colors.warning" class="d-block mb-3 spark-shared-notice">{{ text }}</bs-alert>
      }
      @switch (state()) {
        @case ('loading') {
          <bs-spinner class="spark-shared-loading" />
        }
        @case ('notFound') {
          <bs-alert [type]="colors.danger" class="d-block spark-shared-not-found">{{ t('notFound') }}</bs-alert>
        }
        @case ('ready') {
          <h1 class="h4 mb-3 spark-shared-title">{{ shared()?.label ?? undefined | resolveTranslation:(shared()?.type ?? '') }}</h1>
          <dl class="row spark-shared-attributes">
            @for (cell of attributeCells(); track cell.attribute.name) {
              <dt class="col-sm-4">{{ cell.attribute.label | resolveTranslation:cell.attribute.name }}</dt>
              <dd class="col-sm-8" [attr.data-attribute]="cell.attribute.name">
                <spark-grid-cell [column]="cell.attribute" [display]="cell.display" [rendererValue]="cell.value" [item]="object()" />
              </dd>
            }
          </dl>
          @for (query of queryTables(); track query.name) {
            <section class="mt-4 spark-shared-query" [attr.data-query]="query.name">
              <h2 class="h5">{{ query.label | resolveTranslation:query.name }}</h2>
              <div class="table-responsive">
                <table class="table table-sm align-middle">
                  <thead>
                    <tr>
                      @for (column of query.columns; track column.name) {
                        <th scope="col">{{ column.label | resolveTranslation:column.name }}</th>
                      }
                    </tr>
                  </thead>
                  <tbody>
                    @for (item of query.result.items; track item.id) {
                      <tr [attr.data-id]="item.id">
                        @for (column of query.columns; track column.name) {
                          <td><spark-grid-cell [column]="column" [display]="display(column, item)" [rendererValue]="raw(item, column)" [item]="item" /></td>
                        }
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              @if (query.result.totalItems > query.result.take) {
                <nav class="d-flex align-items-center gap-2 spark-shared-pager">
                  <button type="button" class="btn btn-sm btn-outline-secondary spark-shared-previous" aria-label="Previous"
                          [disabled]="query.result.skip === 0 || busy()" (click)="page(query.name, query.result.skip - query.result.take)">&lsaquo;</button>
                  <span class="small spark-shared-range">{{ query.result.skip + 1 }}–{{ query.last }} / {{ query.result.totalItems }}</span>
                  <button type="button" class="btn btn-sm btn-outline-secondary spark-shared-next" aria-label="Next"
                          [disabled]="query.last >= query.result.totalItems || busy()" (click)="page(query.name, query.result.skip + query.result.take)">&rsaquo;</button>
                </nav>
              }
            </section>
          }
        }
      }
    </div>
  `,
})
export class SparkSharedObjectComponent {
  private readonly http = inject(HttpClient);
  private readonly route = inject(ActivatedRoute);
  private readonly language = inject(SparkLanguageService);
  private readonly baseUrl = `${inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark'}/share`;
  private readonly cellPipe = new QueryCellValuePipe();
  protected readonly colors = Color;

  private readonly type = this.route.snapshot.paramMap.get('type') ?? '';
  private readonly token = this.route.snapshot.paramMap.get('token') ?? '';
  private readonly id = this.route.snapshot.queryParamMap.get('id') ?? '';

  readonly state = signal<State>('loading');
  readonly shared = signal<SparkSharedObject | null>(null);
  /** The page shown of each child query, by name: the first from the load, then whatever was paged to. */
  readonly pages = signal<Record<string, QueryResult>>({});
  readonly busy = signal(false);
  /** A passing problem (rate limit, network) shown above the content, which stays. */
  readonly notice = signal<string | null>(null);

  /** The object in the shape renderers receive as `item`. */
  protected readonly object = computed(() => {
    const shared = this.shared();
    return shared ? ({ id: shared.id, name: shared.type, objectTypeId: shared.type, attributes: shared.attributes } as PersistentObject) : null;
  });

  protected readonly attributeCells = computed(() => {
    const object = this.object();
    if (!object) return [];
    return [...object.attributes]
      .sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
      .map(attribute => ({
        attribute,
        display: this.cellPipe.transform(attribute as unknown as QueryColumn, object as unknown as QueryResultItem, {}),
        // As the detail page: an AsDetail value travels in `object`/`objects`, not `value`.
        value: rendererValue(attribute),
      }));
  });

  protected readonly queryTables = computed(() =>
    Object.entries(this.pages()).map(([name, result]) => ({
      name,
      label: this.shared()?.queryLabels?.[name] ?? undefined,
      result,
      columns: [...(result.columns ?? [])].sort((a, b) => (a.order ?? 0) - (b.order ?? 0)),
      last: Math.min(result.skip + result.take, result.totalItems),
    })));

  constructor() {
    void this.load();
  }

  protected t(key: string): string {
    return this.language.t('share.' + key, TEXTS[key]);
  }

  protected display(column: QueryColumn, item: QueryResultItem): unknown {
    return this.cellPipe.transform(column, item, {});
  }

  protected raw(item: QueryResultItem, column: QueryColumn): unknown {
    return cellValue(valueFor(item, column.name));
  }

  async load(): Promise<void> {
    if (!this.type || !this.token || !this.id) {
      this.state.set('notFound');
      return;
    }
    this.state.set('loading');
    try {
      const shared = await this.post<SparkSharedObject>('load', {});
      this.shared.set({ ...shared, attributes: shared.attributes ?? [], queries: shared.queries ?? {} });
      this.pages.set({ ...(shared.queries ?? {}) });
      this.notice.set(null);
      this.state.set('ready');
    } catch (error) {
      this.fail(error);
    }
  }

  /** Shows the page of `query` that starts at `skip`, the same size as the one shown. */
  async page(query: string, skip: number): Promise<void> {
    const current = this.pages()[query];
    if (!current || skip < 0 || this.busy()) return;
    this.busy.set(true);
    try {
      const result = await this.post<QueryResult>('query', { query, skip, take: current.take });
      this.pages.update(pages => ({ ...pages, [query]: result }));
      this.notice.set(null);
    } catch (error) {
      this.fail(error);
    } finally {
      this.busy.set(false);
    }
  }

  private post<T>(path: 'load' | 'query', extra: Record<string, unknown>): Promise<T> {
    return firstValueFrom(this.http.post<T>(`${this.baseUrl}/${path}`, { type: this.type, id: this.id, token: this.token, ...extra }));
  }

  private fail(error: unknown): void {
    const status = error instanceof HttpErrorResponse ? error.status : 0;
    if (status === 404) {
      // Revoked, replaced, switched off or never valid: the server does not say which, nor does the page.
      this.shared.set(null);
      this.pages.set({});
      this.notice.set(null);
      this.state.set('notFound');
      return;
    }
    this.notice.set(this.t(status === 429 ? 'rateLimited' : 'unavailable'));
    if (this.state() === 'loading') this.state.set('failed');
  }
}
