import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { currentLanguage } from '@mintplayer/ng-spark/models';
import {
  SparkDateTimeMode,
  SparkDateTimeStyle,
  parseSparkInstant,
  renderSparkDateTime,
  sparkDateTimeHint,
} from './spark-date-time-format';
import { SparkTimeZoneNames } from './spark-time-zone-names';

/**
 * A date and time as a viewer reads it (commerce PRD X16, D34/D35): the one component the grids, the detail
 * pages and custom pages render instants with.
 *
 * ```html
 * <spark-date-time [value]="order.placedAt" />                                  <!-- viewer's zone and language -->
 * <spark-date-time [value]="event.startsAt" [alsoIn]="venue.timeZoneId" />      <!-- 21:00 (20:00 London) -->
 * <spark-date-time [value]="audit.at" mode="stored" />                          <!-- 10:00 UTC+02:00 -->
 * ```
 *
 * - `value`: a wire value (ISO-8601 string), a `Date` or epoch milliseconds. Not a date: printed as is.
 * - `mode`: the main rendering. `'viewer'` (default), `'stored'` (the wall clock the value was written with,
 *   and its offset), or `{ zone }` (an IANA zone, labelled with its name).
 * - `alsoIn`: an IANA zone (or `'stored'`) for a second rendering, **hidden when both give the same wall
 *   clock at that instant**: compared by offset on the value's date, never by zone name.
 * - `format`: `'short'` (default), `'medium'`, `'long'`, `'date'` or `'time'`.
 *
 * Zone labels come from {@link SparkTimeZoneNames}: the translated city when a source is provided, else the
 * browser's localized generic name, else the city from the IANA id. The text never waits for a label.
 */
@Component({
  selector: 'spark-date-time',
  templateUrl: './spark-date-time.component.html',
  styleUrl: './spark-date-time.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkDateTimeComponent {
  private readonly names = inject(SparkTimeZoneNames);

  readonly value = input<unknown>(null);
  readonly mode = input<SparkDateTimeMode | null | undefined>('viewer');
  readonly alsoIn = input<string | null | undefined>(null);
  readonly format = input<SparkDateTimeStyle>('short');

  private readonly language = computed(() => currentLanguage());
  private readonly effectiveMode = computed<SparkDateTimeMode>(() => this.mode() ?? 'viewer');

  /** The main rendering; an unknown named zone falls back to the viewer's rather than hiding the value. */
  private readonly rendered = computed(() => {
    const mode = this.effectiveMode();
    const own = renderSparkDateTime(this.value(), mode, this.format(), this.language());
    if (own) return { rendering: own, used: mode, zone: typeof mode === 'object' ? mode.zone : null };
    const viewer = mode === 'viewer' ? null : renderSparkDateTime(this.value(), 'viewer', this.format(), this.language());
    return viewer ? { rendering: viewer, used: 'viewer' as SparkDateTimeMode, zone: null } : null;
  });

  protected readonly main = computed(() => this.rendered()?.rendering ?? null);

  protected readonly mainLabel = computed(() => {
    const zone = this.rendered()?.zone;
    return zone ? this.names.label(zone, this.language())() : null;
  });

  protected readonly hint = computed(() => {
    const rendered = this.rendered();
    return rendered ? sparkDateTimeHint(this.value(), rendered.used, this.alsoIn(), this.format(), this.language()) : null;
  });

  protected readonly hintLabel = computed(() => {
    const zone = this.alsoIn();
    return this.hint() && zone && zone !== 'stored' ? this.names.label(zone, this.language())() : null;
  });

  /** The main rendering with its zone's label when it is in a named zone: `20:00 London`. */
  protected readonly mainText = computed(() => {
    const main = this.main();
    if (!main) return null;
    const label = this.mainLabel();
    return label ? `${main.text} ${label}` : main.text;
  });

  /** The second rendering with its label (`20:00 London`), or null when hidden. */
  protected readonly hintText = computed(() => {
    const hint = this.hint();
    if (!hint) return null;
    const label = this.hintLabel();
    return label ? `${hint.text} ${label}` : hint.text;
  });

  /** The machine-readable instant for `<time datetime>`. */
  protected readonly iso = computed(() => parseSparkInstant(this.value())?.toISOString() ?? null);

  /** What is printed when the value is not a date: the value itself, never "Invalid Date". */
  protected readonly fallback = computed(() => {
    const value = this.value();
    return value == null || value === '' ? '' : String(value);
  });
}
