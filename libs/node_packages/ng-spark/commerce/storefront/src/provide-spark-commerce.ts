import { EnvironmentProviders, makeEnvironmentProviders, Type } from '@angular/core';
import { Route, ROUTES, Routes } from '@angular/router';
import { sparkCommerceRoutes } from '@mintplayer/ng-spark/commerce/core';
import { sparkConsentRoutes } from '@mintplayer/ng-spark/consent';
import { SparkTicketComponent, SparkTicketTransferComponent } from '@mintplayer/ng-spark/ticketing/tickets';
import { resolvePaths, SPARK_LEGAL_KINDS, SparkStorefrontPaths } from './storefront-paths';
import { SPARK_STOREFRONT, SparkStorefrontConfig } from './storefront-config';
import { SparkStorefrontShellComponent } from './pages/spark-storefront-shell.component';
import { SparkStorefrontBasketPage } from './pages/spark-storefront-basket.page';
import { SparkStorefrontCheckoutPage } from './pages/spark-storefront-checkout.page';
import { SparkStorefrontOrderPage } from './pages/spark-storefront-order.page';
import { SparkStorefrontAccountPage } from './pages/spark-storefront-account.page';
import { SparkStorefrontLegalPage } from './pages/spark-storefront-legal.page';
import { SparkStorefrontAccessibilityPage } from './pages/spark-storefront-accessibility.page';
import { SparkStorefrontCatalogPage } from './pages/spark-storefront-catalog.page';
import { SparkStorefrontProductPage } from './pages/spark-storefront-product.page';
import { SparkStorefrontEventsPage } from './pages/spark-storefront-events.page';
import { SparkStorefrontEventPage } from './pages/spark-storefront-event.page';
import { SparkStorefrontTicketsPage } from './pages/spark-storefront-tickets.page';
import { SparkStorefrontWaitingRoomPage } from './pages/spark-storefront-waiting-room.page';
import { SparkStorefrontItemPage } from './pages/spark-storefront-item.page';
import { SparkStorefrontBackInStockPage } from './pages/spark-storefront-back-in-stock.page';

/** The pages every storefront has (D27: each one replaceable). */
export type SparkStorefrontCorePage = 'backInStockConfirm' | 'backInStockUnsubscribe' | 'basket' | 'checkout' | 'order' | 'account' | 'terms' | 'privacy' | 'withdrawal' | 'accessibility' | 'cookies';

/** The shop's pages. */
export type SparkStorefrontShopPage = 'item' | 'catalog' | 'category' | 'product';

/** Ticketing's pages. */
export type SparkStorefrontTicketingPage = 'events' | 'event' | 'tickets' | 'ticket' | 'transfer';

/** The waiting room's page. */
export type SparkStorefrontQueuePage = 'waitingRoom';

/** A page component per page key: the host's own replaces the library's for that route only. */
export type SparkStorefrontPages<K extends string> = Partial<Record<K, Type<unknown>>>;

/** The storefront's own options. */
export interface SparkStorefrontOptions {
  /** URL segments; the defaults match the server's page paths (see `storefront-paths.ts`). */
  paths?: Partial<SparkStorefrontPaths>;
  /** The layout around every page: the library's shell by default, the host's own, or `null` for none. */
  shell?: Type<unknown> | null;
  /**
   * The host's sign-in page, linked from the account page when the visitor is anonymous. `{returnUrl}` is replaced by
   * the account page's path (escaped), e.g. `/login?returnUrl={returnUrl}`.
   */
  signInUrl?: string | null;
  /** Replacements for the core pages. */
  pages?: SparkStorefrontPages<SparkStorefrontCorePage>;
}

/** A route feature (`withShopRoutes()`, …). */
export interface SparkStorefrontFeature {
  readonly kind: 'shop' | 'ticketing' | 'waitingRoom';
  readonly pages: Partial<Record<string, Type<unknown>>>;
}

/** The shop's routes: catalogue, category and product pages (Commerce.Shop). */
export function withShopRoutes(pages: SparkStorefrontPages<SparkStorefrontShopPage> = {}): SparkStorefrontFeature {
  return { kind: 'shop', pages };
}

/** Ticketing's routes: event list and page, the holder's tickets, a ticket, a transfer (Ticketing.Events / Tickets). */
export function withTicketingRoutes(pages: SparkStorefrontPages<SparkStorefrontTicketingPage> = {}): SparkStorefrontFeature {
  return { kind: 'ticketing', pages };
}

/** The waiting room's route, `queue/{key}` (WaitingRoom). */
export function withWaitingRoomRoutes(pages: SparkStorefrontPages<SparkStorefrontQueuePage> = {}): SparkStorefrontFeature {
  return { kind: 'waitingRoom', pages };
}

function isFeature(value: SparkStorefrontOptions | SparkStorefrontFeature): value is SparkStorefrontFeature {
  return typeof (value as SparkStorefrontFeature).kind === 'string' && 'pages' in value;
}

/** Splits `provideSparkCommerce`'s arguments into the options and the features (pure, for the unit test). */
export function storefrontArguments(args: (SparkStorefrontOptions | SparkStorefrontFeature)[]): { options: SparkStorefrontOptions; features: SparkStorefrontFeature[] } {
  const options = args.filter((a): a is SparkStorefrontOptions => !isFeature(a)).reduce<SparkStorefrontOptions>((all, o) => ({ ...all, ...o }), {});
  return { options, features: args.filter(isFeature) };
}

/** The resolved configuration of a set of arguments. */
export function storefrontConfig(options: SparkStorefrontOptions, features: SparkStorefrontFeature[]): SparkStorefrontConfig {
  return {
    paths: resolvePaths(options.paths),
    shop: features.some((f) => f.kind === 'shop'),
    ticketing: features.some((f) => f.kind === 'ticketing'),
    waitingRoom: features.some((f) => f.kind === 'waitingRoom'),
    signInUrl: options.signInUrl ?? null,
  };
}

/**
 * The storefront's routes for explicit placement in the host's own route table (instead of `provideSparkCommerce`'s
 * automatic registration): `provideRouter([...sparkStorefrontRoutes({}, withShopRoutes()), { path: '**', … }])`.
 * Needs `withComponentInputBinding()`: the pages take `slug`, `number`, `code`, `key` and query values as inputs.
 */
export function sparkStorefrontRoutes(...args: (SparkStorefrontOptions | SparkStorefrontFeature)[]): Routes {
  const { options, features } = storefrontArguments(args);
  const config = storefrontConfig(options, features);
  const p = config.paths;
  const core = options.pages ?? {};
  const page = <K extends string>(pages: Partial<Record<K, Type<unknown>>>, key: K, fallback: Type<unknown>) => pages[key] ?? fallback;
  const legal = (key: SparkStorefrontCorePage, kind: string): Route =>
    ({ path: `${p.legal}/${key}`, component: page(core, key, SparkStorefrontLegalPage), data: { kind } });

  const children: Routes = [
    { path: `${p.checkout}/basket`, component: page(core, 'basket', SparkStorefrontBasketPage) },
    { path: `${p.checkout}/orders/:number`, component: page(core, 'order', SparkStorefrontOrderPage) },
    { path: p.checkout, pathMatch: 'full', component: page(core, 'checkout', SparkStorefrontCheckoutPage) },
    { path: p.account, component: page(core, 'account', SparkStorefrontAccountPage) },
    legal('terms', SPARK_LEGAL_KINDS.terms),
    legal('privacy', SPARK_LEGAL_KINDS.privacy),
    legal('withdrawal', SPARK_LEGAL_KINDS.withdrawal),
    legal('cookies', SPARK_LEGAL_KINDS.cookies),
    { path: `${p.legal}/accessibility`, component: page(core, 'accessibility', SparkStorefrontAccessibilityPage) },
    { path: p.consent, children: sparkConsentRoutes() },
    { path: p.commerce, children: sparkCommerceRoutes() },
    { path: `${p.promotions}/back-in-stock/confirm`, component: page(core, 'backInStockConfirm', SparkStorefrontBackInStockPage), data: { action: 'confirm' } },
    { path: `${p.promotions}/back-in-stock/unsubscribe`, component: page(core, 'backInStockUnsubscribe', SparkStorefrontBackInStockPage), data: { action: 'unsubscribe' } },
  ];

  for (const feature of features) {
    const pages = feature.pages;
    if (feature.kind === 'shop') {
      children.push(
        { path: `${p.shop}/item`, component: page(pages, 'item', SparkStorefrontItemPage) },
        { path: `${p.shop}/products/:slug`, component: page(pages, 'product', SparkStorefrontProductPage) },
        { path: `${p.shop}/categories/:slug`, component: page(pages, 'category', SparkStorefrontCatalogPage) },
        { path: p.shop, pathMatch: 'full', component: page(pages, 'catalog', SparkStorefrontCatalogPage) },
      );
    } else if (feature.kind === 'ticketing') {
      children.push(
        { path: `${p.events}/:slug`, component: page(pages, 'event', SparkStorefrontEventPage) },
        { path: p.events, pathMatch: 'full', component: page(pages, 'events', SparkStorefrontEventsPage) },
        { path: `${p.tickets}/transfer/:transferId`, component: page(pages, 'transfer', SparkTicketTransferComponent) },
        { path: `${p.tickets}/:code`, component: page(pages, 'ticket', SparkTicketComponent) },
        { path: p.tickets, pathMatch: 'full', component: page(pages, 'tickets', SparkStorefrontTicketsPage) },
      );
    } else if (feature.kind === 'waitingRoom') {
      children.push({ path: `${p.queue}/:key`, component: page(pages, 'waitingRoom', SparkStorefrontWaitingRoomPage) });
    }
  }

  // The configuration travels with the routes, so explicit placement needs no provider of its own.
  const providers = [{ provide: SPARK_STOREFRONT, useValue: config }];
  const shell = options.shell === undefined ? SparkStorefrontShellComponent : options.shell;
  return [shell ? { path: '', component: shell, providers, children } : { path: '', providers, children }];
}

/**
 * The storefront (commerce #521 M17, PRD §3.3 and §4.14): routed, replaceable pages for the basket, checkout, orders,
 * the customer account area, the legal pages and the accessibility statement, plus the features' pages.
 *
 * ```ts
 * bootstrapApplication(App, { providers: [
 *   provideSparkCommerce({ paths: { shop: 'winkel' } }, withShopRoutes(), withTicketingRoutes(), withWaitingRoomRoutes()),
 *   provideRouter(appRoutes, withComponentInputBinding()),
 * ] });
 * ```
 *
 * The routes are registered through the router's `ROUTES` token, ahead of the app's own when this provider comes first
 * (a `**` route in the app's table would otherwise win). Use {@link sparkStorefrontRoutes} to place them yourself.
 * Each page is replaceable: `withShopRoutes({ product: MyProductPage })`, `{ pages: { basket: MyBasket } }`.
 */
export function provideSparkCommerce(...args: (SparkStorefrontOptions | SparkStorefrontFeature)[]): EnvironmentProviders {
  const { options, features } = storefrontArguments(args);
  return makeEnvironmentProviders([
    { provide: SPARK_STOREFRONT, useValue: storefrontConfig(options, features) },
    { provide: ROUTES, multi: true, useValue: sparkStorefrontRoutes(...args) },
  ]);
}
