import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

/** The `rendererOptions` Contributions writes on each attribution attribute (library README, "Client contract"). */
export interface SparkContributionAttributionOptions {
  /** The generated contributions query's alias (`songlyricscontributions`). */
  contributionsQuery: string;
  /** The target's model name: the query's `parentType` (`Song`). */
  targetType: string;
  /** The `[Contribution]` property (`Lyrics`). */
  property: string;
  /** The slot attributes, in id order: one column filter each (`Language`, `Script`). */
  slots: string[];
  /** The attribution attributes the row carries: `ContributorName`, `UpdatedAt`, `ContributionCount`. */
  attribution: string[];
}

/** The History link of a row: `/query/{contributionsQuery}?parentId=…&parentType=…&{slot}=…`. */
export interface SparkContributionHistoryLink {
  commands: string[];
  queryParams: Record<string, string>;
}

/** The History link for `row` of the object `ownerId`, or null when either is missing. */
export function contributionHistoryLink(
  options: Partial<SparkContributionAttributionOptions> | null | undefined,
  row: Record<string, any> | null | undefined,
  ownerId: string | null | undefined,
): SparkContributionHistoryLink | null {
  if (!options?.contributionsQuery || !options.targetType || !ownerId || !row) return null;
  const queryParams: Record<string, string> = { parentId: ownerId, parentType: options.targetType };
  for (const slot of options.slots ?? []) {
    const value = row[slot];
    if (value === null || value === undefined || value === '') return null; // an unsaved row has no history yet
    queryParams[slot] = String(value);
  }
  return { commands: ['/query', options.contributionsQuery], queryParams };
}

/** "3 days ago" in `language`, from an ISO instant; null when it does not parse. */
export function relativeTime(value: unknown, language: string, now: number = Date.now()): string | null {
  if (typeof value !== 'string' && !(value instanceof Date)) return null;
  const time = new Date(value).getTime();
  if (Number.isNaN(time)) return null;
  const seconds = Math.round((time - now) / 1000);
  const units: [Intl.RelativeTimeFormatUnit, number][] = [
    ['year', 365 * 24 * 3600], ['month', 30 * 24 * 3600], ['week', 7 * 24 * 3600],
    ['day', 24 * 3600], ['hour', 3600], ['minute', 60],
  ];
  let format: Intl.RelativeTimeFormat;
  try { format = new Intl.RelativeTimeFormat(language, { numeric: 'auto' }); }
  catch { format = new Intl.RelativeTimeFormat('en', { numeric: 'auto' }); }
  for (const [unit, size] of units) {
    if (Math.abs(seconds) >= size) return format.format(Math.round(seconds / size), unit);
  }
  return format.format(seconds, 'second');
}

/**
 * The `contributionAttribution` AsDetail **row** renderer (contributions PRD Q6): one line under a
 * row — "by Alice · 3 days ago · History (4)" — instead of three columns. Only the parts the
 * declaration asked for (`rendererOptions.attribution`) and that the row has; History links to the
 * slot's contributions query (`/query/{contributionsQuery}` with the parent and one filter per slot).
 */
@Component({
  selector: 'spark-contribution-attribution',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (parts().length) {
      <span class="spark-contribution-attribution small text-muted">
        @for (part of parts(); track part.kind; let last = $last) {
          @switch (part.kind) {
            @case ('by') { <span class="spark-contribution-by">{{ part.text }}</span> }
            @case ('at') { <time class="spark-contribution-at" [attr.datetime]="part.iso" [attr.title]="part.title">{{ part.text }}</time> }
            @case ('history') {
              @if (historyLink(); as link) {
                <a class="spark-contribution-history" [routerLink]="link.commands" [queryParams]="link.queryParams">{{ part.text }}</a>
              } @else {
                <span class="spark-contribution-history">{{ part.text }}</span>
              }
            }
          }
          @if (!last) { <span aria-hidden="true"> · </span> }
        }
      </span>
    }
  `,
})
export class SparkContributionAttributionComponent {
  private readonly lang = inject(SparkLanguageService);

  row = input<Record<string, any>>({});
  options = input<Record<string, any> | undefined>();
  ownerId = input<string | undefined>();

  private readonly asked = computed(() => new Set<string>(this.options()?.['attribution'] ?? ['ContributorName', 'UpdatedAt', 'ContributionCount']));

  protected readonly historyLink = computed(() =>
    contributionHistoryLink(this.options() as Partial<SparkContributionAttributionOptions> | undefined, this.row(), this.ownerId()));

  protected readonly parts = computed(() => {
    const row = this.row() ?? {};
    const asked = this.asked();
    const language = this.lang.language();
    const parts: { kind: 'by' | 'at' | 'history'; text: string; iso?: string; title?: string }[] = [];

    const name = row['ContributorName'];
    if (asked.has('ContributorName') && typeof name === 'string' && name.trim() !== '')
      parts.push({ kind: 'by', text: this.lang.t('contributions.by').replace('{name}', name) });

    const at = row['UpdatedAt'];
    const relative = asked.has('UpdatedAt') ? relativeTime(at, language) : null;
    if (relative) {
      const date = new Date(at);
      parts.push({ kind: 'at', text: relative, iso: date.toISOString(), title: date.toLocaleString(language) });
    }

    const count = row['ContributionCount'];
    if (asked.has('ContributionCount') && typeof count === 'number')
      parts.push({ kind: 'history', text: this.lang.t('contributions.history').replace('{count}', String(count)) });

    return parts;
  });
}
