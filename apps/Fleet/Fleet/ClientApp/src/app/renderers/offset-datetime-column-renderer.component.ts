import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { SparkCellColumn } from '@mintplayer/ng-spark/models';
import { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Demo renderer: shows the three values a `DateTimeOffset` has at once, so the difference between them
 * is visible instead of having to be reasoned about.
 *
 * **This is a demonstration device, not a pattern to copy.** A real app shows a timestamp one way — the
 * viewer's local time, which is what the default `datetime` column and the detail page both do. This
 * exists because the Fleet demo's whole job is to make an invisible data defect visible.
 *
 * The three lines:
 *
 * - **stored** — the wall clock and offset held in the document, recovered through the index by the
 *   `{Name}Raw` wrapper. `17:48 −03:30` means "quarter to six in the evening, where this was registered".
 * - **your time** — the same instant in the viewer's zone. This is what the detail page shows, and what
 *   every other grid in the framework shows. It differs from the line above whenever the viewer's offset
 *   differs from the value's, which is most of the time.
 * - **index only** — what the projection would have returned before the fix: the same instant, with the
 *   offset destroyed. Derived here from the instant rather than fetched, because that is exactly what
 *   RavenDB's flattening produces — a UTC re-expression of the same moment.
 *
 * A column renderer receives the **raw wire value**, not the piped one, so the first line can print the
 * ISO string's own fields verbatim. Nothing here parses the stored line into a `Date`: `new Date(...)`
 * collapses the value to an instant and forgets the offset, which is the same loss in the browser that
 * RavenDB used to inflict in the index.
 */
@Component({
  selector: 'app-offset-datetime-column-renderer',
  standalone: true,
  imports: [DatePipe],
  template: `
    @if (parts(); as p) {
      <div class="d-flex flex-column gap-1 small lh-sm">
        <div>
          <span class="text-body-secondary me-1" style="display:inline-block;min-width:5.5em;">stored</span>
          <span class="font-monospace">{{ p.wallClock }}</span>
          <span class="badge ms-1"
                [class.text-bg-secondary]="p.offset === '+00:00'"
                [class.text-bg-primary]="p.offset !== '+00:00'"
                [title]="p.offset === '+00:00'
                  ? 'UTC — this row round-trips correctly even without the fix'
                  : 'Offset carried through the index by the ' + p.wrapperName + ' wrapper'">{{ p.offset }}</span>
        </div>
        <div>
          <span class="text-body-secondary me-1" style="display:inline-block;min-width:5.5em;">your time</span>
          <span class="font-monospace">{{ p.instant | date:'yyyy-MM-dd HH:mm' }}</span>
          <span class="text-body-secondary ms-1">{{ viewerZone }}</span>
        </div>
        <div class="text-body-tertiary">
          <span class="me-1" style="display:inline-block;min-width:5.5em;">index only</span>
          <span class="font-monospace">{{ p.flattened }}</span>
          <span class="ms-1" title="What the projection returned before the fix: same instant, offset destroyed">
            ← without the fix
          </span>
        </div>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class OffsetDateTimeColumnRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  column = input<SparkCellColumn>();
  options = input<Record<string, any>>();

  readonly viewerZone = Intl.DateTimeFormat().resolvedOptions().timeZone;

  parts = computed(() => {
    const raw = this.value();
    if (typeof raw !== 'string' || raw.length === 0) return null;

    const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::\d{2}(?:\.\d+)?)?(Z|[+-]\d{2}:\d{2})?$/.exec(raw);
    if (!match) return null;

    const [, date, time, zone] = match;
    const instant = new Date(raw);
    if (Number.isNaN(instant.getTime())) return null;

    // The flattened form is the instant expressed at offset zero — literally what RavenDB stores in the
    // scalar index field, so it can be derived rather than fetched.
    const iso = instant.toISOString();

    return {
      wallClock: `${date} ${time}`,
      offset: !zone || zone === 'Z' ? '+00:00' : zone,
      instant,
      flattened: `${iso.slice(0, 10)} ${iso.slice(11, 16)} Z`,
      wrapperName: `${this.column()?.name ?? ''}Raw`,
    };
  });
}
