import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { combineLatest } from 'rxjs';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import type { CodeLineAnnotation } from '@mintplayer/web-components/code-snippet';
import { canHighlight } from '@mintplayer/web-components/code-snippet';
import { BrowseService, FileDetail } from '../../services/browse.service';

/**
 * Extension → highlight.js grammar key. Keys must resolve on the lazy loader
 * map shipped with mp-code-snippet (the 36 lib/common grammars + aliases);
 * anything else renders as plain text rather than triggering the 54KB
 * auto-detect path on every file view.
 */
const LANGUAGE_BY_EXTENSION: Record<string, string> = {
  cs: 'csharp',
  ts: 'typescript',
  tsx: 'tsx',
  mts: 'typescript',
  cts: 'typescript',
  js: 'javascript',
  jsx: 'javascript',
  mjs: 'javascript',
  cjs: 'javascript',
  html: 'html',
  htm: 'html',
  xml: 'xml',
  csproj: 'xml',
  props: 'xml',
  targets: 'xml',
  config: 'xml',
  json: 'json',
  css: 'css',
  scss: 'scss',
  less: 'less',
  sql: 'sql',
  yaml: 'yaml',
  yml: 'yaml',
  md: 'markdown',
  vb: 'vbnet',
  py: 'python',
  rb: 'ruby',
  go: 'go',
  rs: 'rust',
  java: 'java',
  kt: 'kotlin',
  php: 'php',
  c: 'c',
  h: 'c',
  cpp: 'cpp',
  hpp: 'cpp',
  swift: 'swift',
  sh: 'bash',
  bash: 'bash',
  pl: 'perl',
  lua: 'lua',
  r: 'r',
  ini: 'ini',
  toml: 'ini',
  diff: 'diff',
  makefile: 'makefile',
};

/**
 * Line-by-line coverage view rendered by bs-code-snippet (viewer mode):
 * coverage becomes CodeLineAnnotation rows, line anchors carry #L42 deep
 * links, and syntax highlighting comes from highlight.js.
 */
@Component({
  selector: 'app-file',
  imports: [CommonModule, RouterModule, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent, BsBadgeComponent, BsCodeSnippetComponent],
  templateUrl: './file.component.html',
  styleUrl: './file.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class FileComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly browse = inject(BrowseService);

  readonly owner = signal('');
  readonly name = signal('');
  readonly sha = signal('');
  readonly path = signal('');
  readonly detail = signal<FileDetail | null>(null);
  readonly loading = signal(true);
  readonly targetLine = signal<number | null>(null);

  private readonly viewer = viewChild(BsCodeSnippetComponent);

  readonly code = computed(() => {
    const detail = this.detail();
    if (!detail) return '';
    // Source unavailable: an empty string still renders a full gutter because
    // the annotations name lines beyond the code's extent.
    return detail.source !== null ? detail.source.replace(/\r\n/g, '\n') : '';
  });

  readonly language = computed(() => {
    const path = this.path();
    const fileName = path.split('/').pop() ?? '';
    const ext = fileName.includes('.') ? fileName.split('.').pop()!.toLowerCase() : fileName.toLowerCase();
    const key = LANGUAGE_BY_EXTENSION[ext];
    if (key && canHighlight(key)) return key;
    if (!key && ext) console.warn(`No highlight.js grammar mapped for extension ".${ext}" — rendering plain text.`);
    return 'plaintext';
  });

  readonly annotations = computed<CodeLineAnnotation[]>(() => {
    const detail = this.detail();
    if (!detail) return [];

    const branchesByLine = new Map<number, { taken: number; total: number }>();
    for (const branch of detail.branches) {
      const entry = branchesByLine.get(branch.line) ?? { taken: 0, total: 0 };
      entry.total++;
      if ((branch.taken ?? 0) > 0) entry.taken++;
      branchesByLine.set(branch.line, entry);
    }

    return detail.lines.map((line) => {
      const branches = branchesByLine.get(line.number);
      const kind = line.status === 'Covered' ? 'covered'
        : line.status === 'PartiallyCovered' ? 'partial'
        : 'uncovered';
      return {
        line: line.number,
        kind,
        label: line.hits !== null && line.hits !== undefined ? `${line.hits}×` : undefined,
        secondaryLabel: branches ? `${branches.taken}/${branches.total}` : undefined,
        description: branches ? `Branches: ${branches.taken} of ${branches.total} taken` : undefined,
      };
    });
  });

  readonly lineHref = (line: number): string => `#L${line}`;

  readonly stats = computed(() => {
    const detail = this.detail();
    if (!detail) return null;
    const covered = detail.lines.filter((l) => l.status !== 'NotCovered').length;
    return { covered, coverable: detail.lines.length };
  });

  constructor() {
    combineLatest([this.route.paramMap, this.route.queryParamMap, this.route.fragment])
      .pipe(takeUntilDestroyed())
      .subscribe(async ([params, query, fragment]) => {
        const owner = params.get('owner') ?? '';
        const name = params.get('repo') ?? '';
        const sha = params.get('sha') ?? '';
        const path = query.get('path') ?? '';
        this.targetLine.set(fragment?.startsWith('L') ? parseInt(fragment.slice(1), 10) || null : null);

        if (owner === this.owner() && name === this.name() && sha === this.sha() && path === this.path()) {
          this.scrollToTarget();
          return;
        }

        this.owner.set(owner);
        this.name.set(name);
        this.sha.set(sha);
        this.path.set(path);
        this.loading.set(true);
        this.detail.set(null);
        try {
          this.detail.set(await this.browse.getFile(owner, name, sha, path));
        } finally {
          this.loading.set(false);
          setTimeout(() => this.scrollToTarget());
        }
      });
  }

  // The anchors carry a real href (middle-click/new-tab work); a primary click
  // is cancelled here and routed through Angular so only the fragment changes.
  onLineActivate(event: CustomEvent<{ line: number }>): void {
    event.preventDefault();
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { path: this.path() },
      fragment: `L${event.detail.line}`,
    });
  }

  private scrollToTarget(): void {
    const line = this.targetLine();
    if (line === null) return;
    // The rows live in the element's shadow root — getElementById can't reach
    // them, only the element's own scrollToLine can.
    this.viewer()?.scrollToLine(line);
  }
}
