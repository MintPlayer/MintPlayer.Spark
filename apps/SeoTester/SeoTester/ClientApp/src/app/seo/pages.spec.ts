import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import type { PersistentObject, QueryResultItem } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { routes } from '../app.routes';
import { seoTrend } from './panels/site-trend-panel.component';
import { downloadUrlOf, pageDetailsExpired } from './po-detail-page.component';
import { SeoCatalogueService } from './seo-catalogue.service';
import { languageStub } from './testing';

const po = (values: Record<string, unknown>) =>
  ({ id: 'AuditRuns/1', name: 'AuditRun', attributes: Object.entries(values).map(([name, value]) => ({ name, value })) }) as unknown as PersistentObject;

describe('pageDetailsExpired', () => {
  const now = new Date('2026-10-10T12:00:00Z');
  it('is true only once 7 days passed since the run finished', () => {
    expect(pageDetailsExpired(po({ CompletedAtUtc: '2026-10-03T11:00:00Z' }), now)).toBe(true);
    expect(pageDetailsExpired(po({ CompletedAtUtc: '2026-10-03T13:00:00Z' }), now)).toBe(false);
    expect(pageDetailsExpired(po({ StartedAtUtc: '2026-09-01T00:00:00Z' }), now)).toBe(true);
    expect(pageDetailsExpired(po({}), now)).toBe(false);
  });
});

describe('downloadUrlOf', () => {
  it('downloads a printed report at once, and nothing else', () => {
    expect(downloadUrlOf('ExportReport', { url: '/seotester/report.pdf?run=1&culture=en', ready: true })).toBe('/seotester/report.pdf?run=1&culture=en');
    expect(downloadUrlOf('ExportReport', { url: '/seotester/report.pdf?run=1', ready: false })).toBeNull();
    expect(downloadUrlOf('ExportReport', { url: 'https://evil.example/x', ready: true })).toBeNull();
    expect(downloadUrlOf('ExportReport', { url: '//evil.example/x', ready: true })).toBeNull();
    expect(downloadUrlOf('StartAudit', { url: '/x', ready: true })).toBeNull();
  });
});

describe('seoTrend', () => {
  const item = (at: string, score: number | null, version: string, status = 'Completed'): QueryResultItem => ({
    id: at, values: [{ key: 'StartedAtUtc', value: at }, { key: 'Score', value: score }, { key: 'ScoringVersion', value: version }, { key: 'Status', value: status }],
  });

  it('plots completed runs oldest first and marks where the scoring changed', () => {
    const t = seoTrend([item('2026-10-03', 80, 'v2'), item('2026-10-01', 70, 'v1'), item('2026-10-02', 75, 'v1'), item('2026-10-04', 50, 'v2', 'Failed')]);
    expect(t.points.map(p => (p as { y: number }).y)).toEqual([70, 75, 80]);
    expect(t.changes.map(d => d.toISOString().slice(0, 10))).toEqual(['2026-10-03']);
  });
});

describe('SeoCatalogueService', () => {
  it('fetches the catalogue in the language on screen, once per language', async () => {
    const language = { ...languageStub, language: languageStub.language };
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SparkLanguageService, useValue: language }],
    });
    const http = TestBed.inject(HttpTestingController);
    const service = TestBed.inject(SeoCatalogueService);
    TestBed.tick();

    http.expectOne('/seotester/catalogue?culture=en').flush({ culture: 'en', categories: [], severities: [], checks: [{ id: 'a.b', title: 'A' }] });
    await Promise.resolve();
    expect(service.byId().get('a.b')?.title).toBe('A');

    language.language.set('nl-BE');
    TestBed.tick();
    http.expectOne('/seotester/catalogue?culture=nl').flush({ culture: 'nl', categories: [], severities: [], checks: [] });
    language.language.set('en');
    TestBed.tick();
    http.expectNone('/seotester/catalogue?culture=en');
    http.verify();
  });
});

describe('routes', () => {
  // The shell's own path is '': an empty-path redirect among its children never runs, and '/' rendered an empty page.
  it('redirects / to the landing page at the top level, before the shell', () => {
    const redirect = routes.findIndex(r => r.path === '' && r.redirectTo === 'home' && r.pathMatch === 'full');
    const shell = routes.findIndex(r => r.path === '' && r.component !== undefined);
    expect(redirect).toBeGreaterThanOrEqual(0);
    expect(redirect).toBeLessThan(shell);
  });
});
