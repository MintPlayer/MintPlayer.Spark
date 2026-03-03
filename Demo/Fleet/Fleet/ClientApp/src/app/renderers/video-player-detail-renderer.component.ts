import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { VideoPlayerComponent, provideVideoApis } from '@mintplayer/ng-video-player';
import { youtubePlugin } from '@mintplayer/youtube-player';
import { vimeoPlugin } from '@mintplayer/vimeo-player';
import { dailymotionPlugin } from '@mintplayer/dailymotion-player';
import { soundCloudPlugin } from '@mintplayer/soundcloud-player';
import { filePlugin } from '@mintplayer/file-player';
import { EntityAttributeDefinition, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-video-player-detail-renderer',
  standalone: true,
  imports: [VideoPlayerComponent],
  providers: [provideVideoApis(youtubePlugin, vimeoPlugin, dailymotionPlugin, soundCloudPlugin, filePlugin)],
  template: `
    @if (value(); as url) {
      <video-player
        [width]="options()?.['width'] ?? 480"
        [height]="options()?.['height'] ?? 270"
        [autoplay]="options()?.['autoplay'] ?? false"
        [url]="url">
      </video-player>
    } @else {
      <span class="text-muted">-</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class VideoPlayerDetailRendererComponent implements SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition>();
  options = input<Record<string, any>>();
  formData = input<Record<string, any>>({});
}
