import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

/** Where Google cuts a title or description, approximately (PRD F7: pixel widths, approximated by characters). */
export const SERP_LIMITS = { desktopTitle: 60, mobileTitle: 70, desktopDescription: 158, mobileDescription: 120 };

/** The server's proxy for a page's `og:image` (Q-M11d): the browser never loads the audited site's image itself. */
export const OG_IMAGE_ROUTE = '/seotester/og-image';

export function cut(text: string | null | undefined, max: number): string {
  const t = (text ?? '').trim();
  return t.length <= max ? t : t.slice(0, Math.max(0, max - 1)).trimEnd() + '…';
}

/** A URL the way a result page shows it: host › path segments. */
export function breadcrumbUrl(url: string): string {
  try {
    const u = new URL(url);
    const segments = u.pathname.split('/').filter(Boolean);
    return [u.hostname, ...segments].join(' › ');
  } catch { return url; }
}

function text(data: Record<string, unknown>, key: string): string {
  return typeof data[key] === 'string' ? (data[key] as string) : '';
}

/**
 * What a shared link's card shows (Q-M11d): the page's `og:title`, `og:description` and `og:url` when it has them,
 * else what networks fall back to (the title, the description, the address); the image only through the server's
 * proxy, by the page's id, never from the address in the tag.
 */
export function socialCard(data: Record<string, unknown>, pageId: string | null | undefined) {
  const ogTitle = text(data, 'OgTitle'), ogDescription = text(data, 'OgDescription');
  const address = text(data, 'OgUrl') || text(data, 'Url');
  let host = '';
  try { host = new URL(address).hostname; } catch { /* no address */ }
  const imageUrl = text(data, 'OgImage').trim();
  return {
    host,
    title: cut(ogTitle || text(data, 'Title'), 70),
    description: cut(ogDescription || text(data, 'Description'), 100),
    fromOpenGraph: !!(ogTitle || ogDescription),
    imageUrl,
    imageSrc: imageUrl && pageId ? `${OG_IMAGE_ROUTE}?page=${encodeURIComponent(pageId)}` : null,
  };
}

/**
 * `seo-previews`, the full-width renderer on `PageResult.Title` (PRD F7): how the page appears in Google's results on
 * a desktop and on a phone, and as a link card on social networks, from what the crawl read. The card uses the page's
 * Open Graph values and shows its `og:image` through `/seotester/og-image` (fetched by the server over the SSRF-safe
 * client, raster images up to 5 MB), with the image's size and its address as text.
 */
@Component({
  selector: 'app-seo-previews',
  imports: [TranslateKeyPipe],
  template: `
    @let p = preview();
    @let c = card();
    <div class="d-flex flex-wrap gap-4">
      <figure class="seo-serp mb-0" data-preview="desktop">
        <figcaption class="small text-body-secondary mb-1">{{ 'seoUi.preview.desktop' | t }}</figcaption>
        <div class="seo-serp-url">{{ p.crumb }}</div>
        <div class="seo-serp-title">{{ p.desktopTitle || ('seoUi.preview.noTitle' | t) }}</div>
        <div class="seo-serp-desc">{{ p.desktopDescription || ('seoUi.preview.noDescription' | t) }}</div>
      </figure>
      <figure class="seo-serp seo-serp-mobile mb-0" data-preview="mobile">
        <figcaption class="small text-body-secondary mb-1">{{ 'seoUi.preview.mobile' | t }}</figcaption>
        <div class="seo-serp-url">{{ p.crumb }}</div>
        <div class="seo-serp-title">{{ p.mobileTitle || ('seoUi.preview.noTitle' | t) }}</div>
        <div class="seo-serp-desc">{{ p.mobileDescription }}</div>
      </figure>
      <figure class="seo-card mb-0" data-preview="social">
        <figcaption class="small text-body-secondary mb-1">{{ 'seoUi.preview.social' | t }}</figcaption>
        <div class="border rounded overflow-hidden">
          @if (c.imageSrc && !imageFailed()) {
            <img class="seo-card-image d-block" [src]="c.imageSrc" alt="" referrerpolicy="no-referrer"
                 (load)="onImageLoad($event)" (error)="imageFailed.set(true)" />
          }
          <div class="p-2">
            <div class="small text-body-secondary text-uppercase">{{ c.host }}</div>
            <div class="fw-semibold">{{ c.title || ('seoUi.preview.noTitle' | t) }}</div>
            <div class="small text-body-secondary">{{ c.description }}</div>
          </div>
        </div>
        <div class="small text-body-secondary mt-1 seo-card-notes">
          <div>{{ (c.fromOpenGraph ? 'seoUi.preview.fromOpenGraph' : 'seoUi.preview.fromTitle') | t }}</div>
          @if (c.imageUrl) {
            @if (imageFailed()) {
              <div class="seo-card-image-failed">{{ 'seoUi.preview.imageNotShown' | t }}</div>
            } @else if (imageSize(); as size) {
              <div class="seo-card-image-size">{{ sizeText(size) }}</div>
            }
            <code class="seo-card-image-url">{{ c.imageUrl }}</code>
          } @else {
            <div class="seo-card-no-image">{{ 'seoUi.preview.noImage' | t }}</div>
          }
        </div>
      </figure>
    </div>
  `,
  styles: [`
    .seo-serp { max-width: 600px; font-family: Arial, sans-serif; min-width: 0; }
    .seo-serp-mobile { max-width: 360px; }
    .seo-serp-url { font-size: 12px; color: var(--bs-secondary-color); overflow-wrap: anywhere; }
    .seo-serp-title { font-size: 20px; line-height: 1.3; color: #1a0dab; overflow-wrap: anywhere; }
    :host-context([data-bs-theme=dark]) .seo-serp-title { color: #99c3ff; }
    .seo-serp-desc { font-size: 14px; color: var(--bs-body-color); overflow-wrap: anywhere; }
    .seo-card { width: 320px; max-width: 100%; }
    .seo-card-image { width: 100%; aspect-ratio: 1.91 / 1; object-fit: cover; background: var(--bs-secondary-bg); }
    .seo-card-image-url { display: block; overflow-wrap: anywhere; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeoPreviewsComponent {
  readonly value = input<unknown>();
  readonly formData = input<Record<string, unknown>>({});
  /** The page itself (its id names the image to the proxy). */
  readonly item = input<{ id?: string | null } | undefined>();

  readonly imageFailed = signal(false);
  readonly imageSize = signal<{ width: number; height: number } | null>(null);

  protected readonly preview = computed(() => {
    const data = this.formData();
    const title = typeof this.value() === 'string' ? (this.value() as string) : '';
    const description = text(data, 'Description');
    const url = text(data, 'Url');
    return {
      crumb: breadcrumbUrl(url),
      desktopTitle: cut(title, SERP_LIMITS.desktopTitle), mobileTitle: cut(title, SERP_LIMITS.mobileTitle),
      desktopDescription: cut(description, SERP_LIMITS.desktopDescription), mobileDescription: cut(description, SERP_LIMITS.mobileDescription),
    };
  });

  protected readonly card = computed(() => socialCard({ ...this.formData(), Title: this.value() }, this.item()?.id));

  onImageLoad(event: Event): void {
    const image = event.target as HTMLImageElement;
    if (image.naturalWidth > 0) this.imageSize.set({ width: image.naturalWidth, height: image.naturalHeight });
  }

  protected sizeText(size: { width: number; height: number }): string {
    return this.t('seoUi.preview.imageSize').replace('{0}', String(size.width)).replace('{1}', String(size.height));
  }

  private readonly language = inject(SparkLanguageService);
  private t(key: string): string {
    return this.language.t(key);
  }
}
