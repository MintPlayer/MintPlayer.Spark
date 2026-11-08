import { Injectable, Injector, inject } from '@angular/core';
import { DefaultUrlSerializer, UrlSerializer, UrlTree } from '@angular/router';
import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { SparkLanguageService } from './spark-language.service';

/**
 * Keeps the page's language in every router URL (#604 D4): `routerLink` hrefs, `navigate()` and the address bar add
 * `?lang=<current>` when the URL names none and the current language is not the default. A `lang` the URL sets is
 * never touched, so the language picker's link to the default language can say `?lang=en` explicitly. Every other
 * query parameter keeps Angular's behaviour. Installed by {@link withLanguageUrls}.
 */
@Injectable()
export class SparkUrlSerializer extends DefaultUrlSerializer {
  // Resolved on first use: the Router injects the UrlSerializer, and SparkLanguageService injects the Router.
  private readonly injector = inject(Injector);

  override serialize(tree: UrlTree): string {
    const languages = this.injector.get(SparkLanguageService);
    const current = languages.language();
    if (current === languages.defaultLanguage() || 'lang' in tree.queryParams)
      return super.serialize(tree);
    return super.serialize(new UrlTree(tree.root, { ...tree.queryParams, lang: current }, tree.fragment));
  }
}

/**
 * `provideSpark(withLanguageUrls(), …)`: the language in the URL (#604). Pages of the default language have no
 * `?lang`; the others keep `?lang=xx` on every link, so a link, a crawl and a server render all name the language.
 */
export function withLanguageUrls(): SparkFeature {
  return sparkFeature('languageUrls', SparkFeatureOrder.languageUrls, [
    { provide: UrlSerializer, useClass: SparkUrlSerializer },
  ]);
}
