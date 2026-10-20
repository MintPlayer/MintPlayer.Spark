import { Routes } from '@angular/router';
import { sparkAuthRoutes, withLocalLogin, withRegistration } from '@mintplayer/ng-spark/auth/routes';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { sparkTicketScannerRoutes } from '@mintplayer/ng-spark/ticketing/tickets';
import { ShellComponent } from './shell/shell.component';

/**
 * The app's own routes. The storefront's (provideSparkCommerce in app.config.ts) are registered first: shop, events,
 * tickets, queue, checkout, account, legal, consent, commerce, promotions.
 */
export const routes: Routes = [
  // The door scanner PWA (Ticketing.Tickets, D11): a full-screen page without the storefront's chrome.
  { path: 'scanner', children: sparkTicketScannerRoutes() },
  {
    path: '',
    component: ShellComponent,
    children: [
      // Buyers register and sign in with a password; the tickets channel requires sign-in (D4), the shop does not.
      ...sparkAuthRoutes(withLocalLogin(), withRegistration()),
      { path: '', redirectTo: 'events', pathMatch: 'full' },
      // The back office: sellers, events, venues, products, orders, approvals (Spark's generic pages, /po and /query).
      ...sparkRoutes(),
    ]
  }
];
