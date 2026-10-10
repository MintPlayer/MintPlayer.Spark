import { Component, input, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SeoCheckResultsComponent } from './renderers/check-results.component';
import { SeoCatalogue, SeoCatalogueCheck, SeoCatalogueService } from './seo-catalogue.service';

/** A small catalogue for specs: three checks with English texts. */
export const SPEC_CHECKS: SeoCatalogueCheck[] = [
  { id: 'seo.title-present', category: 'Seo', severity: 'Critical', weight: 10, enabled: true, title: 'Page has a title', summary: '{n} pages have no title', howToFix: 'Add a <title> to every page.', snippets: { html: '<title>Home</title>' }, docsUrl: 'https://developers.google.com/search/docs' },
  { id: 'security.hsts', category: 'Security', severity: 'High', weight: 6, enabled: true, title: 'HSTS header', summary: 'The site sends no HSTS header', howToFix: 'Send Strict-Transport-Security.', snippets: { nginx: 'add_header Strict-Transport-Security "max-age=31536000";' }, docsUrl: null },
  { id: 'performance.cwv', category: 'Performance', severity: 'Medium', weight: 3, enabled: true, title: 'Core Web Vitals', summary: 'LCP is {value}', howToFix: null, snippets: null, docsUrl: null },
];

export const SPEC_CATALOGUE: SeoCatalogue = {
  culture: 'en',
  categories: [{ key: 'Seo', label: 'SEO' }, { key: 'Security', label: 'Security' }, { key: 'Performance', label: 'Performance' }],
  severities: [{ key: 'Critical', label: 'Critical' }, { key: 'High', label: 'High' }, { key: 'Medium', label: 'Medium' }],
  checks: SPEC_CHECKS,
};

/** The catalogue service with a fixed catalogue (no HTTP). */
export function catalogueStub(catalogue: SeoCatalogue | null = SPEC_CATALOGUE) {
  const value = signal(catalogue);
  return {
    catalogue: value,
    byId: () => new Map((value()?.checks ?? []).map(c => [c.id, c] as const)),
    categoryLabel: (key: string) => value()?.categories.find(c => c.key === key)?.label ?? key,
    severityLabel: (key: string) => value()?.severities.find(c => c.key === key)?.label ?? key,
  };
}

/** A language service that answers each key with itself (or the fallback), in English. */
export const languageStub = {
  language: signal('en'),
  t: (key: string, fallback?: string) => fallback ?? key,
  resolve: (ts?: Record<string, string>) => ts?.['en'] ?? '',
  translationsMap: signal({}),
};

/** `bs-code-snippet` hosts a Lit element jsdom cannot run; this has the same selector and inputs. */
@Component({ selector: 'bs-code-snippet', template: '<pre>{{ code() }}</pre>' })
export class StubCodeSnippet {
  readonly code = input('');
  readonly language = input('');
  readonly wrap = input(false);
  readonly label = input('');
  readonly copyLabel = input('');
}

export function provideSeoSpecServices() {
  return [
    // bs-alert animates.
    provideNoopAnimations(),
    { provide: SeoCatalogueService, useValue: catalogueStub() },
    { provide: SparkLanguageService, useValue: languageStub },
  ];
}

export function stubCodeSnippet(): void {
  TestBed.overrideComponent(SeoCheckResultsComponent, { remove: { imports: [BsCodeSnippetComponent] }, add: { imports: [StubCodeSnippet] } });
}
