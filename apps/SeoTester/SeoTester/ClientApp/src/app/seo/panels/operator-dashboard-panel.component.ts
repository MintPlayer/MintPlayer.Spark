import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkQueryCardComponent } from '@mintplayer/ng-spark/grid';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { firstValueFrom } from 'rxjs';

/** `GET /seotester/admin/dashboard` (Admin/OperatorDashboard.cs). */
export interface SeoDashboard {
  generatedAtUtc: string;
  counts: { users: number; organizations: number; sites: number; verifiedSites: number; audits: number; pageResults: number };
  audits: { lastDay: number; lastWeek: number; running: number; byStatusLastWeek: Record<string, number> };
  refusalsLastWeek: Record<string, number>;
  database: { sizeOnDiskBytes: number | null; documents: number; collections: { name: string; documents: number; sizeBytes: number | null }[] };
  sites: { id: string; origin: string; organization: string | null; personal: boolean; verified: boolean; latestScore: number | null; latestGrade: string | null; latestRunAtUtc: string | null; createdAtUtc: string | null }[];
}

/** 1536 → "1.5 KB": binary units, one decimal from KB up. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || !Number.isFinite(bytes)) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return unit === 0 ? `${value} B` : `${value.toFixed(1)} ${units[unit]}`;
}

/**
 * The operator dashboard's figures (PRD F16, D28), drawn under the virtual `OperatorDashboard` page (whose own content
 * is the abuse log): usage, refusals, the database's size per collection, and every organization's sites.
 */
@Component({
  selector: 'app-seo-operator-dashboard-panel',
  imports: [BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent, SparkQueryCardComponent, TranslateKeyPipe],
  styles: [`
    .seo-tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(10rem, 1fr)); gap: .75rem; }
    .seo-pair { display: grid; grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr)); gap: 1rem; }
  `],
  templateUrl: './operator-dashboard-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeoOperatorDashboardPanelComponent {
  private readonly http = inject(HttpClient);
  protected readonly lang = inject(SparkLanguageService);
  protected readonly data = signal<SeoDashboard | null>(null);
  protected readonly failed = signal(false);
  protected readonly bytes = formatBytes;

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    try {
      this.data.set(await firstValueFrom(this.http.get<SeoDashboard>('/seotester/admin/dashboard')));
      this.failed.set(false);
    } catch {
      this.failed.set(true);
    }
  }

  protected entries(record: Record<string, number> | undefined): { key: string; count: number }[] {
    return Object.entries(record ?? {}).map(([key, count]) => ({ key, count }));
  }

  protected date(value: string | null): string {
    if (!value) return '—';
    const d = new Date(value);
    return Number.isNaN(d.getTime()) ? value : d.toLocaleDateString(this.lang.language());
  }
}
