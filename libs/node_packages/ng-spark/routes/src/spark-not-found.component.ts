import { ChangeDetectionStrategy, Component, OnDestroy, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SPARK_SERVER_RESPONSE, SparkLanguageService, SparkSeoService } from '@mintplayer/ng-spark/services';

/**
 * The page of a URL no route matches, routed by `sparkRoutes({ notFound: true })`. A server render answers it with a
 * real 404 (through {@link SPARK_SERVER_RESPONSE}) instead of a 200 shell, so a crawler does not index a missing page
 * as a soft 404; the page also says `noindex`.
 */
@Component({
  selector: 'spark-not-found',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <h1 class="spark-not-found-title">{{ lang.t('notFound.title', 'Page not found') }}</h1>
    <p>{{ lang.t('notFound.message', 'The page you are looking for does not exist (any more).') }}</p>
    <a routerLink="/">{{ lang.t('notFound.home', 'Go to the home page') }}</a>
  `,
})
export class SparkNotFoundComponent implements OnDestroy {
  protected readonly lang = inject(SparkLanguageService);
  private readonly seo = inject(SparkSeoService);

  constructor() {
    const response = inject(SPARK_SERVER_RESPONSE, { optional: true });
    if (response) response.status = 404;
    this.seo.noIndex();
  }

  ngOnDestroy(): void {
    this.seo.reset();
  }
}
