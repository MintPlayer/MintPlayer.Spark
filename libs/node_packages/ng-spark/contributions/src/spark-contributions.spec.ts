import { Injector, runInInjectionContext, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { SPARK_DETAIL_ACTIONS, SPARK_QUERY_ROW_ACTIONS, SparkDetailContext, SparkQueryRowActionScope } from '@mintplayer/ng-spark/panels';
import { resolveRowRenderer, isRowRendered } from '@mintplayer/ng-spark/renderers';
import { AsDetailColumnsPipe } from '@mintplayer/ng-spark/pipes';
import { EntityType, PersistentObject } from '@mintplayer/ng-spark/models';
import { diffHasChanges, lineDiff, splitLines } from './line-diff';
import { lineDiffSourceOf, lineDiffTextOf, SparkLineDiffComponent } from './spark-line-diff.component';
import { SparkContributionAttributionComponent, contributionHistoryLink, relativeTime } from './spark-contribution-attribution.component';
import { SparkRevertContributionComponent, isContributionsQuery, sparkRevertContributionRowAction } from './spark-revert-contribution';
import { provideSparkContributions, sparkContributionRenderers } from './provide-spark-contributions';

const flush = () => new Promise(resolve => setTimeout(resolve, 0));

/** The options Contributions writes on the attribution attributes (library README, "Client contract"). */
const attributionOptions = {
  contributionsQuery: 'songlyricscontributions',
  targetType: 'Song',
  property: 'Lyrics',
  slots: ['Language', 'Script'],
  attribution: ['ContributorName', 'UpdatedAt', 'ContributionCount'],
};

/** The options it seeds on a contribution's text (ContributionDescriptor.LineDiffRendererOptions). */
const diffOptions = {
  compareType: 'Song',
  compareIdPattern: '^(.+?)/LyricsContributions/([^/]+/[^/]+)/User/.+$',
  compareIdReplacement: '$1',
  compareAttribute: 'Lyrics',
  compareRowAttribute: 'Text',
  compareRowKeyPattern: '^(.+?)/LyricsContributions/([^/]+/[^/]+)/User/.+$',
  compareRowKeyReplacement: '$2',
};

const contributionId = 'Songs/1/LyricsContributions/en/Latn/User/MintPlayerUsers/abc';

describe('lineDiff', () => {
  it('keeps common lines and marks removed before added at a change', () => {
    expect(lineDiff('a\nb\nc', 'a\nB\nc')).toEqual([
      { kind: 'same', text: 'a' },
      { kind: 'removed', text: 'b' },
      { kind: 'added', text: 'B' },
      { kind: 'same', text: 'c' },
    ]);
  });

  it('finds the longest common subsequence, not just a common prefix', () => {
    const diff = lineDiff('x\na\nb\nc\ny', 'a\nq\nb\nc');
    expect(diff.filter(l => l.kind === 'same').map(l => l.text)).toEqual(['a', 'b', 'c']);
    expect(diff.filter(l => l.kind === 'removed').map(l => l.text)).toEqual(['x', 'y']);
    expect(diff.filter(l => l.kind === 'added').map(l => l.text)).toEqual(['q']);
  });

  it('treats CRLF like LF, an empty text as no lines, and reports whether anything changed', () => {
    expect(splitLines('a\r\nb')).toEqual(['a', 'b']);
    expect(splitLines('')).toEqual([]);
    expect(lineDiff('', 'new')).toEqual([{ kind: 'added', text: 'new' }]);
    expect(diffHasChanges(lineDiff('a\r\nb', 'a\nb'))).toBe(false);
  });
});

describe('lineDiff renderer source', () => {
  const item = { id: contributionId, name: 'SongLyricsContribution', objectTypeId: 't', attributes: [] } as unknown as PersistentObject;

  it('derives the target id and the row key from the contribution id', () => {
    expect(lineDiffSourceOf(diffOptions, item)).toEqual({ type: 'Song', id: 'Songs/1', attribute: 'Lyrics', rowKey: 'en/Latn', rowAttribute: 'Text' });
  });

  it('reads the id from an attribute when asked, and gives up on options that describe nothing', () => {
    const withTarget = { ...item, attributes: [{ name: 'TargetId', value: 'Songs/9' }] } as unknown as PersistentObject;
    expect(lineDiffSourceOf({ compareType: 'Song', compareIdAttribute: 'TargetId', compareAttribute: 'Text' }, withTarget))
      .toEqual({ type: 'Song', id: 'Songs/9', attribute: 'Text' });
    expect(lineDiffSourceOf({ compareType: 'Song' }, item)).toBeNull();
    expect(lineDiffSourceOf({ ...diffOptions, compareIdPattern: '^nomatch$' }, item)).toBeNull();
  });

  it("takes the text of the target's row for the slot, and nothing when the slot has no row", () => {
    const song = {
      id: 'Songs/1', attributes: [{
        name: 'Lyrics', dataType: 'AsDetail', isArray: true,
        objects: [{ id: 'en/Latn', attributes: [{ name: 'Text', value: 'current\ntext' }] }],
      }],
    } as unknown as PersistentObject;
    const source = lineDiffSourceOf(diffOptions, item)!;
    expect(lineDiffTextOf(song, source)).toBe('current\ntext');
    expect(lineDiffTextOf(song, { ...source, rowKey: 'ko/Kore' })).toBe('');
    expect(lineDiffTextOf({ ...song, attributes: [] }, source)).toBeNull();
  });
});

describe('contributionAttribution row renderer', () => {
  it('links History to the contributions query with the parent and one filter per slot', () => {
    expect(contributionHistoryLink(attributionOptions, { Language: 'en', Script: 'Latn' }, 'Songs/1')).toEqual({
      commands: ['/query', 'songlyricscontributions'],
      queryParams: { parentId: 'Songs/1', parentType: 'Song', Language: 'en', Script: 'Latn' },
    });
    expect(contributionHistoryLink(attributionOptions, { Language: 'en', Script: '' }, 'Songs/1')).toBeNull();
    expect(contributionHistoryLink(attributionOptions, { Language: 'en', Script: 'Latn' }, undefined)).toBeNull();
  });

  it('formats a relative time', () => {
    const now = Date.parse('2026-10-04T12:00:00Z');
    expect(relativeTime('2026-10-01T12:00:00Z', 'en', now)).toBe('3 days ago');
    expect(relativeTime('nonsense', 'en', now)).toBeNull();
  });

  function renderAttribution(row: Record<string, any>, options: Record<string, any> = attributionOptions) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: SparkLanguageService, useValue: { t: (k: string) => ({ 'contributions.by': 'by {name}', 'contributions.history': 'History ({count})' } as Record<string, string>)[k] ?? k, language: signal('en') } },
      ],
    });
    const fixture = TestBed.createComponent(SparkContributionAttributionComponent);
    fixture.componentRef.setInput('row', row);
    fixture.componentRef.setInput('options', options);
    fixture.componentRef.setInput('ownerId', 'Songs/1');
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders one line with the parts present, History as a link', () => {
    const el = renderAttribution({ Language: 'en', Script: 'Latn', ContributorName: 'Alice', UpdatedAt: new Date(Date.now() - 3 * 86400_000).toISOString(), ContributionCount: 4 });
    expect(el.querySelector('.spark-contribution-by')?.textContent).toBe('by Alice');
    expect(el.querySelector('.spark-contribution-at')?.textContent).toBe('3 days ago');
    const link = el.querySelector('a.spark-contribution-history') as HTMLAnchorElement;
    expect(link.textContent).toBe('History (4)');
    expect(link.getAttribute('href')).toBe('/query/songlyricscontributions?parentId=Songs%2F1&parentType=Song&Language=en&Script=Latn');
  });

  it('shows only what the declaration asked for and the row has', () => {
    const el = renderAttribution({ Language: 'en', Script: 'Latn', ContributorName: '', ContributionCount: 2 }, { ...attributionOptions, attribution: ['ContributorName', 'ContributionCount'] });
    expect(el.querySelector('.spark-contribution-by')).toBeNull();
    expect(el.querySelector('.spark-contribution-at')).toBeNull();
    expect(el.querySelector('.spark-contribution-history')?.textContent).toBe('History (2)');
  });
});

describe('row renderers in AsDetail tables', () => {
  const lyrics = {
    id: 'l', name: 'Lyrics', attributes: [
      { name: 'Language', order: 1 },
      { name: 'Text', order: 3 },
      { name: 'ContributorName', order: 4, renderer: 'contributionAttribution', rendererOptions: attributionOptions },
      { name: 'UpdatedAt', order: 5, renderer: 'contributionAttribution', rendererOptions: attributionOptions },
    ],
  } as unknown as EntityType;

  it('draws the attribution attributes once per row, not as columns', () => {
    const resolved = resolveRowRenderer(lyrics, sparkContributionRenderers)!;
    expect(resolved.component).toBe(SparkContributionAttributionComponent);
    expect(resolved.attributes.map(a => a.name)).toEqual(['ContributorName', 'UpdatedAt']);
    expect(resolved.options).toEqual(attributionOptions);

    const columns = new AsDetailColumnsPipe().transform({ name: 'Lyrics' } as any, { Lyrics: lyrics }, sparkContributionRenderers);
    expect(columns.map(c => c.name)).toEqual(['Language', 'Text']);
    expect(isRowRendered(lyrics.attributes[2], [])).toBe(false);
  });

  it('keeps every column when the renderer is not registered', () => {
    expect(resolveRowRenderer(lyrics, [])).toBeNull();
    expect(new AsDetailColumnsPipe().transform({ name: 'Lyrics' } as any, { Lyrics: lyrics }).length).toBe(4);
  });
});

describe('revert', () => {
  let spark: { postEnvelope: ReturnType<typeof vi.fn>; getQueryByName: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    spark = { postEnvelope: vi.fn().mockResolvedValue({}), getQueryByName: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: SparkService, useValue: spark },
        { provide: SparkLanguageService, useValue: { t: (k: string) => k, resolve: (t: any) => t?.en ?? '' } },
        provideSparkContributions(),
      ],
    });
  });

  const scope = (overrides: Partial<SparkQueryRowActionScope> = {}): SparkQueryRowActionScope => ({
    query: { id: 'q', name: 'SongLyricsContributions', source: 'Custom.SparkContributionsOfTarget' } as any,
    entityType: { id: 'type-contribution', name: 'SongLyricsContribution' } as any,
    permissions: { canRevertContribution: true } as any,
    deleted: null,
    ...overrides,
  });

  it('registers the row action and the detail action, and the two renderers', () => {
    expect(TestBed.inject(SPARK_QUERY_ROW_ACTIONS)).toEqual([sparkRevertContributionRowAction]);
    expect(TestBed.inject(SPARK_DETAIL_ACTIONS).map(a => a.component)).toEqual([SparkRevertContributionComponent]);
    expect(sparkContributionRenderers.map(r => r.name)).toEqual(['contributionAttribution', 'lineDiff']);
    expect(sparkContributionRenderers[1].detailComponent).toBe(SparkLineDiffComponent);
  });

  it('offers the row action only on a contributions query, with the right, outside the recycle bin', () => {
    expect(sparkRevertContributionRowAction.isOffered(scope())).toBe(true);
    expect(sparkRevertContributionRowAction.isOffered(scope({ permissions: { canRevertContribution: false } as any }))).toBe(false);
    expect(sparkRevertContributionRowAction.isOffered(scope({ deleted: 'only' }))).toBe(false);
    expect(sparkRevertContributionRowAction.isOffered(scope({ query: { source: 'Database.Songs' } as any }))).toBe(false);
    expect(isContributionsQuery({ source: 'Custom.SparkContributionsOfTarget' })).toBe(true);
  });

  it('confirms, posts the revert and refreshes the list', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const reload = vi.fn();
    await runInInjectionContext(TestBed.inject(Injector), () => sparkRevertContributionRowAction.run({
      ...scope(), row: { id: contributionId } as any, reload,
    }));
    expect(spark.postEnvelope).toHaveBeenCalledWith('/po/revert-contribution', { objectTypeId: 'type-contribution', id: contributionId });
    expect(reload).toHaveBeenCalled();

    confirmSpy.mockReturnValue(false);
    spark.postEnvelope.mockClear();
    await runInInjectionContext(TestBed.inject(Injector), () => sparkRevertContributionRowAction.run({ ...scope(), row: { id: contributionId } as any, reload }));
    expect(spark.postEnvelope).not.toHaveBeenCalled();
    confirmSpy.mockRestore();
  });

  function detailContext(overrides: Partial<SparkDetailContext> = {}): SparkDetailContext {
    return {
      type: 'songlyricscontribution', id: contributionId,
      item: { id: contributionId, objectTypeId: 'type-contribution', name: 'SongLyricsContribution', attributes: [] } as any,
      entityType: { id: 'type-contribution', name: 'SongLyricsContribution' } as any,
      permissions: { canRevertContribution: true } as any,
      deleted: null,
      reload: vi.fn().mockResolvedValue(undefined),
      ...overrides,
    };
  }

  it('shows the detail button only on a generated contribution type, and reloads after a revert', async () => {
    spark.getQueryByName.mockResolvedValue({ name: 'SongLyricsContributions', entityType: 'SongLyricsContribution', source: 'Custom.SparkContributionsOfTarget' });
    const ctx = detailContext();
    const fixture = TestBed.createComponent(SparkRevertContributionComponent);
    fixture.componentRef.setInput('context', ctx);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
    expect(spark.getQueryByName).toHaveBeenCalledWith('SongLyricsContributions');
    const button = (fixture.nativeElement as HTMLElement).querySelector('button.spark-revert-contribution') as HTMLButtonElement;
    expect(button).not.toBeNull();

    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    button.click();
    await flush();
    expect(spark.postEnvelope).toHaveBeenCalledWith('/po/revert-contribution', { objectTypeId: 'type-contribution', id: contributionId });
    expect(ctx.reload).toHaveBeenCalled();
    confirmSpy.mockRestore();
  });

  it('hides the detail button on any other type and without the right', async () => {
    spark.getQueryByName.mockResolvedValue({ name: 'Songs', entityType: 'Song', source: 'Database.Songs' });
    const other = TestBed.createComponent(SparkRevertContributionComponent);
    other.componentRef.setInput('context', detailContext({ entityType: { id: 's', name: 'Song' } as any }));
    other.detectChanges();
    await flush();
    other.detectChanges();
    expect((other.nativeElement as HTMLElement).querySelector('button')).toBeNull();

    const denied = TestBed.createComponent(SparkRevertContributionComponent);
    denied.componentRef.setInput('context', detailContext({ permissions: { canRevertContribution: false } as any }));
    denied.detectChanges();
    await flush();
    denied.detectChanges();
    expect((denied.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });
});
