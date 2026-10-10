import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';

/** Where Google cuts a title or description, approximately (PRD F7: pixel widths, approximated by characters). */
export const SERP_LIMITS = { desktopTitle: 60, mobileTitle: 70, desktopDescription: 158, mobileDescription: 120 };

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

/**
 * `seo-previews`, the full-width renderer on `PageResult.Title` (PRD F7): how the page appears in Google's results on
 * a desktop and on a phone, and as a link card on social networks, from the title, description and address the crawl
 * read. The card has no image: the crawl does not keep the page's `og:image`.
 */
@Component({
  selector: 'app-seo-previews',
  imports: [TranslateKeyPipe],
  template: `
    @let p = preview();
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
        <div class="border rounded p-2">
          <div class="small text-body-secondary text-uppercase">{{ p.host }}</div>
          <div class="fw-semibold">{{ p.cardTitle || ('seoUi.preview.noTitle' | t) }}</div>
          <div class="small text-body-secondary">{{ p.cardDescription }}</div>
        </div>
      </figure>
    </div>
  `,
  styles: [`
    .seo-serp { max-width: 600px; font-family: Arial, sans-serif; }
    .seo-serp-mobile { max-width: 360px; }
    .seo-serp-url { font-size: 12px; color: var(--bs-secondary-color); }
    .seo-serp-title { font-size: 20px; line-height: 1.3; color: #1a0dab; }
    :host-context([data-bs-theme=dark]) .seo-serp-title { color: #99c3ff; }
    .seo-serp-desc { font-size: 14px; color: var(--bs-body-color); }
    .seo-card { width: 320px; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeoPreviewsComponent {
  readonly value = input<unknown>();
  readonly formData = input<Record<string, unknown>>({});

  protected readonly preview = computed(() => {
    const data = this.formData();
    const title = typeof this.value() === 'string' ? (this.value() as string) : '';
    const description = typeof data['Description'] === 'string' ? (data['Description'] as string) : '';
    const url = typeof data['Url'] === 'string' ? (data['Url'] as string) : '';
    let host = '';
    try { host = new URL(url).hostname; } catch { /* no address */ }
    return {
      crumb: breadcrumbUrl(url), host,
      desktopTitle: cut(title, SERP_LIMITS.desktopTitle), mobileTitle: cut(title, SERP_LIMITS.mobileTitle),
      desktopDescription: cut(description, SERP_LIMITS.desktopDescription), mobileDescription: cut(description, SERP_LIMITS.mobileDescription),
      cardTitle: cut(title, 70), cardDescription: cut(description, 100),
    };
  });
}
