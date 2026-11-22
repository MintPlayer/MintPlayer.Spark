/**
 * The wire shape of a stored file attribute (#700): `BinaryFile` → {@link SparkFileValue}, `Image` → {@link SparkImageValue}.
 * A list attribute holds an array of them. `url` is signed per read (a checked download for a file, a short-lived content
 * URL for an image or a variant) and absent when the file may not be served (not `Clean`).
 */
export interface SparkFileValue {
  fileId: string;
  fileName: string;
  contentType: string;
  length: number;
  sha256?: string | null;
  /** Arrives as a name or as its number; read it with {@link sparkFileScanStatus}. */
  scanStatus?: SparkFileScanStatus | number;
  uploadedAt?: string;
  uploadedBy?: string | null;
  url?: string;
}

export interface SparkImageVariantValue {
  fileId: string;
  contentType: string;
  width: number;
  height: number;
  length: number;
  url?: string;
}

export interface SparkImageValue extends SparkFileValue {
  width: number;
  height: number;
  variants?: Record<string, SparkImageVariantValue>;
}

export type SparkFileScanStatus = 'Pending' | 'Clean' | 'Infected' | 'ScanFailed';

/** What the edit renderer writes for a new file: the save claims the token (single-use, bound to user, type, attribute). */
export interface SparkFileClaim {
  uploadToken: string;
}

/** The data types whose values can be stored files. */
export const SPARK_FILE_DATA_TYPES: readonly string[] = ['BinaryFile', 'Image'];

export function isFileDataType(dataType: string | undefined | null): boolean {
  return !!dataType && SPARK_FILE_DATA_TYPES.includes(dataType);
}

/** A stored file as the server sends it. */
export function isSparkFile(value: unknown): value is SparkFileValue {
  return !!value && typeof value === 'object' && !Array.isArray(value)
    && typeof (value as SparkFileValue).fileId === 'string' && typeof (value as SparkFileValue).fileName === 'string';
}

/** A new file the save will claim. */
export function isSparkFileClaim(value: unknown): value is SparkFileClaim {
  return !!value && typeof value === 'object' && typeof (value as SparkFileClaim).uploadToken === 'string';
}

const SCAN_STATUSES: readonly SparkFileScanStatus[] = ['Pending', 'Clean', 'Infected', 'ScanFailed'];

/** The scan status by name, whichever form it arrived in; `Pending` when absent or unknown (never read as clean). */
export function sparkFileScanStatus(value: { scanStatus?: SparkFileScanStatus | number } | null | undefined): SparkFileScanStatus {
  const status = value?.scanStatus;
  if (typeof status === 'number') return SCAN_STATUSES[status] ?? 'Pending';
  return status && SCAN_STATUSES.includes(status) ? status : 'Pending';
}

/** A file value as text: its name, a list's names joined; `null` for anything that is not a file. */
export function sparkFileText(value: unknown): string | null {
  if (isSparkFile(value)) return value.fileName;
  if (Array.isArray(value) && value.length > 0 && value.every(isSparkFile)) return value.map(f => f.fileName).join(', ');
  return null;
}

/**
 * The identity of a file value for comparison: the file id (or the claim's token). The signed `url` differs on every read,
 * so two reads of the same file must not compare by their whole object.
 */
export function comparableFile(value: unknown): unknown {
  if (isSparkFile(value)) return { fileId: value.fileId };
  if (isSparkFileClaim(value)) return { uploadToken: value.uploadToken };
  return value;
}

/**
 * The URL an image shows best at a given size: the smallest variant whose longer edge is at least `edge` (the
 * nearest larger one, §0.12), else the largest variant, else the original. A variant without a URL is skipped.
 */
export function sparkImageUrl(image: SparkImageValue | SparkFileValue | null | undefined, edge?: number): string | null {
  if (!image) return null;
  const variants = Object.values((image as SparkImageValue).variants ?? {})
    .filter(v => !!v?.url)
    .sort((a, b) => Math.max(a.width, a.height) - Math.max(b.width, b.height));
  if (edge !== undefined && variants.length > 0) {
    const fitting = variants.find(v => Math.max(v.width, v.height) >= edge);
    if (fitting) return fitting.url!;
    const original = image.url;
    return original ?? variants[variants.length - 1].url!;
  }
  return image.url ?? variants[variants.length - 1]?.url ?? null;
}

/** A string an `<img>` may show: an `https:` URL, a same-origin path, or a raster `data:image/…` URI (never SVG). */
export function isDisplayableImageUrl(value: unknown): value is string {
  if (typeof value !== 'string' || value.length === 0) return false;
  if (/^data:image\/(png|jpeg|gif|webp)[;,]/i.test(value)) return true;
  if (value.startsWith('/') && !value.startsWith('//')) return true;
  return /^https:\/\//i.test(value);
}

/** A byte count as text: `512 B`, `12.3 KB`, `4.5 MB`. */
export function formatFileSize(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return '';
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}
