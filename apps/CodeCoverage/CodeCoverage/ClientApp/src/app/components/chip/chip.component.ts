import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** The theme colours a chip takes: the six whose pairs were measured in both themes (merge-queue PRD §0 D75, D84). */
export type ChipColor = 'primary' | 'secondary' | 'success' | 'info' | 'warning' | 'danger';

/**
 * A short coloured label: a state, a status, a step number (merge-queue PRD §0 D75, D84).
 *
 * Bootstrap 5.3's theme pair for both themes: the subtle background with its emphasis text and a subtle border. Every
 * colour reads at 7.21:1 or more in light and 7.15:1 or more in dark (Chromium, the app's compiled Bootstrap).
 * - Not `<bs-badge>`: its text is always white, which is 1.63:1 on warning and 1.96:1 on info. Its `[type]` is the only
 *   way to colour it; a `text-bg-*` class on its host never reaches the inner `.badge`, which stays `bg-primary`.
 * - Not a bare `.badge`: ng-bootstrap styles `.badge` only inside `<bs-badge>`, so the chip carries its own shape.
 * - Not `text-bg-*`: white text on the solid colour is 4.5:1 on primary, success and danger, against 7:1 here.
 */
@Component({
  selector: 'app-chip',
  template: '<ng-content />',
  styleUrl: './chip.component.scss',
  host: { '[class]': 'classes()' },
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ChipComponent {
  readonly color = input.required<ChipColor>();

  readonly classes = computed(() => {
    const c = this.color();
    return `bg-${c}-subtle text-${c}-emphasis border border-${c}-subtle`;
  });
}
