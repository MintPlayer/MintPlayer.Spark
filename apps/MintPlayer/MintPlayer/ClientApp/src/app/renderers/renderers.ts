import { SparkAttributeRendererRegistration } from '@mintplayer/ng-spark/renderers';
import { ColorSwatchComponent } from './color-swatch.component';
import { MediaLinkComponent } from './media-link.component';
import { MediaPlayButtonComponent } from './media-play-button.component';

/** MintPlayer's attribute renderers, by the names App_Data/Model uses. */
export const mintPlayerRenderers: SparkAttributeRendererRegistration[] = [
  // The play button leads each row of a Media table (Song, Artist, Person).
  { name: 'media-player', detailComponent: MediaLinkComponent, columnComponent: MediaLinkComponent, leadingComponent: MediaPlayButtonComponent },
  { name: 'color-swatch', detailComponent: ColorSwatchComponent, columnComponent: ColorSwatchComponent },
];
