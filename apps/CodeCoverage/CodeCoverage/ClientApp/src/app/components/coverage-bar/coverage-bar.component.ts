import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CommonModule, DecimalPipe } from '@angular/common';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsProgressComponent, BsProgressBarComponent } from '@mintplayer/ng-bootstrap/progress-bar';
import { CoverageSummary, coveragePercent } from '../../services/browse.service';

/** Compact coverage percentage with a colored progress bar, for table cells. */
@Component({
  selector: 'app-coverage-bar',
  imports: [CommonModule, DecimalPipe, BsProgressComponent, BsProgressBarComponent],
  template: `
    @if (percent() !== null) {
      <div class="d-flex align-items-center gap-2">
        <bs-progress style="width: 80px; height: 8px;">
          <bs-progress-bar [value]="percent()!" [minimum]="0" [maximum]="100" [color]="color()"
                           [ariaLabel]="'Coverage'" [valueText]="(percent() | number:'1.1-1') + '%'" />
        </bs-progress>
        <span class="small text-nowrap">{{ percent() | number:'1.1-1' }}%</span>
      </div>
    } @else {
      <span class="text-muted small">—</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageBarComponent {
  summary = input<CoverageSummary | null | undefined>();

  readonly percent = computed(() => coveragePercent(this.summary()));

  readonly color = computed(() => {
    const p = this.percent();
    if (p === null) return Color.secondary;
    return p >= 80 ? Color.success : p >= 60 ? Color.warning : Color.danger;
  });
}
