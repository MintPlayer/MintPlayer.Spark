import { SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { SparkAttributeRendererRegistration, withAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { SparkFileColumnComponent } from './spark-file-column.component';
import { SparkFileDetailComponent } from './spark-file-detail.component';
import { SparkFileEditComponent } from './spark-file-edit.component';

/**
 * The file renderers as the default of every `BinaryFile` and `Image` attribute (#700 SP6): upload in the form, name,
 * size and download (an image drawn) on the detail page, name or thumbnail in a grid cell.
 */
export const sparkFileRenderers: readonly SparkAttributeRendererRegistration[] = [
  {
    name: 'spark-file',
    dataType: 'BinaryFile',
    editComponent: SparkFileEditComponent,
    detailComponent: SparkFileDetailComponent,
    columnComponent: SparkFileColumnComponent,
  },
  {
    name: 'spark-image',
    dataType: 'Image',
    editComponent: SparkFileEditComponent,
    detailComponent: SparkFileDetailComponent,
    columnComponent: SparkFileColumnComponent,
  },
];

/**
 * File attributes in the client (#700), for `provideSpark(...)`: the upload, detail and column renderers of every
 * `BinaryFile` and `Image` attribute. The server half is `spark.AddFiles()`; without this, a form shows a notice where
 * a file attribute would be. An attribute's own `renderer`, and an app's `withAttributeRenderers` registration for the
 * same data type, still win.
 */
export function withFileAttributes(): SparkFeature {
  return withAttributeRenderers(sparkFileRenderers, SparkFeatureOrder.builtInRegistrations);
}
