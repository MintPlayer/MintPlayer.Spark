import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, distinctUntilChanged, map, Observable, of, switchMap } from 'rxjs';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkSeoService } from '@mintplayer/ng-spark/services';
import { SparkSearchService } from './spark-search.service';
import { SparkSearchHit } from './spark-search.models';
import { SparkSiteSearchComponent } from './spark-site-search.component';

interface SearchState {
  term: string;
  hits: SparkSearchHit[];
  /** A translation key, or null. */
  error: string | null;
}

/** The hits of one entity type, in the order the server ranked the type's first hit. */
export interface SparkSearchGroup {
  typeLabel: string;
  hits: SparkSearchHit[];
}

/** Groups hits by type, keeping the server's order inside a group and of the groups' first hits. */
export function groupSearchHits(hits: readonly SparkSearchHit[]): SparkSearchGroup[] {
  const groups = new Map<string, SparkSearchGroup>();
  for (const hit of hits) {
    let group = groups.get(hit.entityType);
    if (!group) groups.set(hit.entityType, group = { typeLabel: hit.typeLabel, hits: [] });
    group.hits.push(hit);
  }
  return [...groups.values()];
}

/**
 * The results page (`/search?q=`, D11), routed by `sparkSearchRoutes()`: the hits grouped by type, each a link to its
 * object. `noindex`. Server-rendered with its results: the server's prerender contributor supplies the same
 * `GET /spark/search?q=` this page sends.
 */
@Component({
  selector: 'spark-search-page',
  imports: [BsAlertComponent, BsListGroupComponent, BsListGroupItemComponent, RouterLink, TranslateKeyPipe, SparkSiteSearchComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="spark-search-page">
      <spark-site-search class="d-block mb-3" [term]="state().term" />
      @if (state().term; as term) {
        <h1 class="h4">{{ 'search.resultsFor' | t }} “{{ term }}”</h1>
        @if (state().error; as error) {
          <bs-alert [color]="'danger'" [announce]="true" class="d-block spark-search-error">{{ error | t }}</bs-alert>
        } @else if (groups().length) {
          @for (group of groups(); track group.typeLabel) {
            <h2 class="h6 mt-3 spark-search-group">{{ group.typeLabel }}</h2>
            <bs-list-group>
              @for (hit of group.hits; track hit.entityType + '/' + hit.id) {
                <bs-list-group-item><a class="spark-search-hit" [routerLink]="hit.url">{{ hit.label }}</a></bs-list-group-item>
              }
            </bs-list-group>
          }
        } @else {
          <p class="text-muted spark-search-none">{{ 'search.noResults' | t }}</p>
        }
      }
    </div>
  `,
})
export class SparkSearchPageComponent {
  private readonly search = inject(SparkSearchService);

  protected readonly state = toSignal(
    inject(ActivatedRoute).queryParamMap.pipe(
      map(params => (params.get('q') ?? '').trim()),
      distinctUntilChanged(),
      switchMap((term): Observable<SearchState> => term
        ? this.search.search(term).pipe(
            map(hits => ({ term, hits, error: null })),
            catchError((error: unknown) => of({
              term,
              hits: [],
              error: error instanceof HttpErrorResponse && error.status === 429 ? 'search.rateLimited' : 'search.failed',
            })))
        : of({ term, hits: [], error: null })),
    ),
    { initialValue: { term: '', hits: [], error: null } satisfies SearchState },
  );

  protected readonly groups = computed(() => groupSearchHits(this.state().hits));

  constructor() {
    const seo = inject(SparkSeoService);
    seo.noIndex();
    inject(DestroyRef).onDestroy(() => seo.reset());
  }
}
