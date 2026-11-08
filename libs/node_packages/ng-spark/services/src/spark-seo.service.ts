import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, Injector, effect, inject, signal, untracked } from '@angular/core';
import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { Meta, Title } from '@angular/platform-browser';
import { RouterStateSnapshot, TitleStrategy } from '@angular/router';
import { EntityType, PersistentObject, PersistentObjectAttribute } from '@mintplayer/ng-spark/models';
import { SPARK_SERVER_ORIGIN } from './provide-spark-hydration';
import { SparkLanguageService } from './spark-language.service';

/**
 * Rich JSON-LD for one type's page (`docs/spark_ssr_PRD.md` D12): members merged over the ones the model mapping
 * produces, or null for none. It receives only the presented object, exactly as `/spark/po/load` returned it to
 * this viewer, so it cannot reach a value the viewer may not read.
 */
export type SparkStructuredData = (po: PersistentObject, type: EntityType) => Record<string, unknown> | null;

/** A detail page's meta description, from the presented object only (as {@link SparkStructuredData}), or null for none. */
export type SparkSeoDescription = (po: PersistentObject, type: EntityType) => string | null;

export interface SparkSeoOptions {
  /** The page title for an object's or a route's title. Default: `"<title> - <the document's own title>"`. */
  title?: (title: string) => string;
  /** Rich JSON-LD per entity type, keyed by the type's name (e.g. `Song`). */
  structuredData?: Record<string, SparkStructuredData>;
  /**
   * The meta and Open Graph description per entity type, keyed by the type's name, for a type whose model names no
   * description attribute (`seo.description`, which wins). Cut to 160 characters.
   */
  description?: Record<string, SparkSeoDescription>;
  /** The description of every page that has no own: the home page, the lists, the sign-in pages. */
  siteDescription?: string;
  /** The Open Graph image of every page without an own (`seo.image`): a path on the site or an absolute URL. */
  image?: string;
}

export const SPARK_SEO_OPTIONS = new InjectionToken<SparkSeoOptions>('SPARK_SEO_OPTIONS');

/**
 * Configures the pages' SEO tags (D12). Without it, the model mapping alone applies to the detail pages. With it, every
 * other page gets its route's `title` (or the site's), the site's description and image, Open Graph tags and a
 * canonical link too (through {@link SparkTitleStrategy}).
 */
export function withSeo(options: SparkSeoOptions): SparkFeature {
  return sparkFeature('seo', SparkFeatureOrder.seo, [
    { provide: SPARK_SEO_OPTIONS, useValue: options },
    { provide: TitleStrategy, useClass: SparkTitleStrategy },
  ]);
}

const JSON_LD_SELECTOR = 'script[type="application/ld+json"][data-spark-seo]';
/**
 * The tags {@link SparkSeoService.reset} removes by name. `og:url` and `og:locale` are not here: they name the language
 * served (`?lang=xx`, #604), so `writeLanguageTags` writes them beside the canonical, rewrites them when the language
 * changes, and removes them when the page has no URL.
 */
const SEO_META = ['name="description"', 'name="robots"', 'property="og:title"', 'property="og:description"',
  'property="og:image"', 'property="og:type"', 'name="twitter:card"'];
/** The page's language alternates (#604 D5), marked so {@link SparkSeoService.reset} removes exactly its own. */
const ALTERNATE_SELECTOR = 'link[rel="alternate"][hreflang][data-spark-seo]';
/** Google shows about this many characters of a description. */
const DESCRIPTION_LENGTH = 160;

/**
 * The detail page's title, description, robots, canonical link, Open Graph tags and JSON-LD (D12), the same on the
 * server and in the browser. Robots is `index` only for a readable object of an indexable type; an error, a new or
 * an edit page is `noindex`. Everything it sets is removed again by {@link reset}, so another page never shows a
 * previous object's tags.
 */
@Injectable({ providedIn: 'root' })
export class SparkSeoService {
  private readonly document = inject(DOCUMENT);
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);
  private readonly options = inject(SPARK_SEO_OPTIONS, { optional: true });
  private readonly serverOrigin = inject(SPARK_SERVER_ORIGIN, { optional: true });
  /**
   * The document's own title, restored by {@link reset}. A hydrating page's `<title>` is already the server's
   * object title, so the server records the site's on the element (`data-spark-site`) and the browser reads it back.
   */
  private readonly siteTitle = this.document.head?.querySelector('title')?.getAttribute('data-spark-site') ?? this.title.getTitle();
  /** Whether the tags on the page are an object's, set by {@link forObject} and cleared by {@link reset}. */
  private objectTags = false;
  // Resolved on first use, not injected: the Router injects the TitleStrategy (which injects this service), and
  // SparkLanguageService injects the Router. Kept once resolved, and only asked for when there is a page to describe:
  // a page's destroy hook calls reset() after a server render has destroyed the injector (NG0205 ended the renderer).
  private readonly injector = inject(Injector);
  private languageService?: SparkLanguageService;
  /**
   * The page's URL without its query (origin + path), or null when the page has no canonical. The language tags derive
   * from it (#604): the canonical, `og:url`, the hreflang alternates and `og:locale`.
   */
  private readonly pageUrl = signal<string | null>(null);

  constructor() {
    // Again when the language or the list of languages changes: /culture may answer after the page's tags were set.
    effect(() => this.writeLanguageTags());
  }

  /**
   * The tags for a readable object.
   * @param canonicalPath The object's canonical path, slug included (`/po/song/Songs%2F23/a-sky-full-of-stars`).
   */
  forObject(type: EntityType | null, po: PersistentObject, canonicalPath: string): void {
    const seo = type?.seo;
    const name = po.breadcrumb || po.name;
    this.pageUrl.set(this.origin() + (canonicalPath.split(/[?#]/)[0] || '/'));
    const language = untracked(() => this.languageState());
    const url = language.url(language.current);
    const typeDescription = type ? this.options?.description?.[type.name] : undefined;
    const description = clip(seo?.description ? attributeText(po, seo.description)
      : typeDescription && type ? typeDescription(po, type) : null);
    const image = (seo?.image ? attributeText(po, seo.image) : null) ?? this.siteImage();
    // `indexableWhen`: one object of an indexable type may still be private, e.g. a playlist that is not public.
    const indexable = !!seo?.indexable && (!seo.indexableWhen || attributeValue(po, seo.indexableWhen) === true);

    this.objectTags = true;
    this.setPageTitle(name);
    this.setMeta('name', 'robots', indexable ? 'index, follow' : 'noindex');
    this.setMeta('name', 'description', description);
    this.setOpenGraph(name, description, image);
    this.writeLanguageTags();

    if (!type || !seo?.schemaType) {
      this.setJsonLd(null);
      return;
    }
    const data: Record<string, unknown> = {
      '@context': 'https://schema.org', '@type': seo.schemaType, name, url, inLanguage: language.current,
    };
    if (description) data['description'] = description;
    if (image) data['image'] = image;
    for (const [property, attribute] of Object.entries(seo.properties ?? {})) {
      const value = attributeText(po, attribute);
      if (value) data[property] = value;
    }
    this.setJsonLd({ ...data, ...(this.options?.structuredData?.[type.name]?.(po, type) ?? {}) });
  }

  /**
   * The tags of a page that is not an object's: its route's title (or the site's), the site's description and image,
   * and a canonical link to its path without the query. Called by {@link SparkTitleStrategy} on every navigation, after
   * the page's components were created; a detail page replaces the tags once its object arrives. A page that already
   * set its own tags keeps them: an object's (until {@link reset}, so in whichever order the two calls come), or
   * `noindex` (a new, an edit or a not-found page), which gets its title only.
   */
  forPage(routeTitle: string | undefined, path: string): void {
    if (this.objectTags) return;
    if (routeTitle) this.setPageTitle(routeTitle);
    else this.title.setTitle(this.siteTitle);
    if (this.meta.getTag('name="robots"')?.content === 'noindex') return;

    const description = clip(this.options?.siteDescription ?? null);
    this.pageUrl.set(this.origin() + (path.split(/[?#]/)[0] || '/'));
    this.setMeta('name', 'description', description);
    this.setOpenGraph(routeTitle ?? this.siteTitle, description, this.siteImage());
    this.writeLanguageTags();
  }

  /** A page that must not be indexed: an error, a new object, an edit form. */
  noIndex(): void {
    this.reset();
    this.setMeta('name', 'robots', 'noindex');
  }

  /** Removes every tag this service set and restores the document's own title. */
  reset(): void {
    this.objectTags = false;
    this.title.setTitle(this.siteTitle);
    for (const selector of SEO_META) this.meta.removeTag(selector);
    // No page URL: writeLanguageTags removes the canonical, og:url, og:locale and the hreflang alternates.
    this.pageUrl.set(null);
    this.writeLanguageTags();
    this.setJsonLd(null);
  }

  /**
   * The current language, every configured one (the current alone until /culture answers) and the page's URL in a
   * language: bare for the default language, `?lang=xx` for the others (#604 D2).
   */
  private languageState(): { current: string; all: string[]; url: (code: string) => string } {
    const languages = this.languageService ??= this.injector.get(SparkLanguageService);
    const current = languages.language();
    const fallback = languages.defaultLanguage();
    const configured = Object.keys(languages.languages());
    const page = this.pageUrl() ?? '';
    return {
      current,
      all: configured.length ? configured : [current],
      url: code => code === fallback ? page : `${page}?lang=${encodeURIComponent(code)}`,
    };
  }

  /**
   * The tags that state the page's language (#604 D5, D6): the canonical and `og:url` in the language served, one
   * `hreflang` alternate per language (itself included, also with one language) plus `x-default`, and `og:locale` for
   * a code with a region. All removed when the page has no URL.
   */
  private writeLanguageTags(): void {
    const page = this.pageUrl();
    const removeAll = () => {
      for (const link of Array.from(this.document.head?.querySelectorAll(ALTERNATE_SELECTOR) ?? [])) link.remove();
      for (const tag of this.meta.getTags('property="og:locale:alternate"')) this.meta.removeTagElement(tag);
    };
    if (!page) {
      // Removing needs no language, so the language service is not asked (see languageService).
      untracked(() => {
        removeAll();
        this.setCanonical(null);
        this.setMeta('property', 'og:url', null);
        this.setMeta('property', 'og:locale', null);
      });
      return;
    }
    const { current, all, url } = this.languageState();
    untracked(() => {
      removeAll();
      this.setCanonical(url(current));
      this.setMeta('property', 'og:url', url(current));
      for (const code of all) this.addAlternate(code, url(code));
      this.addAlternate('x-default', page);
      this.setMeta('property', 'og:locale', ogLocale(current));
      for (const code of all) {
        const locale = code === current ? null : ogLocale(code);
        if (locale) this.meta.addTag({ property: 'og:locale:alternate', content: locale });
      }
    });
  }

  private addAlternate(hreflang: string, href: string): void {
    const link = this.document.createElement('link');
    link.setAttribute('rel', 'alternate');
    link.setAttribute('hreflang', hreflang);
    link.setAttribute('href', href);
    link.setAttribute('data-spark-seo', '');
    this.document.head?.appendChild(link);
  }

  private origin(): string {
    return this.serverOrigin || this.document.location?.origin || '';
  }

  private setPageTitle(name: string): void {
    this.title.setTitle(this.options?.title?.(name) ?? (this.siteTitle ? `${name} - ${this.siteTitle}` : name));
    this.document.head?.querySelector('title')?.setAttribute('data-spark-site', this.siteTitle);
  }

  private setOpenGraph(title: string, description: string | null, image: string | null): void {
    this.setMeta('property', 'og:type', 'website');
    this.setMeta('property', 'og:title', title);
    this.setMeta('property', 'og:description', description);
    this.setMeta('property', 'og:image', image);
    this.setMeta('name', 'twitter:card', image ? 'summary_large_image' : 'summary');
  }

  /** The site's Open Graph image as an absolute URL (Open Graph requires one), or null. */
  private siteImage(): string | null {
    const image = this.options?.image;
    if (!image) return null;
    return /^https?:\/\//i.test(image) ? image : this.origin() + (image.startsWith('/') ? image : '/' + image);
  }

  private setMeta(attribute: 'name' | 'property', key: string, content: string | null): void {
    if (content) this.meta.updateTag({ [attribute]: key, content });
    else this.meta.removeTag(`${attribute}="${key}"`);
  }

  private setCanonical(href: string | null): void {
    let link = this.document.head.querySelector<HTMLLinkElement>('link[rel="canonical"]');
    if (!href) {
      link?.remove();
      return;
    }
    if (!link) {
      link = this.document.createElement('link');
      link.setAttribute('rel', 'canonical');
      this.document.head.appendChild(link);
    }
    link.setAttribute('href', href);
  }

  private setJsonLd(data: Record<string, unknown> | null): void {
    let script = this.document.head.querySelector<HTMLScriptElement>(JSON_LD_SELECTOR);
    if (!data) {
      script?.remove();
      return;
    }
    if (!script) {
      script = this.document.createElement('script');
      script.setAttribute('type', 'application/ld+json');
      script.setAttribute('data-spark-seo', '');
      this.document.head.appendChild(script);
    }
    // `<` escaped, so a value holding "</script>" cannot end the element.
    script.textContent = JSON.stringify(data).replace(/</g, '\\u003c');
  }
}

/**
 * Sets the document title (and the SEO tags of pages that are not an object's) on every navigation, from the route's
 * `title` through {@link SparkSeoService.forPage}. Installed by {@link withSeo}. Unlike Angular's default
 * strategy it also restores the site's title on a route without one, so a previous page's title never stays.
 */
@Injectable()
export class SparkTitleStrategy extends TitleStrategy {
  private readonly seo = inject(SparkSeoService);

  override updateTitle(snapshot: RouterStateSnapshot): void {
    this.seo.forPage(this.buildTitle(snapshot), snapshot.url);
  }
}

/** A description cut to what a search result shows, on a word boundary; null when empty. */
function clip(text: string | null): string | null {
  const value = text?.replace(/\s+/g, ' ').trim();
  if (!value) return null;
  if (value.length <= DESCRIPTION_LENGTH) return value;
  const cut = value.slice(0, DESCRIPTION_LENGTH - 1);
  const space = cut.lastIndexOf(' ');
  return (space > DESCRIPTION_LENGTH / 2 ? cut.slice(0, space) : cut) + '…';
}

/** An attribute's raw value, or undefined when the object has no such attribute. */
function attributeValue(po: PersistentObject, name: string): unknown {
  return po.attributes.find(a => a.name === name)?.value;
}

/** An attribute's value as text: a reference's breadcrumb, a scalar as is, anything else nothing. */
export function attributeText(po: PersistentObject, name: string): string | null {
  const attribute: PersistentObjectAttribute | undefined = po.attributes.find(a => a.name === name);
  if (!attribute) return null;
  if (attribute.breadcrumb) return attribute.breadcrumb;
  const value = attribute.value;
  return typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean'
    ? String(value) || null
    : null;
}

/** Open Graph's locale (`nl_BE`) of a code with a region (`nl-BE`); null for a bare language, whose region would be a guess. */
function ogLocale(code: string): string | null {
  const [language, region] = code.split(/[-_]/);
  return language && region ? `${language.toLowerCase()}_${region.toUpperCase()}` : null;
}
