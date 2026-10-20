import { describe, expect, it } from 'vitest';
import { Route } from '@angular/router';

import { inAppPath, isInAppUrl, phaseRoomKey, resolvePaths, storefrontUrl } from './storefront-paths';
import { inAppPathOf } from './storefront-links';
import { metaDescription, STOREFRONT_META_DESCRIPTION_MAX } from './storefront-meta';
import { fillAccessibilityStatement, SPARK_ACCESSIBILITY_STATEMENT_TEMPLATE, unfilledPlaceholders } from './accessibility-statement-template';
import {
  sparkStorefrontRoutes, storefrontArguments, storefrontConfig, withShopRoutes, withTicketingRoutes, withWaitingRoomRoutes,
} from './provide-spark-commerce';
import { accountSections, signInHref } from './pages/spark-storefront-account.page';
import { SparkStorefrontShellComponent } from './pages/spark-storefront-shell.component';
import { SparkStorefrontProductPage } from './pages/spark-storefront-product.page';

// M17: the storefront's routing, links, meta tags and accessibility statement.

const ORIGIN = 'https://shop.example';

function paths(routes: Route[]): string[] {
  return routes.flatMap((r) => [r.path ?? '', ...paths(r.children ?? []).map((c) => `${r.path ? r.path + '/' : ''}${c}`)]);
}

describe('storefront routes', () => {
  it('registers the core pages under the shell, matching the server page paths', () => {
    const routes = sparkStorefrontRoutes();
    expect(routes).toHaveLength(1);
    expect(routes[0].component).toBe(SparkStorefrontShellComponent);
    const all = paths(routes);
    for (const p of ['checkout/basket', 'checkout/orders/:number', 'checkout', 'account', 'legal/terms', 'legal/privacy',
      'legal/withdrawal', 'legal/cookies', 'legal/accessibility', 'consent/settings', 'consent/unsubscribe', 'commerce/traders/:organizationId']) {
      expect(all).toContain(p);
    }
    expect(all).not.toContain('shop');
    expect(all).not.toContain('events/:slug');
  });

  it('adds each feature only when asked', () => {
    const all = paths(sparkStorefrontRoutes(withShopRoutes(), withTicketingRoutes(), withWaitingRoomRoutes()));
    for (const p of ['shop', 'shop/products/:slug', 'shop/categories/:slug', 'events', 'events/:slug', 'tickets', 'tickets/:code',
      'tickets/transfer/:transferId', 'queue/:key']) {
      expect(all).toContain(p);
    }
  });

  it('puts the specific ticket routes before the code route', () => {
    const children = sparkStorefrontRoutes(withTicketingRoutes())[0].children ?? [];
    const transfer = children.findIndex((r) => r.path === 'tickets/transfer/:transferId');
    const code = children.findIndex((r) => r.path === 'tickets/:code');
    expect(transfer).toBeGreaterThanOrEqual(0);
    expect(transfer).toBeLessThan(code);
  });

  it('replaces one page per route and renames segments', () => {
    class MyProduct {}
    const routes = sparkStorefrontRoutes({ paths: { shop: '/winkel/' } }, withShopRoutes({ product: MyProduct }));
    const product = routes[0].children?.find((r) => r.path === 'winkel/products/:slug');
    expect(product?.component).toBe(MyProduct);
    expect(product?.component).not.toBe(SparkStorefrontProductPage);
  });

  it('can run without a shell and still carries its configuration', () => {
    const routes = sparkStorefrontRoutes({ shell: null }, withShopRoutes());
    expect(routes[0].component).toBeUndefined();
    expect(routes[0].providers?.length).toBe(1);
  });

  it('splits options from features', () => {
    const { options, features } = storefrontArguments([{ paths: { account: 'me' } }, withShopRoutes()]);
    expect(options.paths?.account).toBe('me');
    expect(features.map((f) => f.kind)).toEqual(['shop']);
    const config = storefrontConfig(options, features);
    expect(config).toMatchObject({ shop: true, ticketing: false, waitingRoom: false });
    expect(config.paths.account).toBe('me');
  });
});

describe('storefront links', () => {
  it('normalises segments and escapes parts', () => {
    expect(resolvePaths({ checkout: '/kassa/' }).checkout).toBe('kassa');
    expect(storefrontUrl('checkout', 'orders', '2026/0001')).toBe('/checkout/orders/2026%2F0001');
  });

  it('follows only in-app URLs (no open redirect)', () => {
    expect(isInAppUrl('/events/x', ORIGIN)).toBe(true);
    expect(isInAppUrl('//evil.example/x', ORIGIN)).toBe(false);
    expect(isInAppUrl('https://evil.example/x', ORIGIN)).toBe(false);
    expect(isInAppUrl(`${ORIGIN}/events/x?a=1`, ORIGIN)).toBe(true);
    expect(inAppPath(`${ORIGIN}/events/x?a=1#k`, ORIGIN)).toBe('/events/x?a=1#k');
  });

  it('routes page links but leaves API paths, files and other origins to the browser', () => {
    expect(inAppPathOf('/shop/products/mug', ORIGIN)).toBe('/shop/products/mug');
    expect(inAppPathOf('/tickets/AB12#k=s3cr3t', ORIGIN)).toBe('/tickets/AB12#k=s3cr3t');
    expect(inAppPathOf('/spark/commerce/legal/pdf?id=1', ORIGIN)).toBeNull();
    expect(inAppPathOf('/files/terms.pdf', ORIGIN)).toBeNull();
    expect(inAppPathOf('https://carrier.example/track', ORIGIN)).toBeNull();
    expect(inAppPathOf('#top', ORIGIN)).toBeNull();
    expect(inAppPathOf('mailto:a@b.c', ORIGIN)).toBeNull();
  });

  it('keys a phase room exactly like the server', async () => {
    // Expected values computed from CommerceWaitingRoomScopes.RoomKey's algorithm (SHA-256 prefix of the exact id).
    expect(await phaseRoomKey('TicketSalePhases/1-A')).toBe('ticketing-ticketsalephases-1-a-ff812631');
    expect(await phaseRoomKey('x')).toBe('ticketing-x-2d711642');
  });
});

describe('storefront meta and account', () => {
  it('cuts long descriptions on a word boundary', () => {
    const long = 'word '.repeat(200);
    const cut = metaDescription(long);
    expect(cut.length).toBeLessThanOrEqual(STOREFRONT_META_DESCRIPTION_MAX);
    expect(cut.endsWith('…')).toBe(true);
    expect(metaDescription('  short\n text ')).toBe('short text');
  });

  it('offers the account sections of the enabled features only', () => {
    expect(accountSections({ shop: false, ticketing: false })).toEqual(['orders', 'guestOrders', 'consent']);
    expect(accountSections({ shop: true, ticketing: true })).toEqual(['orders', 'guestOrders', 'tickets', 'addresses', 'backInStock', 'consent']);
  });
});

describe('accessibility statement template', () => {
  it('has the same placeholders in every language, including the hold-timer reasoning', () => {
    const en = unfilledPlaceholders(SPARK_ACCESSIBILITY_STATEMENT_TEMPLATE.en).sort();
    expect(unfilledPlaceholders(SPARK_ACCESSIBILITY_STATEMENT_TEMPLATE.fr).sort()).toEqual(en);
    expect(unfilledPlaceholders(SPARK_ACCESSIBILITY_STATEMENT_TEMPLATE.nl).sort()).toEqual(en);
    expect(en).toContain('holdMax');
    expect(SPARK_ACCESSIBILITY_STATEMENT_TEMPLATE.en).toContain('2.2.1');
  });

  it('fills what it is given and reports what is left', () => {
    const text = fillAccessibilityStatement('en', { organisation: 'Acme', service: 'Acme Tickets' });
    expect(text).toContain('Acme Tickets');
    expect(unfilledPlaceholders(text)).not.toContain('service');
    expect(unfilledPlaceholders(text)).toContain('contact');
  });
});

describe('storefront follow-ups (M17)', () => {
  it('routes the back-in-stock mail links always, and the item link with the shop', () => {
    expect(paths(sparkStorefrontRoutes())).toEqual(expect.arrayContaining(['promotions/back-in-stock/confirm', 'promotions/back-in-stock/unsubscribe']));
    expect(paths(sparkStorefrontRoutes())).not.toContain('shop/item');
    expect(paths(sparkStorefrontRoutes(withShopRoutes()))).toContain('shop/item');
  });

  it('carries the sign-in URL and fills in the return URL', () => {
    expect(storefrontConfig({ signInUrl: '/login?returnUrl={returnUrl}' }, []).signInUrl).toBe('/login?returnUrl={returnUrl}');
    expect(storefrontConfig({}, []).signInUrl).toBeNull();
    expect(signInHref('/login?returnUrl={returnUrl}', '/account')).toBe('/login?returnUrl=%2Faccount');
    expect(signInHref(null, '/account')).toBeNull();
  });
});
