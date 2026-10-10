import { HttpClient } from '@angular/common/http';
import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { firstValueFrom } from 'rxjs';

/** One catalogue entry in one language, as `GET /seotester/catalogue` answers it. */
export interface SeoCatalogueCheck {
  id: string;
  category: string;
  severity: string;
  weight: number;
  enabled: boolean;
  title: string;
  summary?: string | null;
  howToFix?: string | null;
  snippets?: Partial<Record<SeoSnippetPlatform, string>> | null;
  docsUrl?: string | null;
}

export type SeoSnippetPlatform = 'nginx' | 'apache' | 'iis' | 'html';

export interface SeoCatalogue {
  culture: string;
  categories: { key: string; label: string }[];
  severities: { key: string; label: string }[];
  checks: SeoCatalogueCheck[];
}

/** The order categories are shown in: the scored ones by their default weight (PRD §7), Domain (info) last. */
export const SEO_CATEGORY_ORDER = ['Seo', 'Technical', 'Performance', 'Security', 'Accessibility', 'Social', 'Mobile', 'Domain'];
export const SEO_SEVERITY_ORDER = ['Critical', 'High', 'Medium', 'Low', 'Info'];

/**
 * The check catalogue's texts in the viewer's language (the public `GET /seotester/catalogue`, so the anonymous share
 * page has them too). Fetched once per language and shared by every renderer on the page; it follows the language
 * selector.
 */
@Injectable({ providedIn: 'root' })
export class SeoCatalogueService {
  private readonly http = inject(HttpClient);
  private readonly lang = inject(SparkLanguageService);
  private readonly loaded = signal<Record<string, SeoCatalogue>>({});
  private readonly pending = new Map<string, Promise<void>>();

  /** The catalogue in the current language; `null` until it arrived (or when it could not be loaded). */
  readonly catalogue = computed<SeoCatalogue | null>(() => {
    const culture = this.culture();
    return this.loaded()[culture] ?? null;
  });

  constructor() {
    // Fetch the language on screen, again when the language selector changes it (each language once).
    effect(() => {
      const culture = this.culture();
      untracked(() => void this.load(culture));
    });
  }

  /** Entries by check id. */
  readonly byId = computed(() => new Map((this.catalogue()?.checks ?? []).map(c => [c.id, c] as const)));

  categoryLabel(key: string): string {
    return this.catalogue()?.categories.find(c => c.key === key)?.label ?? key;
  }

  severityLabel(key: string): string {
    return this.catalogue()?.severities.find(c => c.key === key)?.label ?? key;
  }

  private culture(): string {
    return (this.lang.language() ?? 'en').toLowerCase().startsWith('nl') ? 'nl' : 'en';
  }

  private load(culture: string): Promise<void> {
    let pending = this.pending.get(culture);
    if (!pending) {
      pending = firstValueFrom(this.http.get<SeoCatalogue>('/seotester/catalogue', { params: { culture } }))
        .then(catalogue => this.loaded.update(all => ({ ...all, [culture]: catalogue })))
        .catch(() => { /* the renderers fall back to the check ids */ });
      this.pending.set(culture, pending);
    }
    return pending;
  }
}
