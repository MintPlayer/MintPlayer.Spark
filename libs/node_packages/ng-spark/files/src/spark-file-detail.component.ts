import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import {
  EntityAttributeDefinition,
  formatFileSize,
  isDisplayableImageUrl,
  isSparkFile,
  PersistentObject,
  sparkFileScanStatus,
  SparkFileValue,
  sparkImageUrl,
  SparkImageValue,
} from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { sparkCheckedDownloadUrl, variantNameOf } from './spark-file-download-url';

interface DetailFile {
  file: SparkFileValue;
  size: string;
  /** Served: the scan answered `Clean` and the server signed a URL. */
  available: boolean;
  scanning: boolean;
  /** The image as shown (the `medium` variant's size), and the original for the enlarged view. */
  image: string | null;
  full: string | null;
}

/**
 * The detail renderer of a `BinaryFile`/`Image` attribute (#700), registered by `withFileAttributes()`: each file's
 * name and size with a download link, an image drawn at its nearest larger variant (the original on click). A file
 * that is not `Clean` has no URL and reads "Scanning for malware…" or "Not available". An attribute that holds a URL
 * string shows the image (a displayable URL only) or a link.
 *
 * Image and variant URLs live five minutes; when one fails (the page stayed open), the image falls back to the
 * checked download, which is authorized per request and never expires.
 */
@Component({
  selector: 'spark-file-detail',
  imports: [TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (urlString(); as url) {
      @if (attribute()?.dataType === 'Image' && isDisplayable(url)) {
        <img class="spark-file-image img-fluid rounded" [src]="url" alt="" />
      } @else {
        <span class="text-break">{{ url }}</span>
      }
    } @else {
      @for (entry of files(); track entry.file.fileId) {
        <div class="spark-file mb-2" [attr.data-file]="entry.file.fileName">
          @if (entry.image) {
            <button type="button" class="btn p-0 border-0 d-block spark-file-enlarge" (click)="toggle(entry.file.fileId)"
                    [attr.aria-expanded]="enlarged() === entry.file.fileId">
              <img class="spark-file-image img-fluid rounded" [class.spark-file-image-full]="enlarged() === entry.file.fileId"
                   [src]="enlarged() === entry.file.fileId ? (entry.full ?? entry.image) : entry.image" [alt]="entry.file.fileName"
                   (error)="onImageError(entry.file)" />
            </button>
          }
          <div>
            @if (entry.available && entry.file.url) {
              <a class="spark-file-download" [href]="entry.file.url" download>{{ entry.file.fileName }}</a>
            } @else {
              <span>{{ entry.file.fileName }}</span>
            }
            <span class="text-body-secondary small ms-1">{{ entry.size }}</span>
            @if (!entry.available) {
              <span class="text-body-secondary small ms-1 spark-file-status">{{ (entry.scanning ? 'files.scanning' : 'files.unavailable') | t }}</span>
            }
          </div>
        </div>
      }
    }
  `,
  styles: [`
    .spark-file-image { max-height: 20rem; }
    .spark-file-image-full { max-height: none; }
  `],
})
export class SparkFileDetailComponent {
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  readonly value = input<unknown>(null);
  readonly attribute = input<EntityAttributeDefinition | undefined>(undefined);
  readonly item = input<PersistentObject | undefined>(undefined);

  protected readonly enlarged = signal<string | null>(null);
  /** Files whose signed image URL failed: shown through the checked download from then on. */
  private readonly fallbacks = signal<ReadonlySet<string>>(new Set());

  protected readonly urlString = computed(() => typeof this.value() === 'string' && this.value() !== '' ? this.value() as string : null);

  protected readonly files = computed<DetailFile[]>(() => {
    const value = this.value();
    const list = (Array.isArray(value) ? value : value ? [value] : []).filter(isSparkFile);
    const isImage = this.attribute()?.dataType === 'Image';
    const fallbacks = this.fallbacks();
    return list.map(file => {
      const status = sparkFileScanStatus(file);
      const available = status === 'Clean';
      let image: string | null = null;
      let full: string | null = null;
      if (isImage && available) {
        const shown = sparkImageUrl(file as SparkImageValue, 640);
        if (fallbacks.has(file.fileId)) {
          image = this.checked(file, variantNameOf((file as SparkImageValue).variants, shown));
          full = this.checked(file);
        } else {
          image = isDisplayableImageUrl(shown) ? shown : null;
          full = isDisplayableImageUrl(file.url) ? file.url! : null;
        }
      }
      return { file, size: formatFileSize(file.length), available, scanning: status === 'Pending', image, full };
    });
  });

  protected isDisplayable(url: string): boolean {
    return isDisplayableImageUrl(url);
  }

  protected toggle(fileId: string): void {
    this.enlarged.set(this.enlarged() === fileId ? null : fileId);
  }

  protected onImageError(file: SparkFileValue): void {
    if (this.fallbacks().has(file.fileId)) return;
    this.fallbacks.set(new Set([...this.fallbacks(), file.fileId]));
  }

  private checked(file: SparkFileValue, variant?: string): string | null {
    const item = this.item();
    const attribute = this.attribute()?.name;
    if (!item?.id || !item.objectTypeId || !attribute) return null;
    return sparkCheckedDownloadUrl(this.baseUrl, item.objectTypeId, item.id, attribute, file.fileId, variant);
  }
}
