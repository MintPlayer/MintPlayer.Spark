import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkMyTicketsComponent } from '@mintplayer/ng-spark/ticketing/tickets';
import { injectStorefront } from '../storefront-config';

/** The holder's tickets (`/tickets`), for the account or the guest visitor. */
@Component({
  selector: 'spark-storefront-tickets-page',
  imports: [TranslateKeyPipe, SparkMyTicketsComponent],
  template: `
    <h1>{{ 'commerceStorefront.nav.tickets' | t }}</h1>
    <spark-my-tickets [ticketPath]="ticketPath" />
  `,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontTicketsPage {
  protected readonly ticketPath = `/${injectStorefront().paths.tickets}/{code}`;
}
