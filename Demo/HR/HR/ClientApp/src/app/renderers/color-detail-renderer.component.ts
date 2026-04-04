import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { EntityAttributeDefinition, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-color-detail-renderer',
  standalone: true,
  template: `
    @if (value(); as colorVal) {
      <span class="d-inline-block align-middle border rounded me-2"
            [style.background-color]="colorVal"
            style="width: 1.5em; height: 1.5em;"></span>
      {{ colorVal }}
    } @else {
      -
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ColorDetailRendererComponent implements SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
  formData = input<Record<string, any>>({});
}
