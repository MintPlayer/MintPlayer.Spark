import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { SparkIconRegistry } from './spark-icon-registry';

@Component({
  selector: 'spark-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span [innerHTML]="iconHtml()"></span>`,
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

  iconHtml = computed(() => {
    const iconName = this.name();
    const icon = this.registry.get(iconName);
    if (!icon) {
      console.warn(`Icon "${iconName}" not registered. Register it via SparkIconRegistry.`);
    }
    return icon;
  });
}
