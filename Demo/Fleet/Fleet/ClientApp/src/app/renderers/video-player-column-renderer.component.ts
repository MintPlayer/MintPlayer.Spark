import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { EntityAttributeDefinition, SparkAttributeColumnRenderer } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-video-player-column-renderer',
  standalone: true,
  template: `
    @if (thumbnailUrl(); as thumb) {
      <a [href]="value()" target="_blank" title="Watch video">
        <img [src]="thumb" alt="Video thumbnail" style="height: 40px;" />
      </a>
    } @else if (value(); as url) {
      <a [href]="url" target="_blank">{{ url }}</a>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class VideoPlayerColumnRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();

  private static readonly YT_REGEX = /(?:youtube\.com\/watch\?v=|youtu\.be\/)([\w-]+)/;

  thumbnailUrl = computed(() => {
    const url = this.value();
    if (!url) return null;
    const match = VideoPlayerColumnRendererComponent.YT_REGEX.exec(url);
    if (!match) return null;
    return `https://img.youtube.com/vi/${match[1]}/default.jpg`;
  });
}
