import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { SparkCellColumn } from '@mintplayer/ng-spark/models';
import { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

@Component({
  selector: 'app-color-column-renderer',
  standalone: true,
  template: `
    @if (value(); as colorVal) {
      <span class="d-inline-block align-middle border rounded"
            [style.background-color]="colorVal"
            style="width: 1.5em; height: 1.5em;"></span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ColorColumnRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  column = input<SparkCellColumn>();
  options = input<Record<string, any>>();
}
