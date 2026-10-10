import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SeoCatalogueCheck, SeoCatalogueService } from '../seo-catalogue.service';
import { asRows, str } from '../rows';

/** A page's finding, its catalogue title and its summary with the page's own values filled in. Pure, tested on its own. */
export function seoPageFindings(value: unknown, catalogue: ReadonlyMap<string, SeoCatalogueCheck>) {
  const rank = (s: string) => (s === 'Fail' ? 0 : s === 'Warn' ? 1 : s === 'Error' ? 2 : s === 'Pass' ? 3 : 4);
  return asRows(value).map(row => {
    const id = str(row, 'CheckId') ?? '';
    const status = str(row, 'Status') ?? 'Error';
    const check = catalogue.get(id);
    let detail: Record<string, unknown> = {};
    try { detail = JSON.parse(str(row, 'Detail') ?? '{}') ?? {}; } catch { /* not JSON: no parameters */ }
    const summary = check?.summary?.replace(/\{([a-zA-Z]+)\}/g, (all, name: string) => detail[name] == null ? all : String(detail[name]));
    return {
      id, status, title: check?.title ?? id,
      summary: summary && !/\{[a-zA-Z]+\}/.test(summary) ? summary : null,
    };
  }).sort((a, b) => rank(a.status) - rank(b.status) || a.id.localeCompare(b.id));
}

/** `seo-page-findings`, the renderer of `PageResult.Findings` (PRD F6): the page's checks, issues first. */
@Component({
  selector: 'app-seo-page-findings',
  imports: [BsBadgeComponent, TranslateKeyPipe],
  template: `
    @let rows = findings();
    @if (rows.length === 0) { <span class="text-muted">—</span> }
    <ul class="list-unstyled mb-0">
      @for (f of rows; track f.id) {
        <li class="py-1" [attr.data-check]="f.id">
          <bs-badge [type]="color(f.status)">{{ ('seoUi.status.' + f.status) | t }}</bs-badge>
          <span class="ms-2">{{ f.title }}</span>
          @if (f.summary && f.status !== 'Pass') { <div class="small text-body-secondary ms-1">{{ f.summary }}</div> }
        </li>
      }
    </ul>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeoPageFindingsComponent {
  readonly value = input<unknown>();
  private readonly catalogue = inject(SeoCatalogueService);
  protected readonly lang = inject(SparkLanguageService);
  protected readonly findings = computed(() => seoPageFindings(this.value(), this.catalogue.byId()));

  protected color(status: string): Color {
    return status === 'Fail' ? Color.danger : status === 'Warn' ? Color.warning : status === 'Pass' ? Color.success : Color.secondary;
  }
}
