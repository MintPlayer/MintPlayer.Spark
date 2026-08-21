import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked, Type } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings, type BsDatatableFetch } from '@mintplayer/ng-bootstrap/datatable';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SortColumn } from '@mintplayer/pagination';
import { SparkService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, AttributeValuePipe, ReferenceChipsPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { NgComponentOutlet } from '@angular/common';
import { SPARK_ATTRIBUTE_RENDERERS, rendererValue, withDeclaredInputs } from '@mintplayer/ng-spark/renderers';
import {
  EntityType,
  EntityAttributeDefinition,
  LookupReference,
  PersistentObject,
  SparkQuery,
  ShowedOn,
  hasShowedOnFlag,
} from '@mintplayer/ng-spark/models';

@Component({
  selector: 'spark-sub-query',
  imports: [CommonModule, NgComponentOutlet, RouterModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsSpinnerComponent, ResolveTranslationPipe, AttributeValuePipe, ReferenceChipsPipe, TranslateKeyPipe],
  templateUrl: './spark-sub-query.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkSubQueryComponent {
  private readonly sparkService = inject(SparkService);
  private readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);

  queryId = input.required<string>();

  /**
   * The parent persistent object this query is scoped to, when it has one.
   *
   * Optional, because not every query is a detail of something: a page can host
   * a grid that stands on its own — "my accounts", a dashboard list — and the
   * server already treats an absent parent as "no parent" rather than as an
   * error. Leaving these required made that shape impossible to express: the
   * component simply never loaded, with no request, no error and no log.
   *
   * Pass both or neither. One without the other is ignored, matching
   * `SparkService.executeQuery`, which omits either param when it is falsy, and
   * the execute endpoint, which resolves a parent only when both are present.
   */
  parentId = input<string>('');
  parentType = input<string>('');

  /**
   * Change this to re-run the query. Any value works; only its identity matters.
   *
   * A declarative token rather than only a `reload()` method, because calling a
   * method means holding a component handle, and hosts wrap this grid in `@if`,
   * where a `viewChild` is intermittently undefined. Nothing else in ng-spark
   * uses `viewChild` either — the house idiom is to re-seed a signal.
   *
   * This drives the CHEAP refresh (see {@link reload}). It deliberately does not
   * feed the main effect: re-running `loadData` would re-resolve the query, the
   * entity types, the permissions and the lookups, and reset the user's page and
   * sort on every button press.
   */
  reloadToken = input<unknown>(null);

  colors = Color;

  query = signal<SparkQuery | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  /**
   * Why the component renders its own failure instead of only reporting one.
   *
   * `SparkService` is a bare `firstValueFrom` passthrough with no interceptor, so
   * every failure surfaces here and nowhere else. A host embedding this grid cannot
   * surface what it never sees, and the default has to be visible with no host
   * cooperation — hence a rendered alert, not just an output.
   *
   * A 404 is deliberately vague. `Endpoints/Queries/Get.cs` answers 404 for BOTH
   * "no such query" and "you may not see it", with byte-identical bodies, so that
   * existence is not disclosed (security audit M-3). This component therefore
   * genuinely cannot tell the two apart, and any message claiming otherwise would
   * either leak or mislead.
   */
  errorMessage = signal<string | null>(null);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  loading = signal(true);
  canRead = signal(false);
  settings = signal(new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortColumns: []
  }));
  fetchFn = signal<BsDatatableFetch<PersistentObject> | null>(null);
  isVirtualScrolling = computed(() => this.query()?.renderMode === 'VirtualScrolling');

  visibleAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.Query))
      .sort((a, b) => a.order - b.order) || [];
  });

  constructor() {
    effect(() => {
      const qId = this.queryId();
      const pId = this.parentId();
      const pType = this.parentType();
      // Only the query id is required. Requiring a parent here is what made a
      // standalone grid silently render nothing.
      if (qId) {
        this.loadData(qId, pId, pType);
      } else {
        // No query id at all. `loading` starts true, so without this the component
        // would spin forever instead of saying anything.
        this.loading.set(false);
      }
    });

    // Separate effect, so the token drives the cheap refresh and never the full
    // metadata reload. `first` skips the initial run: the effect above has already
    // fetched, and reacting to the token's starting value would double-fetch on mount.
    let first = true;
    effect(() => {
      this.reloadToken();
      if (first) { first = false; return; }
      untracked(() => this.reload());
    });
  }

  /**
   * Re-run the query, keeping the current page, sort and scroll position.
   *
   * Data-level on purpose: it re-seeds the fetch closure and nothing else, mirroring
   * `SparkQueryListComponent.reload()`. Use it after something mutates server-side
   * state the query reads from. For a definition change — new columns, a renamed
   * query — the inputs themselves must change; that is the expensive path.
   */
  reload(): void {
    const q = this.query();
    if (q) this.fetchFn.set(this.makeFetch(q, this.parentId(), this.parentType()));
  }

  private async loadData(queryId: string, parentId: string, parentType: string): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.fetchFn.set(null);
    // Reset everything derived from the previous query, not just the fetch. Leaving
    // `entityType`/`canRead` behind let a failed reload build a row link out of the
    // PREVIOUS type and the previous permission.
    this.query.set(null);
    this.entityType.set(null);
    this.canRead.set(false);
    try {
      const [resolvedQuery, entityTypes] = await Promise.all([
        this.sparkService.getQuery(queryId),
        this.sparkService.getEntityTypes()
      ]);

      this.query.set(resolvedQuery);
      this.allEntityTypes.set(entityTypes);

      const initialSortColumns: SortColumn[] = (resolvedQuery.sortColumns || []).map(sc => ({
        property: sc.property,
        direction: sc.direction === 'desc' ? 'descending' as const : 'ascending' as const
      }));

      // Resolve entity type from query's entityType field
      if (resolvedQuery.entityType) {
        const et = entityTypes.find(t =>
          t.name === resolvedQuery.entityType || t.alias === resolvedQuery.entityType?.toLowerCase()
        );
        this.entityType.set(et || null);
        if (et) {
          const permissions = await this.sparkService.getPermissions(et.id);
          this.canRead.set(permissions.canRead);
        }
      }

      this.settings.set(new DatatableSettings({
        perPage: { values: [10, 25, 50], selected: 10 },
        page: { values: [1], selected: 1 },
        sortColumns: initialSortColumns
      }));
      // The datatable drives paging/sorting via [(settings)] and calls fetchFn
      // per page. Virtual scrolling is just the [virtualScroll] template flag.
      this.fetchFn.set(this.makeFetch(resolvedQuery, parentId, parentType));

      this.loadLookupReferenceOptions();
    } catch {
      this.fetchFn.set(null);
    } finally {
      this.loading.set(false);
    }
  }

  private makeFetch(query: SparkQuery, parentId: string, parentType: string): BsDatatableFetch<PersistentObject> {
    return (req) => this.sparkService.executeQuery(query.id, {
      sortColumns: req.sortColumns,
      skip: (req.page - 1) * req.perPage,
      take: req.perPage,
      parentId, parentType,
    }).then(r => {
      return {
        data: r.data,
        totalRecords: r.totalRecords,
        totalPages: Math.ceil(r.totalRecords / req.perPage) || 1,
        perPage: req.perPage,
        page: req.page,
      };
    }).catch(() => {
      return { data: [], totalRecords: 0, totalPages: 1, perPage: req.perPage, page: req.page };
    });
  }

  private async loadLookupReferenceOptions(): Promise<void> {
    const lookupAttrs = this.visibleAttributes().filter(a => a.lookupReferenceType);
    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const entries = await Promise.all(
      lookupNames.map(async name => {
        const result = await this.sparkService.getLookupReference(name);
        return [name, result] as const;
      })
    );
    this.lookupReferenceOptions.set(entries.reduce((acc, [k, v]) => ({ ...acc, [k]: v }), {} as Record<string, LookupReference>));
  }

  getColumnRendererComponent(attr: EntityAttributeDefinition): Type<any> | null {
    if (!attr.renderer) return null;
    return this.rendererRegistry.find(r => r.name === attr.renderer)?.columnComponent ?? null;
  }

  getColumnRendererInputs(component: Type<any>, item: PersistentObject, attr: EntityAttributeDefinition): Record<string, any> {
    const itemAttr = item.attributes.find(a => a.name === attr.name);
    return withDeclaredInputs(component, {
      value: rendererValue(itemAttr),
      attribute: attr,
      options: attr.rendererOptions,
      item,
    });
  }
}
