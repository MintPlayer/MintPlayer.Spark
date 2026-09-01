import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { BrowseService } from '../services/browse.service';
import { HOME_URL } from './home-route';

/**
 * The account, repository and commit pages are the generic Spark detail pages
 * (`spark-po-detail` + a few app panels). These guards keep the readable,
 * shareable URLs working — README badge markdown links to /r/{owner}/{name},
 * and /a/{login} is what the accounts grid and the file page link to — by
 * resolving the document id and forwarding into the generic page.
 *
 * The direction matters: the vanity URL is the one people hold, so it forwards
 * INTO /po/{type}/{id} rather than the other way round. A document id is not a
 * name, and nobody should have to paste one.
 */
export const accountRedirectGuard: CanActivateFn = async (route) => {
  const browse = inject(BrowseService);
  const router = inject(Router);
  const login = route.paramMap.get('login') ?? '';
  try {
    const account = await browse.getAccount(login);
    return router.createUrlTree(['/po', 'account', account.id], { queryParams: route.queryParams });
  } catch {
    return router.createUrlTree([HOME_URL]);
  }
};

export const repositoryRedirectGuard: CanActivateFn = async (route) => {
  const browse = inject(BrowseService);
  const router = inject(Router);
  const owner = route.paramMap.get('owner') ?? '';
  const name = route.paramMap.get('repo') ?? '';
  try {
    const repo = await browse.getRepo(owner, name);
    // Keep query params (e.g. ?flag=) alive through the redirect.
    return router.createUrlTree(['/po', 'repository', repo.id], { queryParams: route.queryParams });
  } catch {
    return router.createUrlTree([HOME_URL]);
  }
};

export const commitRedirectGuard: CanActivateFn = async (route) => {
  const browse = inject(BrowseService);
  const router = inject(Router);
  const owner = route.paramMap.get('owner') ?? '';
  const name = route.paramMap.get('repo') ?? '';
  const sha = route.paramMap.get('sha') ?? '';
  try {
    const commit = await browse.getCommit(owner, name, sha);
    return router.createUrlTree(['/po', 'commit', commit.id], { queryParams: route.queryParams });
  } catch {
    return router.createUrlTree([HOME_URL]);
  }
};
