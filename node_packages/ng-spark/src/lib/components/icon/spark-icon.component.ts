import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { SparkIconRegistry } from './spark-icon-registry';

@Component({
  selector: 'spark-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (iconHtml(); as html) {
      <span [innerHTML]="html"></span>
    } @else {
      <i class="bi" [class]="cssFallbackClass()"></i>
    }
  `,
  styles: [`
    :host {
      display: inline-flex;
      align-items: center;
      justify-content: center;
    }
    span {
      display: inline-flex;
      align-items: center;
    }
    span ::ng-deep svg {
      width: 1em;
      height: 1em;
      fill: currentColor;
    }
  `]
})
export class SparkIconComponent {
  private registry = inject(SparkIconRegistry);

  name = input.required<string>();

  iconHtml = computed(() => this.registry.get(this.name()));

  cssFallbackClass = computed(() => `bi-${this.name()}`);
}
