import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { EntityAttributeDefinition, SparkAttributeColumnRenderer, SparkIconComponent } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-video-player-column-renderer',
  standalone: true,
  imports: [SparkIconComponent],
  template: `
    @if (value(); as url) {
      <a [href]="url" target="_blank" title="Watch video">
        <spark-icon name="play-circle" />
      </a>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class VideoPlayerColumnRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
}
