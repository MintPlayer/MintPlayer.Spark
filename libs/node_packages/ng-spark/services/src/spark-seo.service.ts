import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { Meta, Title } from '@angular/platform-browser';
import { RouterStateSnapshot, TitleStrategy } from '@angular/router';
import { EntityType, PersistentObject, PersistentObjectAttribute } from '@mintplayer/ng-spark/models';
import { SPARK_SERVER_ORIGIN } from './provide-spark-hydration';

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
const SEO_META = ['name="description"', 'name="robots"', 'property="og:title"', 'property="og:description"',
  'property="og:url"', 'property="og:image"', 'property="og:type"', 'name="twitter:card"'];
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

  /**
   * The tags for a readable object.
   * @param canonicalPath The object's canonical path, slug included (`/po/song/Songs%2F23/a-sky-full-of-stars`).
   */
  forObject(type: EntityType | null, po: PersistentObject, canonicalPath: string): void {
    const seo = type?.seo;
    const name = po.breadcrumb || po.name;
    const url = this.origin() + canonicalPath;
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
    this.setOpenGraph(name, description, url, image);
    this.setCanonical(url);

    if (!type || !seo?.schemaType) {
      this.setJsonLd(null);
      return;
    }
    const data: Record<string, unknown> = { '@context': 'https://schema.org', '@type': seo.schemaType, name, url };
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
    const url = this.origin() + (path.split(/[?#]/)[0] || '/');
    this.setMeta('name', 'description', description);
    this.setOpenGraph(routeTitle ?? this.siteTitle, description, url, this.siteImage());
    this.setCanonical(url);
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
    this.setCanonical(null);
    this.setJsonLd(null);
  }

  private origin(): string {
    return this.serverOrigin || this.document.location?.origin || '';
  }

  private setPageTitle(name: string): void {
    this.title.setTitle(this.options?.title?.(name) ?? (this.siteTitle ? `${name} - ${this.siteTitle}` : name));
    this.document.head?.querySelector('title')?.setAttribute('data-spark-site', this.siteTitle);
  }

  private setOpenGraph(title: string, description: string | null, url: string, image: string | null): void {
    this.setMeta('property', 'og:type', 'website');
    this.setMeta('property', 'og:title', title);
    this.setMeta('property', 'og:description', description);
    this.setMeta('property', 'og:url', url);
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
