import { inject, Injectable } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';

/** What a public page tells link previews and the tab title. */
export interface SparkStorefrontPageMeta {
  title: string;
  description?: string | null;
  image?: string | null;
  /** `og:type`: `product`, `website`, … */
  type?: string;
}

/** The longest description sent (the server's injection cuts at 300 too). */
export const STOREFRONT_META_DESCRIPTION_MAX = 300;

/** A plain-text description cut at {@link STOREFRONT_META_DESCRIPTION_MAX} characters on a word boundary, with "…". */
export function metaDescription(text: string | null | undefined): string {
  const plain = (text ?? '').replace(/\s+/g, ' ').trim();
  if (plain.length <= STOREFRONT_META_DESCRIPTION_MAX) return plain;
  const cut = plain.slice(0, STOREFRONT_META_DESCRIPTION_MAX - 1);
  const space = cut.lastIndexOf(' ');
  return (space > STOREFRONT_META_DESCRIPTION_MAX / 2 ? cut.slice(0, space) : cut).trimEnd() + '…';
}

/**
 * Client-side meta tags for the public product and event pages (PRD §4.14, spike S19). The server injects the same tags
 * into `index.html` for link previews and crawlers (`ISparkPageMetaProvider`, X6); this service keeps them right after
 * client-side navigation and for installed PWAs, whose service worker serves a cached `index.html` without them.
 * Values go through Angular's `Meta`/`Title`, which set attributes and text, never markup.
 */
@Injectable({ providedIn: 'root' })
export class SparkStorefrontMeta {
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);

  set(page: SparkStorefrontPageMeta): void {
    const description = metaDescription(page.description);
    this.title.setTitle(page.title);
    this.tag('name', 'description', description);
    this.tag('property', 'og:title', page.title);
    this.tag('property', 'og:description', description);
    this.tag('property', 'og:type', page.type ?? 'website');
    this.tag('property', 'og:image', page.image ?? '');
    this.tag('name', 'twitter:card', page.image ? 'summary_large_image' : 'summary');
  }

  private tag(attribute: 'name' | 'property', key: string, content: string): void {
    const selector = `${attribute}="${key}"`;
    if (!content) {
      this.meta.removeTag(selector);
      return;
    }
    this.meta.updateTag({ [attribute]: key, content }, selector);
  }
}
