import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkConsentBannerComponent } from '@mintplayer/ng-spark/consent';
import { injectStorefront } from '../storefront-config';
import { inAppLinkTarget } from '../storefront-links';

/**
 * The storefront's layout (M17): a skip link (WCAG 2.4.1), the navigation of the enabled features with the basket and
 * the account, the page in a `<main>` landmark, the consent banner (GA9, once per app), and a footer with the legal
 * pages, the consent settings ("withdrawing must be as easy as giving", GDPR Art. 7(3)) and the accessibility statement
 * (GA10). Theme tokens (`--spark-commerce-*`) are declared here over Bootstrap's variables, so dark mode (#462) follows;
 * a host overrides them on any ancestor, or replaces the shell (`provideSparkCommerce({ shell: MyShell })`).
 */
@Component({
  selector: 'spark-storefront-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslateKeyPipe, SparkConsentBannerComponent],
  templateUrl: './spark-storefront-shell.component.html',
  styleUrl: './spark-storefront-shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontShellComponent {
  private readonly router = inject(Router);
  protected readonly config = injectStorefront();
  protected readonly p = this.config.paths;

  protected link(segment: string, ...rest: string[]): string {
    return '/' + [segment, ...rest].join('/');
  }

  /**
   * The library components link with plain `href`s (they work without a router). Inside the shell a plain left click on
   * an in-app link is routed instead of reloading the page.
   */
  protected followInApp(event: MouseEvent): void {
    const target = inAppLinkTarget(event, location.origin);
    if (target === null) return;
    event.preventDefault();
    void this.router.navigateByUrl(target);
  }

  /** Moves focus to the page, for the skip link (a fragment link alone does not move focus in every browser). */
  protected skipToContent(event: Event, main: HTMLElement): void {
    event.preventDefault();
    main.focus();
  }
}
