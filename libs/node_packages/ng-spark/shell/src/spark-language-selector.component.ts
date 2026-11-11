import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, UrlTree } from '@angular/router';
import { filter, map } from 'rxjs';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';

/**
 * The culture switcher (#604 D9): a `<details>` menu with one router link per language, each to the current page with
 * `?lang=<code>`. Following one switches the language without a reload (SparkLanguageService reads the `lang` of every
 * navigation), and because the choices are real links, a crawler reaches every language of every page. Renders nothing
 * when the app declares one language (or none), so hosts can include it unconditionally: `<spark-shell>`'s topbar does
 * exactly that as its trailing default.
 */
@Component({
  selector: 'spark-language-selector',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ResolveTranslationPipe],
  templateUrl: './spark-language-selector.component.html',
  styleUrl: './spark-language-selector.component.scss',
})
export class SparkLanguageSelectorComponent {
  protected readonly lang = inject(SparkLanguageService);
  private readonly router = inject(Router);

  protected readonly choices = computed(() =>
    Object.entries(this.lang.languages()).map(([code, name]) => ({ code, name })));

  // The page the links point at: the router's URL after each navigation. The shell sits outside the router outlet, so a
  // relative routerLink would resolve against the root, not against the page.
  private readonly url = toSignal(
    this.router.events.pipe(filter(e => e instanceof NavigationEnd), map(() => this.router.url)),
    { initialValue: this.router.url });

  /** The current page in `code`: its path and query, `lang` replaced. Named explicitly, the default language too. */
  protected linkTo(code: string): UrlTree {
    const tree = this.router.parseUrl(this.url());
    tree.queryParams = { ...tree.queryParams, lang: code };
    return tree;
  }
}
