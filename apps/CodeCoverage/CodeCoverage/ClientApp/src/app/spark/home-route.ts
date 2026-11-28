import { inject } from '@angular/core';
import { RedirectFunction, Router } from '@angular/router';

/**
 * Where Home lives, in one place.
 *
 * Home stopped being a hand-written page and became a virtual persistent object, so its URL is
 * now derived from `programUnits.json` — `/po/{alias}/{objectId}` — rather than chosen by the
 * router. Several call sites need it: the empty-path and `/home` redirects, the post-sign-in
 * return URL, and the vanity guards' fallback. Those are strings that must agree with the
 * server's JSON, so they are named once here instead of spelled out at each site.
 */
export const HOME_ROUTE = {
  /** `alias` of the Home program unit's persistent object, from programUnits.json. */
  poAlias: 'home',
  /** `objectId` of the Home program unit. HomeActions ignores it — there is exactly one Home. */
  objectId: 'main',
} as const;

/** The Home page's absolute URL. */
export const HOME_URL = `/po/${HOME_ROUTE.poAlias}/${HOME_ROUTE.objectId}`;

/**
 * The `/` and `/home` redirects to {@link HOME_URL}, keeping the #fragment (#721). A `redirectTo` string that is
 * absolute drops it (`docs/section_fragment_links_PRD.md` S4): `/home#merge-queue` would land on Home's top.
 */
export const redirectToHome: RedirectFunction = ({ fragment }) =>
  inject(Router).createUrlTree([HOME_URL], { fragment: fragment ?? undefined });
