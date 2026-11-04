import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { Router } from '@angular/router';
import { BsTypeaheadComponent, BsTypeaheadItemDirective } from '@mintplayer/ng-bootstrap/typeahead';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkSearchService } from './spark-search.service';
import { SPARK_SEARCH_OPTIONS, SparkSearchHit } from './spark-search.models';

/**
 * The site-wide search box (`docs/spark_search_PRD.md` D11): suggestions while typing (debounced, a newer keystroke
 * cancels the request in flight), arrow keys and Enter to pick one, and a pick opens the object. Enter on typed text
 * opens the one object it names (D4, unless `withSearch({ singleHit: 'searchPage' })`), otherwise the results page.
 *
 * Server-rendered as a `GET` form on the results page with a named `q` input, so it searches with JavaScript off too.
 * (The grid's filter box is `<spark-search-box>`; this one searches the whole site.)
 */
@Component({
  selector: 'spark-site-search',
  imports: [BsTypeaheadComponent, BsTypeaheadItemDirective, TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form role="search" method="get" [attr.action]="options.pagePath" (submit)="$event.preventDefault()">
      <bs-typeahead
        name="q"
        [value]="term()"
        [provider]="provider"
        [minLength]="2"
        [itemText]="itemText"
        [itemKey]="itemKey"
        [inputLabel]="'common.search' | t"
        [noResultsLabel]="'search.noResults' | t"
        (itemSelect)="open($event.item)"
        (termSubmit)="submit($event)">
        <ng-template bsTypeaheadItem let-item>
          <span class="spark-site-search-label">{{ item.label }}</span>
          <small class="ms-2 opacity-75 spark-site-search-type">{{ item.typeLabel }}</small>
        </ng-template>
      </bs-typeahead>
    </form>
  `,
})
export class SparkSiteSearchComponent {
  private readonly search = inject(SparkSearchService);
  private readonly router = inject(Router);
  protected readonly options = inject(SPARK_SEARCH_OPTIONS);

  /** The text the box starts with (the results page passes its term). */
  readonly term = input('');

  protected readonly provider = (term: string) => this.search.suggest(term);
  protected readonly itemText = (hit: SparkSearchHit) => hit.label;
  protected readonly itemKey = (hit: SparkSearchHit) => `${hit.entityType}/${hit.id}`;

  protected open(hit: SparkSearchHit): void {
    void this.router.navigateByUrl(hit.url);
  }

  protected async submit(text: string): Promise<void> {
    const term = text.trim();
    if (!term) return;
    if (this.options.singleHit === 'item') {
      // A failed resolve (429, offline) still lands on the results page, which explains it.
      const hit = await this.search.resolve(term).catch(() => null);
      if (hit) {
        void this.router.navigateByUrl(hit.url);
        return;
      }
    }
    void this.router.navigate([this.options.pagePath], { queryParams: { q: term } });
  }
}
