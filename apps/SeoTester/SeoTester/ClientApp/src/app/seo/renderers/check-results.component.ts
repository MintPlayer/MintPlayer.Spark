import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SEO_CATEGORY_ORDER, SEO_SEVERITY_ORDER, SeoCatalogueCheck, SeoCatalogueService, SeoSnippetPlatform } from '../seo-catalogue.service';
import { asRows, num, str, strings } from '../rows';

/** Which results the list shows. */
export type SeoResultFilter = 'issues' | 'notMeasured' | 'passed' | 'all';

/** One check's result on a run, joined with its catalogue entry. */
export interface SeoCheckRow {
  id: string;
  status: string;
  affected: number;
  sampleUrls: string[];
  delta: string | null;
  note: string | null;
  check: SeoCatalogueCheck | null;
  category: string;
  severity: string;
  title: string;
  summary: string | null;
  /** Fail/Warn first, then severity, then weight × affected pages (PRD F5's priority). */
  priority: number;
}

const HOW_TO_FIX_KEY = 'seotester.howToFixOpen';
const PLATFORMS: { key: SeoSnippetPlatform; label: string; language: string }[] = [
  { key: 'nginx', label: 'nginx', language: 'nginx' },
  { key: 'apache', label: 'Apache', language: 'apache' },
  { key: 'iis', label: 'IIS / ASP.NET Core', language: '' }, // web.config or C#: the snippet component detects it
  { key: 'html', label: 'HTML', language: 'html' },
];

/** Whether a result is an issue (failed or warned). */
export const isIssue = (status: string) => status === 'Fail' || status === 'Warn';
/** Whether a result was not measured (an error, or not applicable with a reason). */
export const isNotMeasured = (status: string, note: string | null) => status === 'Error' || (status === 'NotApplicable' && !!note);

/**
 * Joins a run's `CheckResults` with the catalogue and orders them: issues first (fail before warn), then by severity,
 * then by weight × affected pages. Pure, so it is tested on its own.
 */
export function seoCheckRows(value: unknown, catalogue: ReadonlyMap<string, SeoCatalogueCheck>): SeoCheckRow[] {
  return asRows(value).map(row => {
    const id = str(row, 'CheckId') ?? '';
    const check = catalogue.get(id) ?? null;
    const status = str(row, 'Status') ?? 'Error';
    const affected = num(row, 'Affected') ?? 0;
    const category = check?.category ?? (id.split('.')[0] ? id.split('.')[0][0].toUpperCase() + id.split('.')[0].slice(1) : 'Seo');
    const severity = check?.severity ?? 'Info';
    const statusRank = status === 'Fail' ? 0 : status === 'Warn' ? 1 : 2;
    const severityRank = Math.max(0, SEO_SEVERITY_ORDER.indexOf(severity));
    const summary = check?.summary ? check.summary.replace(/\{n\}/g, String(affected)) : null;
    return {
      id, status, affected, check, category, severity,
      sampleUrls: strings(row, 'SampleUrls'),
      delta: str(row, 'Delta'),
      note: str(row, 'Note'),
      title: check?.title ?? id,
      // A template with placeholders the run does not keep ({url}, {value}) is the page view's, not the run's.
      summary: summary && !/\{[a-z]+\}/i.test(summary) ? summary : null,
      priority: statusRank * 1_000_000 + severityRank * 10_000 - Math.min(9_999, (check?.weight ?? 0) * Math.max(1, affected)),
    };
  }).sort((a, b) => a.priority - b.priority || a.id.localeCompare(b.id));
}

/**
 * `seo-check-results`, the full-width renderer of `AuditRun.CheckResults` (PRD F5, §4.5): every check's result,
 * grouped by category, issues first, with what changed since the previous run (New / Fixed / Unchanged), why a check
 * was not measured, the sample pages, and the catalogue's "how to fix" (a translated explanation and a snippet per
 * platform). Also the share page's cell for that attribute.
 */
@Component({
  selector: 'app-seo-check-results',
  imports: [BsBadgeComponent, BsCodeSnippetComponent, TranslateKeyPipe],
  templateUrl: './check-results.component.html',
  styles: [`
    .seo-filter .btn { --bs-btn-padding-y: .2rem; }
    .seo-check { border-top: 1px solid var(--bs-border-color); padding: .6rem 0; }
    .seo-check:first-child { border-top: 0; }
    .seo-check-main { flex: 1 1 0; min-width: 12rem; }
    .seo-urls { font-family: var(--bs-font-monospace); font-size: .8rem; word-break: break-all; }
    .seo-status { min-width: 6.5rem; display: inline-block; text-align: center; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeoCheckResultsComponent {
  readonly value = input<unknown>();

  protected readonly catalogue = inject(SeoCatalogueService);
  private readonly lang = inject(SparkLanguageService);
  protected readonly colors = Color;
  protected readonly platforms = PLATFORMS;

  protected readonly filter = signal<SeoResultFilter>('issues');
  /** "How to fix" opens by default when the viewer last chose so (PRD F5: a preference remembers the default). */
  protected readonly howToFixOpen = signal(readHowToFix());
  protected readonly expanded = signal<ReadonlySet<string>>(new Set());

  protected readonly rows = computed(() => seoCheckRows(this.value(), this.catalogue.byId()));

  protected readonly counts = computed(() => {
    const rows = this.rows();
    return {
      issues: rows.filter(r => isIssue(r.status)).length,
      notMeasured: rows.filter(r => isNotMeasured(r.status, r.note)).length,
      passed: rows.filter(r => r.status === 'Pass').length,
      all: rows.length,
    } satisfies Record<SeoResultFilter, number>;
  });

  protected readonly groups = computed(() => {
    const filter = this.filter();
    const rows = this.rows().filter(r =>
      filter === 'all' ? true
        : filter === 'issues' ? isIssue(r.status)
          : filter === 'passed' ? r.status === 'Pass'
            : isNotMeasured(r.status, r.note));
    const order = (c: string) => { const i = SEO_CATEGORY_ORDER.indexOf(c); return i < 0 ? 99 : i; };
    const categories = [...new Set(rows.map(r => r.category))].sort((a, b) => order(a) - order(b));
    return categories.map(category => ({ category, rows: rows.filter(r => r.category === category) }));
  });

  protected readonly filters: SeoResultFilter[] = ['issues', 'notMeasured', 'passed', 'all'];

  protected setHowToFix(open: boolean): void {
    this.howToFixOpen.set(open);
    this.expanded.set(new Set());
    try { localStorage.setItem(HOW_TO_FIX_KEY, open ? '1' : '0'); } catch { /* private window: per page only */ }
  }

  /** Whether a row's how-to-fix is shown: the default, flipped for the rows the viewer toggled. */
  protected isOpen(id: string): boolean {
    return this.howToFixOpen() !== this.expanded().has(id);
  }

  protected toggle(id: string): void {
    const next = new Set(this.expanded());
    if (!next.delete(id)) next.add(id);
    this.expanded.set(next);
  }

  protected hasFix(row: SeoCheckRow): boolean {
    return !!(row.check?.howToFix || row.check?.docsUrl || this.snippetPlatforms(row).length);
  }

  protected snippetPlatforms(row: SeoCheckRow) {
    return PLATFORMS.filter(p => !!row.check?.snippets?.[p.key]);
  }

  protected statusColor(status: string): Color {
    return status === 'Fail' ? Color.danger : status === 'Warn' ? Color.warning : status === 'Pass' ? Color.success : Color.secondary;
  }

  protected statusLabel(row: SeoCheckRow): string {
    if (isNotMeasured(row.status, row.note)) return this.lang.t('seoUi.status.notMeasured', 'Not measured');
    return this.lang.t(`seoUi.status.${row.status}`, row.status);
  }

  protected severityColor(severity: string): Color {
    // Readable on both themes: 'light' drew white text on a white badge in dark mode.
    return severity === 'Critical' ? Color.danger : severity === 'High' ? Color.warning : severity === 'Medium' ? Color.info : Color.secondary;
  }
}

function readHowToFix(): boolean {
  try { return localStorage.getItem(HOW_TO_FIX_KEY) === '1'; } catch { return false; }
}
