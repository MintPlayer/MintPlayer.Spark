import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { IconRegistry } from './icon-registry';

@Component({
  selector: 'app-icon',
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
export class IconComponent {
  private registry = inject(IconRegistry);

  name = input.required<string>();

  iconHtml = computed(() => {
    const iconName = this.name();
    const icon = this.registry.get(iconName);
    if (!icon) {
      console.warn(`Icon "${iconName}" not registered. Add it to icon-registry.ts`);
    }
    return icon;
  });
}
