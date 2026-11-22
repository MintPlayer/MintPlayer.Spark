import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import {
  isDisplayableImageUrl,
  isSparkFile,
  SparkCellColumn,
  sparkFileScanStatus,
  sparkImageUrl,
  SparkImageValue,
} from '@mintplayer/ng-spark/models';

/**
 * The grid cell of a `BinaryFile`/`Image` attribute (#700), registered by `withFileAttributes()`: the first file's
 * thumbnail (an image, at its smallest fitting variant) or name, and "+N" for the rest of a list. When the thumbnail
 * fails to load (its URL expired) the cell shows the name; a cell has no object type to build a checked download from.
 */
@Component({
  selector: 'spark-file-column',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (urlString(); as url) {
      @if (column()?.dataType === 'Image' && isDisplayable(url)) {
        <img class="spark-file-cell-thumb rounded" [src]="url" alt="" />
      } @else {
        <span class="text-break">{{ url }}</span>
      }
    } @else if (first(); as file) {
      <span class="d-inline-flex align-items-center gap-1 spark-file-cell" [attr.data-file]="file.fileName">
        @if (thumbnail(); as src) {
          <img class="spark-file-cell-thumb rounded" [src]="src" [alt]="file.fileName" [title]="file.fileName" (error)="failed.set(true)" />
        } @else {
          <span class="text-truncate">{{ file.fileName }}</span>
        }
        @if (more() > 0) {
          <span class="text-body-secondary small spark-file-more">+{{ more() }}</span>
        }
      </span>
    }
  `,
  styles: [`
    .spark-file-cell-thumb { height: 2rem; width: 2rem; object-fit: cover; }
  `],
})
export class SparkFileColumnComponent {
  readonly value = input<unknown>(null);
  readonly column = input<SparkCellColumn | undefined>(undefined);

  protected readonly failed = signal(false);

  protected readonly urlString = computed(() => typeof this.value() === 'string' && this.value() !== '' ? this.value() as string : null);
  private readonly list = computed(() => {
    const value = this.value();
    return (Array.isArray(value) ? value : value ? [value] : []).filter(isSparkFile);
  });
  protected readonly first = computed(() => this.list()[0] ?? null);
  protected readonly more = computed(() => Math.max(0, this.list().length - 1));
  protected readonly thumbnail = computed(() => {
    const file = this.first();
    if (!file || this.failed() || this.column()?.dataType !== 'Image' || sparkFileScanStatus(file) !== 'Clean') return null;
    const url = sparkImageUrl(file as SparkImageValue, 64);
    return isDisplayableImageUrl(url) ? url : null;
  });

  protected isDisplayable(url: string): boolean {
    return isDisplayableImageUrl(url);
  }
}
