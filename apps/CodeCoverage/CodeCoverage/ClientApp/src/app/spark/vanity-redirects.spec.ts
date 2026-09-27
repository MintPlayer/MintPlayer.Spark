import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, CanActivateFn, convertToParamMap, Params, provideRouter, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub } from '../../testing/test-utils';
import { HOME_URL } from './home-route';
import { accountRedirectGuard, commitRedirectGuard, repositoryRedirectGuard } from './vanity-redirects';

/**
 * The vanity URLs people hold (README badges, the accounts grid) forward INTO the generic
 * /po/{type}/{id} page. Every failure — private, missing, network — lands on Home, and they must be
 * indistinguishable (#453): a different destination for "private" would be an existence oracle.
 */

function snapshot(params: Params, queryParams: Params = {}): ActivatedRouteSnapshot {
  return { paramMap: convertToParamMap(params), queryParams } as unknown as ActivatedRouteSnapshot;
}

async function run(
  guard: CanActivateFn,
  browse: BrowseServiceStub,
  params: Params,
  queryParams: Params = {},
): Promise<string> {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideBrowseStub(browse)],
  });
  const result = await TestBed.runInInjectionContext(() => guard(snapshot(params, queryParams), {} as RouterStateSnapshot));
  expect(result).toBeInstanceOf(UrlTree);
  return TestBed.inject(Router).serializeUrl(result as UrlTree);
}

describe('accountRedirectGuard', () => {
  it('forwards to the account detail page, keeping the query string', async () => {
    const browse = createBrowseStub({ getAccount: () => Promise.resolve({ id: 'accounts/github-1' }) });

    const url = await run(accountRedirectGuard, browse, { provider: 'github', login: 'mintplayer' }, { tab: 'repos' });

    expect(browse.getAccount).toHaveBeenCalledWith('github', 'mintplayer');
    expect(url).toBe('/po/account/accounts%2Fgithub-1?tab=repos');
  });

  it('goes Home when the lookup fails', async () => {
    const browse = createBrowseStub({ getAccount: () => Promise.reject(new Error('404')) });

    expect(await run(accountRedirectGuard, browse, { provider: 'github', login: 'nobody' }, { tab: 'repos' })).toBe(HOME_URL);
  });

  it('passes an empty string for a missing parameter rather than null', async () => {
    const browse = createBrowseStub({ getAccount: () => Promise.reject(new Error('404')) });

    await run(accountRedirectGuard, browse, {});

    expect(browse.getAccount).toHaveBeenCalledWith('', '');
  });
});

describe('repositoryRedirectGuard', () => {
  it('forwards to the repository detail page, keeping ?flag= alive', async () => {
    const browse = createBrowseStub({ getRepo: () => Promise.resolve({ id: 'repos/7' }) });

    const url = await run(repositoryRedirectGuard, browse, { provider: 'gitlab', owner: 'acme', repo: 'widgets' }, { flag: 'unit', branch: 'dev' });

    expect(browse.getRepo).toHaveBeenCalledWith('gitlab', 'acme', 'widgets');
    expect(url).toBe('/po/repository/repos%2F7?flag=unit&branch=dev');
  });

  // Private and missing answer identically on the server (#453); the client must not split them.
  it('goes Home, dropping the query string, when the lookup fails', async () => {
    const browse = createBrowseStub({ getRepo: () => Promise.reject(new Error('404')) });

    expect(await run(repositoryRedirectGuard, browse, { provider: 'github', owner: 'acme', repo: 'secret' }, { flag: 'unit' })).toBe(HOME_URL);
  });

  it('passes empty strings for missing parameters', async () => {
    const browse = createBrowseStub({ getRepo: () => Promise.reject(new Error('404')) });

    await run(repositoryRedirectGuard, browse, { provider: 'github' });

    expect(browse.getRepo).toHaveBeenCalledWith('github', '', '');
  });
});

describe('commitRedirectGuard', () => {
  it('forwards to the commit detail page, keeping the query string', async () => {
    const browse = createBrowseStub({ getCommit: () => Promise.resolve({ id: 'commits/abc' }) });

    const url = await run(commitRedirectGuard, browse, { provider: 'github', owner: 'acme', repo: 'widgets', sha: 'abc123' }, { flag: 'e2e' });

    expect(browse.getCommit).toHaveBeenCalledWith('github', 'acme', 'widgets', 'abc123');
    expect(url).toBe('/po/commit/commits%2Fabc?flag=e2e');
  });

  it('goes Home when the lookup fails', async () => {
    const browse = createBrowseStub({ getCommit: () => Promise.reject(new Error('403')) });

    expect(await run(commitRedirectGuard, browse, { provider: 'github', owner: 'acme', repo: 'widgets', sha: 'dead' })).toBe(HOME_URL);
  });

  it('passes empty strings for missing parameters', async () => {
    const browse = createBrowseStub({ getCommit: () => Promise.reject(new Error('404')) });

    await run(commitRedirectGuard, browse, {});

    expect(browse.getCommit).toHaveBeenCalledWith('', '', '', '');
  });
});
