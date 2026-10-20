import { ChangeDetectionStrategy, Component, computed, inject, input, OnInit, output, signal } from '@angular/core';
import { SparkDateTimeComponent } from '@mintplayer/ng-spark/date-time';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkPublicEventSummary } from './generated/contracts';
import { SparkTicketingEventsService } from './spark-ticketing-events.service';

/** The event page's URL for `slug` from a path pattern with `{slug}` (the server's `EventPagePath`). */
export function eventPageUrl(pattern: string, slug: string): string {
  return pattern.replace('{slug}', encodeURIComponent(slug));
}

/**
 * The storefront's event list (commerce #521 M17): published events, upcoming first by their next date (in the
 * viewer's zone with the venue-local hint, D33), each linking to its event page. The past ones follow, marked as such.
 *
 * `<spark-ticketing-event-list eventPath="/events/{slug}" (opened)="…" />`
 */
@Component({
  selector: 'spark-ticketing-event-list',
  imports: [TranslateKeyPipe, ResolveTranslationPipe, SparkDateTimeComponent],
  templateUrl: './spark-ticketing-event-list.component.html',
  styleUrl: './spark-ticketing-event-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTicketingEventListComponent implements OnInit {
  private readonly events = inject(SparkTicketingEventsService);

  /** The event page's path pattern; matches the server's `Spark:Ticketing:Events:EventPagePath` by default. */
  eventPath = input('/events/{slug}');

  /** Events per page. */
  pageSize = input(24);

  /** An event was opened (the link is a plain `href` too, so this is for hosts that route themselves). */
  readonly opened = output<SparkPublicEventSummary>();

  protected readonly list = signal<SparkPublicEventSummary[]>([]);
  protected readonly total = signal(0);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly hasMore = computed(() => this.list().length < this.total());

  async ngOnInit(): Promise<void> {
    await this.load(0);
  }

  protected href(event: SparkPublicEventSummary): string {
    return eventPageUrl(this.eventPath(), event.slug);
  }

  protected open(event: SparkPublicEventSummary, e: MouseEvent): void {
    if (e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) return;
    this.opened.emit(event);
  }

  protected async more(): Promise<void> {
    await this.load(this.list().length);
  }

  private async load(skip: number): Promise<void> {
    this.busy.set(true);
    try {
      const page = await this.events.publicEvents(skip, this.pageSize());
      this.list.set(skip === 0 ? page.events : [...this.list(), ...page.events]);
      this.total.set(page.total);
    } finally {
      this.busy.set(false);
      this.loaded.set(true);
    }
  }
}
