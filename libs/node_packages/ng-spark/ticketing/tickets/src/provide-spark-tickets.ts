import { Routes } from '@angular/router';
import { SparkTicketComponent } from './spark-ticket.component';
import { SparkTicketTransferComponent } from './spark-ticket-transfer.component';
import { SparkTicketScannerComponent } from './scanner/spark-ticket-scanner.component';

/**
 * The holder's ticket pages (commerce #521 M15), to mount where the server's `Delivery:TicketPagePath` (default
 * `/tickets/{code}`) and `Delivery:TransferPagePath` (`/tickets/transfer/{id}`) point; each route is replaceable (D27).
 * Needs `withComponentInputBinding()` for the `:code` and `:transferId` inputs.
 *
 * ```ts
 * provideRouter([{ path: 'tickets', children: sparkTicketRoutes() }, { path: 'scanner', children: sparkTicketScannerRoutes() }], withComponentInputBinding())
 * ```
 */
export function sparkTicketRoutes(): Routes {
  return [
    { path: 'transfer/:transferId', component: SparkTicketTransferComponent },
    { path: ':code', component: SparkTicketComponent },
  ];
}

/**
 * The door scanner (§4.11 "Scanner PWA"). Install the app as a PWA (`ng-spark/pwa`'s `provideSparkServiceWorker`) so
 * the page itself loads offline; the offline list lives in IndexedDB (`ng-spark/offline`), never in the service worker
 * cache (no `/spark` data groups).
 */
export function sparkTicketScannerRoutes(): Routes {
  return [{ path: '', component: SparkTicketScannerComponent }];
}
