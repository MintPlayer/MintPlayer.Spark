import { ChangeDetectionStrategy, Component } from '@angular/core';
import { SparkTicketingEventListComponent } from '@mintplayer/ng-spark/ticketing/events';
import { injectStorefront } from '../storefront-config';

/** The event list (`/events`): published events, upcoming first. */
@Component({
  selector: 'spark-storefront-events-page',
  imports: [SparkTicketingEventListComponent],
  template: `<spark-ticketing-event-list [eventPath]="eventPath" />`,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontEventsPage {
  protected readonly eventPath = `/${injectStorefront().paths.events}/{slug}`;
}
