import { HttpInterceptorFn, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideSpark } from '@mintplayer/ng-spark';
import { EntityAttributeDefinition } from '@mintplayer/ng-spark/models';
import { resolveAttributeRenderer, SPARK_ATTRIBUTE_RENDERERS, withAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { sparkCheckedDownloadUrl, variantNameOf } from './spark-file-download-url';
import { acceptAttribute, maxFiles, moveRow, rowsFromValue, sameFiles, SparkFileRow, valueFromRows } from './spark-file-rows';
import { SparkFileColumnComponent } from './spark-file-column.component';
import { SparkFileDetailComponent } from './spark-file-detail.component';
import { SparkFileEditComponent } from './spark-file-edit.component';
import { SparkFilesService, SparkPreparedFile } from './spark-files.service';
import { withFileAttributes } from './provide-spark-files';

const stored = (fileId: string, extra: Record<string, unknown> = {}) =>
  ({ fileId, fileName: `${fileId}.png`, contentType: 'image/png', length: 2048, scanStatus: 'Clean', ...extra });

const attr = (overrides: Partial<EntityAttributeDefinition> = {}): EntityAttributeDefinition =>
  ({ id: 'a', name: 'Photos', dataType: 'Image', storesFile: true, isArray: false, rules: [], order: 0, ...overrides }) as EntityAttributeDefinition;

describe('file rows (#700)', () => {
  it('starts one stored row per stored file and none for a claim or a URL string', () => {
    expect(rowsFromValue([stored('f1'), stored('f2')]).map(r => r.key)).toEqual(['stored:f1', 'stored:f2']);
    expect(rowsFromValue(stored('f1'))).toHaveLength(1);
    expect(rowsFromValue({ uploadToken: 't' })).toEqual([]);
    expect(rowsFromValue('https://example.com/a.png')).toEqual([]);
    expect(rowsFromValue(null)).toEqual([]);
  });

  it('saves stored files as read and ready new files as claims, in row order, leaving out unfinished ones', () => {
    const file = new File(['x'], 'x.png');
    const rows: SparkFileRow[] = [
      { kind: 'new', key: 'n1', file, sent: 1, phase: 'ready', token: 'tok1' },
      { kind: 'stored', key: 's', value: stored('f1') as any },
      { kind: 'new', key: 'n2', file, sent: 0, phase: 'uploading', token: 'tok2' },
      { kind: 'new', key: 'n3', file, sent: 1, phase: 'refused', token: 'tok3', error: 'bad' },
    ];
    expect(valueFromRows(rows, true)).toEqual([{ uploadToken: 'tok1' }, stored('f1')]);
    expect(valueFromRows(rows, false)).toEqual({ uploadToken: 'tok1' });
    expect(valueFromRows([], false)).toBeNull();
    expect(valueFromRows([], true)).toEqual([]);
  });

  it('compares values by file id, not by their signed urls', () => {
    expect(sameFiles(stored('f1', { url: '/a?sig=1' }), stored('f1', { url: '/a?sig=2' }))).toBe(true);
    expect(sameFiles([stored('f1'), stored('f2')], [stored('f2'), stored('f1')])).toBe(false);
    expect(sameFiles(null, [])).toBe(true);
  });

  it('reads accept and the file limit from the model', () => {
    expect(acceptAttribute({ accept: ['image/png', '.jpg'] })).toBe('image/png,.jpg');
    expect(acceptAttribute({ accept: ['*'] })).toBeNull();
    expect(acceptAttribute(undefined)).toBeNull();
    expect(maxFiles(false, { maxCount: 5 })).toBe(1);
    expect(maxFiles(true, { maxCount: 5 })).toBe(5);
    expect(maxFiles(true, undefined)).toBe(10);
  });

  it('moves a row and ignores a move out of range', () => {
    expect(moveRow(['a', 'b', 'c'], 0, 1)).toEqual(['b', 'a', 'c']);
    expect(moveRow(['a', 'b', 'c'], 2, -1)).toEqual(['a', 'c', 'b']);
    expect(moveRow(['a', 'b'], 0, -1)).toEqual(['a', 'b']);
  });

  it('builds the checked download url with every part encoded', () => {
    expect(sparkCheckedDownloadUrl('/spark', 't-1', 'cars/1-A', 'Photos', 'f&1', 'medium'))
      .toBe('/spark/files/download?type=t-1&id=cars%2F1-A&attribute=Photos&file=f%261&variant=medium');
    expect(variantNameOf({ thumb: { url: '/t' }, medium: { url: '/m' } }, '/m')).toBe('medium');
    expect(variantNameOf({ thumb: { url: '/t' } }, '/original')).toBeUndefined();
  });
});

describe('resolveAttributeRenderer (#700 SP6)', () => {
  const Edit = class {};
  const Other = class {};
  const registry = [
    { name: 'app-image', dataType: 'Image', editComponent: Other },
    { name: 'custom', editComponent: Other, detailComponent: Other },
    { name: 'spark-image', dataType: 'Image', editComponent: Edit, detailComponent: Edit },
  ];

  it('takes the named renderer first, then the first one for the data type, else null', () => {
    expect(resolveAttributeRenderer(registry, { renderer: 'custom', dataType: 'Image' }, 'editComponent')?.name).toBe('custom');
    expect(resolveAttributeRenderer(registry, { dataType: 'Image' }, 'editComponent')?.name).toBe('app-image');
    expect(resolveAttributeRenderer(registry, { dataType: 'String' }, 'editComponent')).toBeNull();
    expect(resolveAttributeRenderer(registry, null)).toBeNull();
  });

  it('skips a registration that does not provide the slot', () => {
    expect(resolveAttributeRenderer(registry, { dataType: 'Image' }, 'detailComponent')?.name).toBe('spark-image');
    expect(resolveAttributeRenderer(registry, { renderer: 'app-image', dataType: 'Image' }, 'detailComponent')?.name).toBe('spark-image');
  });

  it('puts an app registration for the data type before withFileAttributes()', () => {
    TestBed.configureTestingModule({
      providers: [provideSpark(withFileAttributes(), withAttributeRenderers([{ name: 'mine', dataType: 'Image', editComponent: Other }]))],
    });
    const renderers = TestBed.inject(SPARK_ATTRIBUTE_RENDERERS);
    expect(resolveAttributeRenderer(renderers, { dataType: 'Image' }, 'editComponent')?.editComponent).toBe(Other);
    expect(resolveAttributeRenderer(renderers, { dataType: 'Image' }, 'columnComponent')?.columnComponent).toBe(SparkFileColumnComponent);
    expect(resolveAttributeRenderer(renderers, { dataType: 'BinaryFile' }, 'editComponent')?.editComponent).toBe(SparkFileEditComponent);
  });
});

describe('SparkFilesService (#700)', () => {
  let http: HttpTestingController;
  let service: SparkFilesService;
  const marker: HttpInterceptorFn = (req, next) => next(req.clone({ setHeaders: { 'X-App': '1' } }));

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([marker])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkFilesService);
  });
  afterEach(() => http.verify());

  it('prepares with the type, attribute, id and each file name and size', async () => {
    const pending = service.prepare('t-1', 'Photos', undefined, [new File(['abc'], 'a.png')]);
    const req = http.expectOne('/spark/files/prepare');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ objectTypeId: 't-1', attribute: 'Photos', id: null, files: [{ fileName: 'a.png', length: 3 }] });
    req.flush({ files: [{ token: 'tok' }] });
    expect(await pending).toEqual([{ token: 'tok' }]);
  });

  it('sends a Spark upload through the app client as a form of the fields, then the file', async () => {
    const prepared: SparkPreparedFile = { token: 'tok', fileName: 'a.png', url: '/spark/files/upload', method: 'POST', fields: { token: 'tok' }, headers: {}, direct: false };
    const progress = vi.fn();
    const pending = service.upload(prepared, new File(['abc'], 'a.png'), progress);
    const req = http.expectOne('/spark/files/upload');
    const form = req.request.body as FormData;
    expect([...form.keys()]).toEqual(['token', 'file']);
    expect(req.request.headers.get('X-App')).toBe('1');
    req.event({ type: 1, loaded: 2, total: 3 } as any);
    req.flush(null, { status: 204, statusText: 'No Content' });
    await pending;
    expect(progress).toHaveBeenCalledWith(2);
  });

  it('sends a direct upload past the interceptors, with only the slot headers', async () => {
    const prepared: SparkPreparedFile = { token: 'tok', fileName: 'a.png', url: 'https://store.example/b?sig=1', method: 'PUT', fields: {}, headers: { 'x-ms-blob-type': 'BlockBlob' }, direct: true };
    const pending = service.upload(prepared, new File(['abc'], 'a.png'));
    const req = http.expectOne('https://store.example/b?sig=1');
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('X-App')).toBeNull();
    expect(req.request.headers.get('x-ms-blob-type')).toBe('BlockBlob');
    expect(req.request.withCredentials).toBe(false);
    req.flush(null, { status: 201, statusText: 'Created' });
    await pending;
  });

  it('commits again while a deferred scan answers Pending, and stops at the attempt bound', async () => {
    vi.useFakeTimers();
    try {
      const pending = service.commitUntilScanned(['tok'], { intervalMs: 10, attempts: 3 });
      const answer = (status: string) => http.expectOne('/spark/files/commit').flush({ files: [{ token: 'tok', scanStatus: status, error: null }] });
      answer('Pending');
      await vi.advanceTimersByTimeAsync(10);
      answer('Pending');
      await vi.advanceTimersByTimeAsync(10);
      answer('Clean');
      expect((await pending)[0].scanStatus).toBe('Clean');

      const bounded = service.commitUntilScanned(['tok'], { intervalMs: 10, attempts: 2 });
      answer('Pending');
      await vi.advanceTimersByTimeAsync(10);
      answer('Pending');
      expect((await bounded)[0].scanStatus).toBe('Pending');
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('file renderers (#700)', () => {
  let files: { prepare: ReturnType<typeof vi.fn>; upload: ReturnType<typeof vi.fn>; commit: ReturnType<typeof vi.fn>; commitUntilScanned: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    files = {
      prepare: vi.fn().mockResolvedValue([{ token: 'tok', fileName: 'new.png', url: '/spark/files/upload', method: 'POST', fields: { token: 'tok' }, headers: {}, direct: false }]),
      upload: vi.fn().mockResolvedValue(undefined),
      commit: vi.fn().mockResolvedValue([{ token: 'tok', scanStatus: 'Clean', error: null }]),
      commitUntilScanned: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: SparkFilesService, useValue: files },
        { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
      ],
    });
    globalThis.URL.createObjectURL ??= (() => 'blob:preview') as any;
    globalThis.URL.revokeObjectURL ??= (() => undefined) as any;
  });

  function edit(attribute: EntityAttributeDefinition, value: unknown) {
    const fixture = TestBed.createComponent(SparkFileEditComponent);
    const changes: unknown[] = [];
    fixture.componentRef.setInput('attribute', attribute);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('objectTypeId', 't-1');
    fixture.componentRef.setInput('objectId', 'cars/1');
    fixture.componentRef.setInput('valueChange', (v: unknown) => changes.push(v));
    fixture.detectChanges();
    return { fixture, changes, el: fixture.nativeElement as HTMLElement, cmp: fixture.componentInstance as any };
  }

  it('uploads a chosen file and writes its token only once the server answers Clean', async () => {
    const { fixture, changes, cmp, el } = edit(attr(), null);
    await cmp.onFiles([{ file: new File(['abc'], 'new.png', { type: 'image/png' }), progress: 0, index: 0 }]);
    fixture.detectChanges();
    expect(files.prepare).toHaveBeenCalledWith('t-1', 'Photos', 'cars/1', [expect.any(File)]);
    expect(changes).toEqual([{ uploadToken: 'tok' }]);
    expect(el.querySelector('[data-phase="ready"]')).not.toBeNull();
  });

  it('prepares a field of an AsDetail row against the document and its Parent.Field path (M10)', async () => {
    const { fixture, cmp } = edit(attr(), null);
    fixture.componentRef.setInput('attributePath', 'Services.Photos');
    await cmp.onFiles([{ file: new File(['abc'], 'new.png', { type: 'image/png' }), progress: 0, index: 0 }]);
    expect(files.prepare).toHaveBeenCalledWith('t-1', 'Services.Photos', 'cars/1', [expect.any(File)]);
  });

  it('shows why the server refused a file and does not write it', async () => {
    files.commit.mockResolvedValue([{ token: 'tok', scanStatus: 'Clean', error: 'The file is not an image.' }]);
    const { fixture, changes, cmp, el } = edit(attr(), null);
    await cmp.onFiles([{ file: new File(['abc'], 'new.png'), progress: 0, index: 0 }]);
    fixture.detectChanges();
    expect(changes).toEqual([]);
    expect(el.querySelector('.spark-file-error')?.textContent).toContain('The file is not an image.');
  });

  it('waits for a deferred scan and refuses an infected file', async () => {
    files.commit.mockResolvedValue([{ token: 'tok', scanStatus: 'Pending', error: null }]);
    files.commitUntilScanned.mockResolvedValue([{ token: 'tok', scanStatus: 'Infected', error: null }]);
    const { fixture, changes, cmp, el } = edit(attr(), null);
    await cmp.onFiles([{ file: new File(['abc'], 'new.png'), progress: 0, index: 0 }]);
    fixture.detectChanges();
    expect(files.commitUntilScanned).toHaveBeenCalled();
    expect(changes).toEqual([]);
    expect(el.querySelector('[data-phase="refused"]')).not.toBeNull();
  });

  it('refuses a file over maxBytes without sending it', async () => {
    const { cmp, changes } = edit(attr({ fileOptions: { maxBytes: 2 } }), null);
    await cmp.onFiles([{ file: new File(['abc'], 'big.png'), progress: 0, index: 0 }]);
    expect(files.prepare).not.toHaveBeenCalled();
    expect(changes).toEqual([]);
  });

  it('keeps stored files, removes one and reorders a list', () => {
    const { fixture, changes, el } = edit(attr({ isArray: true }), [stored('f1'), stored('f2'), stored('f3')]);
    (el.querySelectorAll('.spark-file-remove')[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect((changes.at(-1) as any[]).map(f => f.fileId)).toEqual(['f1', 'f3']);
    (el.querySelector('.spark-file-down') as HTMLButtonElement).click();
    expect((changes.at(-1) as any[]).map(f => f.fileId)).toEqual(['f3', 'f1']);
  });

  it('clears a single file to null', () => {
    const { changes, el } = edit(attr(), stored('f1'));
    (el.querySelector('.spark-file-remove') as HTMLButtonElement).click();
    expect(changes).toEqual([null]);
  });

  it('edits a URL-string attribute with a URL input', () => {
    const { fixture, changes, el } = edit(attr({ storesFile: undefined }), 'https://example.com/a.png');
    const input = el.querySelector('input.spark-file-url') as HTMLInputElement;
    expect(input.value).toBe('https://example.com/a.png');
    input.value = '';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(changes).toEqual([null]);
    expect(el.querySelector('bs-file-upload')).toBeNull();
  });

  it('shows a stored file with a download link, and no link while it is not Clean', () => {
    const fixture = TestBed.createComponent(SparkFileDetailComponent);
    fixture.componentRef.setInput('attribute', attr({ dataType: 'BinaryFile' }));
    fixture.componentRef.setInput('value', [stored('f1', { url: '/spark/files/download?x=1' }), stored('f2', { scanStatus: 0 })]);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('a.spark-file-download')).toHaveLength(1);
    expect(el.querySelector('[data-file="f2.png"] .spark-file-status')?.textContent).toContain('files.scanning');
    expect(el.querySelector('img')).toBeNull();
  });

  it('draws an image at its variant and falls back to the checked download when the url fails', () => {
    const fixture = TestBed.createComponent(SparkFileDetailComponent);
    fixture.componentRef.setInput('attribute', attr());
    fixture.componentRef.setInput('item', { id: 'cars/1', objectTypeId: 't-1', attributes: [] });
    fixture.componentRef.setInput('value', stored('f1', {
      url: '/spark/files/content/orig', width: 2000, height: 1000,
      variants: { medium: { fileId: 'v', contentType: 'image/webp', width: 800, height: 400, length: 1, url: '/spark/files/content/med' } },
    }));
    fixture.detectChanges();
    const img = () => (fixture.nativeElement as HTMLElement).querySelector('img.spark-file-image') as HTMLImageElement;
    expect(img().getAttribute('src')).toBe('/spark/files/content/med');
    img().dispatchEvent(new Event('error'));
    fixture.detectChanges();
    expect(img().getAttribute('src')).toBe('/spark/files/download?type=t-1&id=cars%2F1&attribute=Photos&file=f1&variant=medium');
  });

  it('never draws a non-displayable url as an image', () => {
    const fixture = TestBed.createComponent(SparkFileDetailComponent);
    fixture.componentRef.setInput('attribute', attr({ storesFile: undefined }));
    fixture.componentRef.setInput('value', 'javascript:alert(1)');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('img')).toBeNull();
    expect(el.textContent).toContain('javascript:alert(1)');
  });

  it('shows the first file of a list in a cell with +N for the rest', () => {
    const fixture = TestBed.createComponent(SparkFileColumnComponent);
    fixture.componentRef.setInput('column', { name: 'Photos', dataType: 'BinaryFile' });
    fixture.componentRef.setInput('value', [stored('f1'), stored('f2'), stored('f3')]);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('f1.png');
    expect(el.querySelector('.spark-file-more')?.textContent?.trim()).toBe('+2');
  });
});
