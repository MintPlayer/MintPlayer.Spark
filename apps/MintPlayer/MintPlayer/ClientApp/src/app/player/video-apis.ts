import { Injectable, signal } from '@angular/core';
import { ApiPlugin, IApiService } from '@mintplayer/player-provider';
import { findApis } from '@mintplayer/video-player';
import { youtubePlugin } from '@mintplayer/youtube-player';
import { vimeoPlugin } from '@mintplayer/vimeo-player';
import { soundCloudPlugin } from '@mintplayer/soundcloud-player';
import { dailymotionPlugin } from '@mintplayer/dailymotion-player';

/** The sites the player can play, as on the legacy site. Shared by `provideVideoApis` and {@link VideoApis}. */
export const videoPlugins: ApiPlugin[] = [youtubePlugin, vimeoPlugin, soundCloudPlugin, dailymotionPlugin];

/**
 * Whether a URL is playable, by the player's own URL patterns. ng-video-player keeps its loaded plugins
 * private, so they are loaded once more here (the imports are lazy chunks, cached after the first).
 */
@Injectable({ providedIn: 'root' })
export class VideoApis {
  private readonly apis = signal<IApiService[]>([]);

  constructor() {
    Promise.all(videoPlugins.map(plugin => plugin())).then(apis => this.apis.set(apis));
  }

  /** Reactive: false until the plugins have loaded. */
  isPlayable(url: string | null | undefined): boolean {
    const apis = this.apis();
    return !!url && apis.length > 0 && findApis(url, apis).length > 0;
  }
}
