import { Component, input, output, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FileDetail } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub } from '../../../testing/test-utils';
import FileComponent from './file.component';

/** Stand-in for the Lit-backed `<mp-code-snippet>` wrapper; exposes what the page binds. */
@Component({ selector: 'bs-code-snippet', template: '' })
class StubCodeSnippet {
  readonly code = input<string>('');
  readonly language = input<string>('');
  readonly lineNumbers = input<boolean>(false);
  readonly annotations = input<unknown[]>([]);
  readonly activeLine = input<number | null>(null);
  readonly lineHref = input<((line: number) => string) | null>(null);
  readonly label = input<string>('');
  readonly lineActivate = output<CustomEvent<{ line: number }>>();
}

function detail(overrides: Partial<FileDetail> = {}): FileDetail {
  return {
    path: 'src/a.ts',
    source: 'one\r\ntwo\r\nthree',
    lines: [
      { number: 1, hits: 3, status: 'Covered' },
      { number: 2, hits: 1, status: 'PartiallyCovered' },
      { number: 3, hits: 0, status: 'NotCovered' },
    ],
    branches: [{ line: 2, covered: 1, total: 2 }],
    ...overrides,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

const base = '/github/r/acme/widgets/c/abc1234567/f';

describe('FileComponent', () => {
  let browse: BrowseServiceStub;
  let harness: RouterTestingHarness;

  async function open(url: string, getFile: (...args: any[]) => Promise<FileDetail> = () => Promise.resolve(detail())) {
    browse = createBrowseStub({ getFile });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: ':provider/r/:owner/:repo/c/:sha/f', component: FileComponent }]),
        provideBrowseStub(browse),
      ],
    });
    TestBed.overrideComponent(FileComponent, {
      remove: { imports: [BsCodeSnippetComponent] },
      add: { imports: [StubCodeSnippet] },
    });
    harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl(url, FileComponent);
    await settleHarness();
    return component;
  }

  async function settleHarness() {
    for (let i = 0; i < 4; i++) {
      await harness.fixture.whenStable();
      await Promise.resolve();
      harness.detectChanges();
    }
    await new Promise((resolve) => setTimeout(resolve, 0));
    harness.detectChanges();
  }

  const text = () => (harness.routeNativeElement as HTMLElement).textContent ?? '';
  const snippet = () => harness.fixture.debugElement.query(By.directive(StubCodeSnippet))?.componentInstance as StubCodeSnippet | undefined;

  afterEach(() => vi.restoreAllMocks());

  it('loads the file named by the route params and ?path=', async () => {
    const component = await open(`${base}?path=src%2Fa.ts#L2`);

    expect(browse.getFile).toHaveBeenCalledWith('github', 'acme', 'widgets', 'abc1234567', 'src/a.ts');
    expect(component.provider()).toBe('github');
    expect(component.targetLine()).toBe(2);
    expect(component.loading()).toBe(false);
    expect(snippet()!.code()).toBe('one\ntwo\nthree');
    expect(snippet()!.activeLine()).toBe(2);
    expect(snippet()!.label()).toBe('src/a.ts');
    expect(snippet()!.lineHref()!(7)).toBe('#L7');
    expect(text()).toContain('2/3 lines covered');
    expect(text()).toContain('abc1234');
  });

  it('turns lines and branches into annotations', async () => {
    const component = await open(`${base}?path=src%2Fa.ts`);

    expect(component.annotations()).toEqual([
      { line: 1, kind: 'covered', label: '3×', secondaryLabel: undefined, description: undefined },
      { line: 2, kind: 'partial', label: '1×', secondaryLabel: '1/2', description: 'Branches: 1 of 2 taken' },
      { line: 3, kind: 'uncovered', label: '0×', secondaryLabel: undefined, description: undefined },
    ]);
    expect(component.stats()).toEqual({ covered: 2, coverable: 3 });
  });

  it('omits the hit label when the report carries no hit count', async () => {
    const component = await open(`${base}?path=a.ts`, () => Promise.resolve(detail({
      lines: [{ number: 1, status: 'Covered' }], branches: [],
    })));
    expect(component.annotations()[0].label).toBeUndefined();
  });

  it('breadcrumb links carry the forge', async () => {
    await open(`${base}?path=a.ts`);
    const hrefs = [...harness.routeNativeElement!.querySelectorAll('bs-card-header a')].map((a) => a.getAttribute('href'));
    expect(hrefs).toEqual(['/github/a/acme', '/github/r/acme/widgets', '/github/r/acme/widgets/c/abc1234567']);
  });

  it('shows the unavailable-source note and an empty code string when source is null', async () => {
    const component = await open(`${base}?path=a.ts`, () => Promise.resolve(detail({ source: null })));
    expect(component.code()).toBe('');
    expect(text()).toContain('Source unavailable');
  });

  it('shows a spinner while loading', async () => {
    const pending = deferred<FileDetail>();
    const component = await open(`${base}?path=a.ts`, () => pending.promise);

    expect(component.loading()).toBe(true);
    expect(harness.routeNativeElement!.querySelector('bs-spinner')).not.toBeNull();
    pending.resolve(detail());
    await settleHarness();
    expect(component.loading()).toBe(false);
    expect(harness.routeNativeElement!.querySelector('bs-spinner')).toBeNull();
  });

  it('shows "no coverage data" when the file cannot be loaded', async () => {
    const component = await open(`${base}?path=a.ts`, () => Promise.reject(new Error('404')));

    expect(component.loading()).toBe(false);
    expect(component.detail()).toBeNull();
    expect(text()).toContain('No coverage data for this file.');
  });

  it('a fragment-only change re-targets the line without refetching', async () => {
    const component = await open(`${base}?path=a.ts#L1`);
    browse.getFile.mockClear();

    await harness.navigateByUrl(`${base}?path=a.ts#L3`);
    await settleHarness();
    expect(component.targetLine()).toBe(3);
    expect(browse.getFile).not.toHaveBeenCalled();

    await harness.navigateByUrl(`${base}?path=a.ts#top`);
    await settleHarness();
    expect(component.targetLine()).toBeNull();
  });

  it('a new ?path= reloads the file on the same component', async () => {
    const component = await open(`${base}?path=a.ts`);
    await harness.navigateByUrl(`${base}?path=b.cs`);
    await settleHarness();

    expect(browse.getFile).toHaveBeenLastCalledWith('github', 'acme', 'widgets', 'abc1234567', 'b.cs');
    expect(component.path()).toBe('b.cs');
    expect(component.language()).toBe('csharp');
  });

  it('activating a line navigates to its fragment, keeping ?path=', async () => {
    const component = await open(`${base}?path=src%2Fa.ts`);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    const event = new CustomEvent('lineActivate', { detail: { line: 9 }, cancelable: true });

    snippet()!.lineActivate.emit(event);
    await settleHarness();

    expect(event.defaultPrevented).toBe(true);
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: { path: 'src/a.ts' }, fragment: 'L9' }));
    expect(router.url).toBe(`${base}?path=src%2Fa.ts#L9`);
    expect(component.targetLine()).toBe(9);
    expect(browse.getFile).toHaveBeenCalledTimes(1);
  });

  describe('language', () => {
    it.each([
      ['src/a.cs', 'csharp'],
      ['src/A.TS', 'typescript'],
      ['web/index.html', 'html'],
      ['proj/app.csproj', 'xml'],
      ['config.yml', 'yaml'],
      ['main.py', 'python'],
    ])('maps %s to %s', async (path, language) => {
      const component = await open(`${base}?path=${encodeURIComponent(path)}`);
      expect(component.language()).toBe(language);
      expect(snippet()!.language()).toBe(language);
    });

    // An extension-less name is looked up as a whole ("Makefile" → makefile).
    it('maps an extension-less Makefile by its name', async () => {
      const component = await open(`${base}?path=Makefile`);
      expect(component.language()).toBe('makefile');
    });

    it('renders an unknown extension as plain text and warns once about it', async () => {
      const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
      const component = await open(`${base}?path=a.weird`);

      expect(component.language()).toBe('plaintext');
      expect(warn).toHaveBeenCalledWith(expect.stringContaining('".weird"'));
    });

    it('renders an empty path as plain text without warning', async () => {
      const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
      const component = await open(base);

      expect(component.language()).toBe('plaintext');
      expect(warn).not.toHaveBeenCalled();
    });
  });
});
