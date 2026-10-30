import { DOCUMENT } from '@angular/common';
import { EnvironmentProviders, Injectable, InjectionToken, inject, makeEnvironmentProviders } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { EntityType, PersistentObject, PersistentObjectAttribute } from '@mintplayer/ng-spark/models';
import { SPARK_SERVER_ORIGIN } from './provide-spark-hydration';

/**
 * Rich JSON-LD for one type's page (`docs/spark_ssr_PRD.md` D12): members merged over the ones the model mapping
 * produces, or null for none. It receives only the presented object, exactly as `/spark/po/load` returned it to
 * this viewer, so it cannot reach a value the viewer may not read.
 */
export type SparkStructuredData = (po: PersistentObject, type: EntityType) => Record<string, unknown> | null;

export interface SparkSeoOptions {
  /** The page title for an object's title (its breadcrumb). Default: `"<title> - <the document's own title>"`. */
  title?: (title: string) => string;
  /** Rich JSON-LD per entity type, keyed by the type's name (e.g. `Song`). */
  structuredData?: Record<string, SparkStructuredData>;
}

export const SPARK_SEO_OPTIONS = new InjectionToken<SparkSeoOptions>('SPARK_SEO_OPTIONS');

/** Configures the detail pages' SEO tags (D12). Without it, the model mapping alone applies. */
export function provideSparkSeo(options: SparkSeoOptions): EnvironmentProviders {
  return makeEnvironmentProviders([{ provide: SPARK_SEO_OPTIONS, useValue: options }]);
}

const JSON_LD_SELECTOR = 'script[type="application/ld+json"][data-spark-seo]';
const SEO_META = ['name="description"', 'name="robots"', 'property="og:title"', 'property="og:description"',
  'property="og:url"', 'property="og:image"', 'property="og:type"'];

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

  /**
   * The tags for a readable object.
   * @param canonicalPath The object's canonical path, slug included (`/po/song/Songs%2F23/a-sky-full-of-stars`).
   */
  forObject(type: EntityType | null, po: PersistentObject, canonicalPath: string): void {
    const seo = type?.seo;
    const name = po.breadcrumb || po.name;
    const url = this.origin() + canonicalPath;
    const description = seo?.description ? attributeText(po, seo.description) : null;
    const image = seo?.image ? attributeText(po, seo.image) : null;

    this.title.setTitle(this.options?.title?.(name) ?? (this.siteTitle ? `${name} - ${this.siteTitle}` : name));
    this.document.head?.querySelector('title')?.setAttribute('data-spark-site', this.siteTitle);
    this.setMeta('name', 'robots', seo?.indexable ? 'index, follow' : 'noindex');
    this.setMeta('name', 'description', description);
    this.setMeta('property', 'og:type', 'website');
    this.setMeta('property', 'og:title', name);
    this.setMeta('property', 'og:description', description);
    this.setMeta('property', 'og:url', url);
    this.setMeta('property', 'og:image', image);
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

  /** A page that must not be indexed: an error, a new object, an edit form. */
  noIndex(): void {
    this.reset();
    this.setMeta('name', 'robots', 'noindex');
  }

  /** Removes every tag this service set and restores the document's own title. */
  reset(): void {
    this.title.setTitle(this.siteTitle);
    for (const selector of SEO_META) this.meta.removeTag(selector);
    this.setCanonical(null);
    this.setJsonLd(null);
  }

  private origin(): string {
    return this.serverOrigin || this.document.location?.origin || '';
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
