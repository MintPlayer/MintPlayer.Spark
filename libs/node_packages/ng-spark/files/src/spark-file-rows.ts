import {
  comparableFile,
  isSparkFile,
  isSparkFileClaim,
  SparkAttributeFileOptions,
  SparkFileClaim,
  SparkFileValue,
} from '@mintplayer/ng-spark/models';

/** Where a new file is in the prepare → upload → commit flow. */
export type SparkFileRowPhase = 'uploading' | 'checking' | 'scanning' | 'ready' | 'refused' | 'failed';

/** A file already stored on the object: kept by echoing it back on save. */
export interface SparkStoredFileRow {
  kind: 'stored';
  key: string;
  value: SparkFileValue;
}

/** A file the user just chose: claimed by its token on save, once `ready`. */
export interface SparkNewFileRow {
  kind: 'new';
  key: string;
  file: File;
  /** Bytes sent so far. */
  sent: number;
  phase: SparkFileRowPhase;
  token?: string;
  /** The server's reason for a refused or failed file, else a translation key (`files.*`). */
  error?: string;
  /** A local object URL of an image, for the preview; revoked when the row goes. */
  preview?: string;
}

export type SparkFileRow = SparkStoredFileRow | SparkNewFileRow;

/** The rows a stored value starts as: one per stored file. Anything else (a claim, a URL string) has no row. */
export function rowsFromValue(value: unknown): SparkFileRow[] {
  const files = Array.isArray(value) ? value : value === null || value === undefined ? [] : [value];
  return files.filter(isSparkFile).map(file => ({ kind: 'stored' as const, key: `stored:${file.fileId}`, value: file }));
}

/**
 * The value the form saves: a stored file as it was read (kept), a ready new file as `{ uploadToken }` (claimed), in row
 * order. A file still uploading, refused or failed is not in it. A single attribute takes the first, or `null`.
 */
export function valueFromRows(rows: readonly SparkFileRow[], isArray: boolean): SparkFileValue | SparkFileClaim | (SparkFileValue | SparkFileClaim)[] | null {
  const values: (SparkFileValue | SparkFileClaim)[] = [];
  for (const row of rows) {
    if (row.kind === 'stored') values.push(row.value);
    else if (row.phase === 'ready' && row.token) values.push({ uploadToken: row.token });
  }
  return isArray ? values : values[0] ?? null;
}

/** Whether two values hold the same files in the same order (by file id or token; a signed URL differs per read). */
export function sameFiles(a: unknown, b: unknown): boolean {
  const key = (v: unknown) => JSON.stringify((Array.isArray(v) ? v : v === null || v === undefined ? [] : [v])
    .filter(x => isSparkFile(x) || isSparkFileClaim(x)).map(comparableFile));
  return key(a) === key(b);
}

/** The file input's `accept`: the model's types and extensions, comma-separated; null accepts anything. */
export function acceptAttribute(options: SparkAttributeFileOptions | undefined): string | null {
  const accept = (options?.accept ?? []).filter(a => !!a && a !== '*' && a !== '*/*');
  return accept.length > 0 ? accept.join(',') : null;
}

/** How many files the attribute holds at most: 1 for a single one, else `maxCount` (the server's default 10). */
export function maxFiles(isArray: boolean, options: SparkAttributeFileOptions | undefined): number {
  return isArray ? options?.maxCount ?? 10 : 1;
}

/** Whether the browser may preview this file in an `<img>`: a raster image type (an SVG is never previewed). */
export function isPreviewableImage(file: File): boolean {
  return /^image\/(png|jpeg|gif|webp|bmp)$/i.test(file.type);
}

/** Moves the row at `index` by `delta` places; out of range leaves the list unchanged. */
export function moveRow<T>(rows: readonly T[], index: number, delta: number): T[] {
  const target = index + delta;
  if (index < 0 || index >= rows.length || target < 0 || target >= rows.length) return [...rows];
  const copy = [...rows];
  const [row] = copy.splice(index, 1);
  copy.splice(target, 0, row);
  return copy;
}
