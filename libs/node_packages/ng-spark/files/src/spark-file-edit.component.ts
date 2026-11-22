import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { BsFileUploadComponent, FileUpload } from '@mintplayer/ng-bootstrap/file-upload';
import { BsProgressBarComponent, BsProgressComponent } from '@mintplayer/ng-bootstrap/progress-bar';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import {
  EntityAttributeDefinition,
  formatFileSize,
  isDisplayableImageUrl,
  sparkFileScanStatus,
  sparkImageUrl,
  SparkImageValue,
} from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { sparkCheckedDownloadUrl, variantNameOf } from './spark-file-download-url';
import {
  acceptAttribute,
  isPreviewableImage,
  maxFiles,
  moveRow,
  rowsFromValue,
  sameFiles,
  SparkFileRow,
  SparkNewFileRow,
  valueFromRows,
} from './spark-file-rows';
import { SparkCommittedFile, SparkFilesService } from './spark-files.service';

let nextKey = 0;

/**
 * The edit renderer of a `BinaryFile`/`Image` attribute (#700), registered by `withFileAttributes()`.
 *
 * Choosing or dropping a file prepares a slot, sends the bytes (straight to the store when it offers a presigned URL)
 * and commits them; the row shows the progress, then "Checking…", "Scanning for malware…" while a deferred scan runs,
 * or why the server refused the file. Only a file the server answered `Clean` is written to the form, as
 * `{ uploadToken }`, which the save claims. Stored files are echoed back as read, which keeps them; removing a row
 * drops it from the value, and the save then deletes the file. A list attribute adds, removes and reorders up to
 * `fileOptions.maxCount` files; a single one replaces its file.
 *
 * An attribute that holds a URL string (`storesFile` absent) gets a URL input instead.
 */
@Component({
  selector: 'spark-file-edit',
  imports: [BsFileUploadComponent, BsProgressComponent, BsProgressBarComponent, TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!storesFile()) {
      <input type="url" class="form-control spark-file-url" [value]="urlValue()" (input)="onUrlInput($event)" />
    } @else {
      @if (rows().length > 0) {
        <ul class="list-unstyled mb-2 spark-file-rows">
          @for (row of rows(); track row.key; let i = $index, last = $last) {
            <li class="d-flex align-items-center gap-2 py-1 spark-file-row" [attr.data-file]="rowName(row)" [attr.data-phase]="row.kind === 'new' ? row.phase : 'stored'">
              @if (thumbnail(row); as src) {
                <img class="spark-file-thumb rounded" [src]="src" alt="" (error)="onThumbnailError(row)" />
              }
              <div class="flex-grow-1 text-break">
                <div>{{ rowName(row) }} <span class="text-body-secondary small">{{ rowSize(row) }}</span></div>
                @if (row.kind === 'new') {
                  @switch (row.phase) {
                    @case ('uploading') {
                      <bs-progress [style.height]="'.4rem'">
                        <bs-progress-bar [value]="percent(row)" [minimum]="0" [maximum]="100" />
                      </bs-progress>
                      <span class="visually-hidden">{{ 'files.uploading' | t }}</span>
                    }
                    @case ('checking') { <div class="small text-body-secondary spark-file-status">{{ 'files.checking' | t }}</div> }
                    @case ('scanning') { <div class="small text-body-secondary spark-file-status">{{ 'files.scanning' | t }}</div> }
                    @case ('refused') { <div class="small text-danger spark-file-error" role="alert">{{ 'files.refused' | t }}: {{ errorText(row) }}</div> }
                    @case ('failed') { <div class="small text-danger spark-file-error" role="alert">{{ errorText(row) }}</div> }
                  }
                } @else if (rowStatus(row) !== 'Clean') {
                  <div class="small text-body-secondary spark-file-status">{{ (rowStatus(row) === 'Pending' ? 'files.scanning' : 'files.unavailable') | t }}</div>
                }
              </div>
              @if (isArray()) {
                <button type="button" class="btn btn-sm btn-outline-secondary spark-file-up" [disabled]="i === 0" (click)="move(i, -1)"
                        [attr.aria-label]="'files.moveUp' | t" [title]="'files.moveUp' | t">↑</button>
                <button type="button" class="btn btn-sm btn-outline-secondary spark-file-down" [disabled]="last" (click)="move(i, 1)"
                        [attr.aria-label]="'files.moveDown' | t" [title]="'files.moveDown' | t">↓</button>
              }
              <button type="button" class="btn btn-sm btn-outline-danger spark-file-remove" (click)="remove(row)">{{ 'files.remove' | t }}</button>
            </li>
          }
        </ul>
      }
      @if (canAdd()) {
        <bs-file-upload class="spark-file-drop" [(files)]="dropped" [accept]="accept()" [multiple]="isArray()"
                        [dropFilesCaption]="'files.drop' | t" [browseFilesCaption]="chooseCaption() | t"
                        [label]="chooseCaption() | t" [inputLabel]="chooseCaption() | t"
                        (filesDropped)="onFiles($event)" />
      } @else if (isArray()) {
        <div class="small text-body-secondary spark-file-full">{{ 'files.tooMany' | t }}</div>
      }
    }
  `,
  styles: [`
    .spark-file-thumb { width: 3rem; height: 3rem; object-fit: cover; flex: none; }
  `],
})
export class SparkFileEditComponent {
  private readonly files = inject(SparkFilesService);
  private readonly lang = inject(SparkLanguageService);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';
  private readonly abort = new AbortController();

  readonly value = input<unknown>(null);
  readonly attribute = input<EntityAttributeDefinition | undefined>(undefined);
  readonly options = input<Record<string, any> | undefined>(undefined);
  readonly valueChange = input<((value: unknown) => void) | undefined>(undefined);
  readonly objectTypeId = input<string | undefined>(undefined);
  readonly objectId = input<string | undefined>(undefined);
  /** In an AsDetail row (M10): the path on the document, `Parent.Field`; the upload is bound to the document and that path. */
  readonly attributePath = input<string | undefined>(undefined);

  protected readonly rows = signal<SparkFileRow[]>([]);
  protected readonly dropped = signal<FileUpload[]>([]);
  /** Stored rows whose thumbnail URL failed and now show the checked download. */
  private readonly fallbacks = signal<ReadonlySet<string>>(new Set());
  /** What this component last wrote; a value that differs came from elsewhere (a reload) and resets the rows. */
  private lastWritten: unknown = undefined;

  protected readonly storesFile = computed(() => !!this.attribute()?.storesFile);
  protected readonly isArray = computed(() => !!this.attribute()?.isArray);
  protected readonly accept = computed(() => acceptAttribute(this.attribute()?.fileOptions));
  private readonly max = computed(() => maxFiles(this.isArray(), this.attribute()?.fileOptions));
  protected readonly canAdd = computed(() => !this.isArray() || this.rows().length < this.max());
  protected readonly chooseCaption = computed(() => this.isArray() ? 'files.chooseMany' : this.rows().length > 0 ? 'files.replace' : 'files.choose');
  protected readonly urlValue = computed(() => typeof this.value() === 'string' ? this.value() as string : '');

  constructor() {
    effect(() => {
      const value = this.value();
      if (!this.storesFile()) return;
      untracked(() => {
        if (this.lastWritten !== undefined && sameFiles(value, this.lastWritten)) return;
        this.lastWritten = value;
        this.replaceRows(rowsFromValue(value));
      });
    });
    inject(DestroyRef).onDestroy(() => {
      this.abort.abort();
      for (const row of this.rows()) revokePreview(row);
    });
  }

  protected onUrlInput(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.valueChange()?.(text === '' ? null : text);
  }

  protected async onFiles(batch: FileUpload[]): Promise<void> {
    this.dropped.set([]);
    const attribute = this.attribute();
    const objectTypeId = this.objectTypeId();
    if (!attribute || !objectTypeId || batch.length === 0) return;

    const maxBytes = attribute.fileOptions?.maxBytes;
    const room = this.isArray() ? Math.max(0, this.max() - this.rows().length) : 1;
    const chosen = batch.map(u => u.file).slice(0, room);
    const added: SparkNewFileRow[] = chosen.map(file => ({
      kind: 'new',
      key: `new:${nextKey++}`,
      file,
      sent: 0,
      phase: maxBytes !== undefined && file.size > maxBytes ? 'failed' : 'uploading',
      error: maxBytes !== undefined && file.size > maxBytes ? 'files.tooLarge' : undefined,
      preview: isPreviewableImage(file) ? URL.createObjectURL(file) : undefined,
    }));
    if (batch.length > room) {
      added.push(...batch.slice(room).map(u => ({
        kind: 'new' as const, key: `new:${nextKey++}`, file: u.file, sent: 0, phase: 'failed' as const, error: 'files.tooMany',
      })));
    }
    this.replaceRows(this.isArray() ? [...this.rows(), ...added] : added);
    this.write();

    const sending = added.filter(r => r.phase === 'uploading');
    if (sending.length === 0) return;
    let prepared;
    try {
      prepared = await this.files.prepare(objectTypeId, this.attributePath() ?? attribute.name, this.objectId(), sending.map(r => r.file));
    } catch (error) {
      for (const row of sending) this.update(row.key, { phase: 'failed', error: SparkFilesService.errorText(error) ?? 'files.failed' });
      return;
    }
    await Promise.all(sending.map((row, i) => this.send(row, prepared[i])));
  }

  private async send(row: SparkNewFileRow, prepared: Awaited<ReturnType<SparkFilesService['prepare']>>[number] | undefined): Promise<void> {
    if (!prepared) {
      this.update(row.key, { phase: 'failed', error: 'files.failed' });
      return;
    }
    this.update(row.key, { token: prepared.token });
    try {
      await this.files.upload(prepared, row.file, sent => this.update(row.key, { sent }));
      if (this.abort.signal.aborted || !this.has(row.key)) return;
      this.update(row.key, { phase: 'checking', sent: row.file.size });
      let [committed] = await this.files.commit([prepared.token]);
      if (committed && committed.error === null && sparkFileScanStatus(committed) === 'Pending') {
        this.update(row.key, { phase: 'scanning' });
        [committed] = await this.files.commitUntilScanned([prepared.token], { signal: this.abort.signal });
      }
      if (this.abort.signal.aborted || !this.has(row.key)) return;
      this.update(row.key, phaseOf(committed));
    } catch (error) {
      if (this.abort.signal.aborted) return;
      this.update(row.key, { phase: 'failed', error: SparkFilesService.errorText(error) ?? 'files.failed' });
    }
    this.write();
  }

  protected remove(row: SparkFileRow): void {
    revokePreview(row);
    this.rows.set(this.rows().filter(r => r.key !== row.key));
    this.write();
  }

  protected move(index: number, delta: number): void {
    this.rows.set(moveRow(this.rows(), index, delta));
    this.write();
  }

  protected rowName(row: SparkFileRow): string {
    return row.kind === 'stored' ? row.value.fileName : row.file.name;
  }

  protected rowSize(row: SparkFileRow): string {
    return formatFileSize(row.kind === 'stored' ? row.value.length : row.file.size);
  }

  protected rowStatus(row: SparkFileRow): string {
    return row.kind === 'stored' ? sparkFileScanStatus(row.value) : '';
  }

  protected percent(row: SparkNewFileRow): number {
    return row.file.size > 0 ? Math.min(100, Math.round(row.sent * 100 / row.file.size)) : 0;
  }

  protected errorText(row: SparkNewFileRow): string {
    const error = row.error ?? 'files.failed';
    return error.startsWith('files.') ? this.lang.t(error) : error;
  }

  /** A stored image's smallest fitting variant (or the checked download once its URL failed); a new image's local preview. */
  protected thumbnail(row: SparkFileRow): string | null {
    if (row.kind === 'new') return row.preview ?? null;
    if (this.attribute()?.dataType !== 'Image') return null;
    const image = row.value as SparkImageValue;
    const url = sparkImageUrl(image, 96);
    if (this.fallbacks().has(row.key)) return this.fallbackUrl(image, url);
    return isDisplayableImageUrl(url) ? url : null;
  }

  protected onThumbnailError(row: SparkFileRow): void {
    if (row.kind !== 'stored' || this.fallbacks().has(row.key)) return;
    this.fallbacks.set(new Set([...this.fallbacks(), row.key]));
  }

  private fallbackUrl(image: SparkImageValue, url: string | null): string | null {
    const objectTypeId = this.objectTypeId();
    const id = this.objectId();
    const attribute = this.attributePath() ?? this.attribute()?.name;
    if (!objectTypeId || !id || !attribute) return null;
    return sparkCheckedDownloadUrl(this.baseUrl, objectTypeId, id, attribute, image.fileId, variantNameOf(image.variants, url));
  }

  private has(key: string): boolean {
    return this.rows().some(r => r.key === key);
  }

  private update(key: string, patch: Partial<SparkNewFileRow>): void {
    this.rows.set(this.rows().map(r => r.key === key && r.kind === 'new' ? { ...r, ...patch } : r));
  }

  private replaceRows(rows: SparkFileRow[]): void {
    const kept = new Set(rows.map(r => r.key));
    for (const row of this.rows()) if (!kept.has(row.key)) revokePreview(row);
    this.rows.set(rows);
    this.fallbacks.set(new Set());
  }

  private write(): void {
    const value = valueFromRows(this.rows(), this.isArray());
    if (sameFiles(value, this.lastWritten)) return;
    this.lastWritten = value;
    this.valueChange()?.(value);
  }
}

function phaseOf(committed: SparkCommittedFile | undefined): Partial<SparkNewFileRow> {
  if (!committed) return { phase: 'failed', error: 'files.failed' };
  if (committed.error !== null) return { phase: 'refused', error: committed.error };
  switch (sparkFileScanStatus(committed)) {
    case 'Clean': return { phase: 'ready' };
    case 'Pending': return { phase: 'scanning' };
    default: return { phase: 'refused', error: 'files.unavailable' };
  }
}

function revokePreview(row: SparkFileRow): void {
  if (row.kind === 'new' && row.preview) URL.revokeObjectURL(row.preview);
}
