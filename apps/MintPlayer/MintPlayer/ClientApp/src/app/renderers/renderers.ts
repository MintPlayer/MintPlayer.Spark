import { SparkAttributeRendererRegistration } from '@mintplayer/ng-spark/renderers';
import { ColorSwatchComponent } from './color-swatch.component';
import { MediaLinkComponent } from './media-link.component';

/** MintPlayer's attribute renderers, by the names App_Data/Model uses. */
export const mintPlayerRenderers: SparkAttributeRendererRegistration[] = [
  { name: 'media-player', detailComponent: MediaLinkComponent, columnComponent: MediaLinkComponent },
  { name: 'color-swatch', detailComponent: ColorSwatchComponent, columnComponent: ColorSwatchComponent },
];
