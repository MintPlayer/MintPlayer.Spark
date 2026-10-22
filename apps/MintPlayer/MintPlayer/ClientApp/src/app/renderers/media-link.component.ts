import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * The `media-player` renderer of `Medium.Value`: the medium's URL as a link, with the site's icon. Only http(s) URLs
 * become links; anything else is shown as text, so a stored `javascript:` value can never be clicked.
 */
@Component({
  selector: 'app-media-link',
  templateUrl: './media-link.component.html',
  styleUrl: './media-link.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MediaLinkComponent implements SparkAttributeDetailRenderer, SparkAttributeColumnRenderer {
  readonly value = input<any>();

  protected readonly url = computed(() => {
    const value = this.value();
    if (typeof value !== 'string') return null;
    try {
      const url = new URL(value);
      return url.protocol === 'https:' || url.protocol === 'http:' ? url : null;
    } catch {
      return null;
    }
  });

  protected readonly icon = computed(() => {
    const host = this.url()?.hostname.replace(/^www\./, '') ?? '';
    if (host === 'youtu.be' || host.endsWith('youtube.com')) return 'bi-youtube';
    if (host.endsWith('spotify.com')) return 'bi-spotify';
    if (host.endsWith('soundcloud.com')) return 'bi-soundwave';
    if (host.endsWith('wikipedia.org')) return 'bi-wikipedia';
    return 'bi-link-45deg';
  });
}
