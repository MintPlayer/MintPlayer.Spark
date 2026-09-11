import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SparkCellColumn } from '@mintplayer/ng-spark/models';
import { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Renders a `DateTimeOffset` as the wall clock and UTC offset the server actually sent.
 *
 * The default `datetime` column pipes the value through Angular's `DatePipe` with no timezone
 * argument, which formats in the **browser's** local zone. That is correct for a moment in time and
 * wrong for this demo: every row would display in your own offset, hiding the very thing the fix
 * restores. A column renderer receives the **raw wire value** rather than the piped one, so it can
 * print the ISO string verbatim.
 *
 * Nothing here parses into a JS `Date` on purpose — `new Date(...)` discards the offset immediately,
 * which is the same loss in the browser that RavenDB used to inflict in the index.
 */
@Component({
  selector: 'app-offset-datetime-column-renderer',
  standalone: true,
  template: `
    @if (parts(); as p) {
      <span class="font-monospace">{{ p.wallClock }}</span>
      <span class="badge ms-2"
            [class.text-bg-secondary]="p.offset === '+00:00'"
            [class.text-bg-primary]="p.offset !== '+00:00'"
            [title]="p.offset === '+00:00' ? 'UTC — round-trips correctly even without the fix' : 'Offset preserved through the index projection'">
        {{ p.offset }}
      </span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class OffsetDateTimeColumnRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  column = input<SparkCellColumn>();
  options = input<Record<string, any>>();

  /**
   * Splits an ISO-8601 string into its wall clock and its offset without going through `Date`.
   * `Z` is normalised to `+00:00` so the control row reads the same way as the others.
   */
  parts = computed(() => {
    const raw = this.value();
    if (typeof raw !== 'string' || raw.length === 0) return null;

    const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::\d{2}(?:\.\d+)?)?(Z|[+-]\d{2}:\d{2})?$/.exec(raw);
    if (!match) return { wallClock: raw, offset: '' };

    const [, date, time, zone] = match;
    return {
      wallClock: `${date} ${time}`,
      offset: !zone || zone === 'Z' ? '+00:00' : zone,
    };
  });
}
