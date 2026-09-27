import { Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, Route, Router, RouterOutlet, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it } from 'vitest';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub } from '../testing/test-utils';
import { routes } from './app.routes';
import { ShellComponent } from './shell/shell.component';
import { HOME_URL } from './spark/home-route';

/**
 * The route table's ORDER is its collision story: `:provider/...` matches any first segment,
 * including 'po', so every Spark route (literal first segment) must be tried first. Nothing
 * else enforces that — no route constraint, no forge list — so these pin it twice: once on the
 * real exported array, and once by navigating through it.
 */

function firstSegment(route: Route): string {
  return (route.path ?? '').split('/')[0];
}

function shellChildren(): Routes {
  const shell = routes.find(r => r.component === ShellComponent);
  expect(shell).toBeDefined();
  return shell!.children!;
}

describe('app routes: declaration order', () => {
  it('redirects the bare root to Home at the top level, full match only', () => {
    const root = routes[0];

    expect(root.path).toBe('');
    expect(root.redirectTo).toBe(HOME_URL);
    expect(root.pathMatch).toBe('full');
    // Before the shell, which also has path '' and would otherwise consume the empty URL.
    expect(routes.indexOf(root)).toBeLessThan(routes.findIndex(r => r.component === ShellComponent));
  });

  it('declares every Spark and auth route before the first parameterised first segment', () => {
    const children = shellChildren();
    const firstParam = children.findIndex(r => firstSegment(r).startsWith(':'));
    expect(firstParam).toBeGreaterThan(0);

    // sparkAuthRoutes() wraps its pages in one path-'' group; its children carry the paths.
    const literal = children
      .map((route, index) => ({ route, index }))
      .filter(({ route }) => ['po', 'query'].includes(firstSegment(route))
        || (route.path === '' && (route.children ?? []).length > 0));

    const sparkPaths = literal.filter(({ route }) => route.path !== '').map(({ route }) => route.path);
    expect(sparkPaths).toEqual(expect.arrayContaining(['query/:queryId', 'po/:type/new', 'po/:type/:id/edit', 'po/:type/:id', 'po/:type']));

    const authGroup = literal.find(({ route }) => route.path === '');
    expect(authGroup).toBeDefined();
    expect(authGroup!.route.children!.map(c => c.path)).toEqual(expect.arrayContaining(['sign-in', 'passkeys']));

    for (const { route, index } of literal) {
      expect(index, `route '${route.path}' must precede the :provider routes`).toBeLessThan(firstParam);
    }
    // And nothing literal hides after the parameterised block, where it could never match 'po'.
    for (const route of children.slice(firstParam)) {
      expect(firstSegment(route).startsWith(':')).toBe(true);
    }
  });
});

@Component({ selector: 'app-stub-outlet', imports: [RouterOutlet], template: '<router-outlet />' })
class StubOutlet {}

@Component({ selector: 'app-stub-page', template: 'page' })
class StubPage {}

/**
 * The real table with its heavy leaves swapped out, order and paths untouched: the shell (Lit
 * web components) becomes a bare outlet and every lazy page a stub. Guards stay real, backed by
 * a BrowseService stub, so a guard that runs shows up as a lookup call.
 */
function stubbed(table: Routes): Routes {
  return table.map(route => {
    const copy: Route = { ...route };
    if (copy.loadComponent) {
      delete copy.loadComponent;
      copy.component = StubPage;
    }
    if (copy.component === ShellComponent) copy.component = StubOutlet;
    if (copy.children) copy.children = stubbed(copy.children);
    return copy;
  });
}

function leaf(router: Router): ActivatedRouteSnapshot {
  let node = router.routerState.snapshot.root;
  while (node.firstChild) node = node.firstChild;
  return node;
}

describe('app routes: navigation', () => {
  async function setup(): Promise<{ harness: RouterTestingHarness; router: Router; browse: BrowseServiceStub }> {
    const browse = createBrowseStub({
      getAccount: () => Promise.resolve({ id: 'a1' }),
      getRepo: () => Promise.resolve({ id: 'r1' }),
      getCommit: () => Promise.resolve({ id: 'c1' }),
    });
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter(stubbed(routes)), provideBrowseStub(browse)],
    });
    const harness = await RouterTestingHarness.create();
    return { harness, router: TestBed.inject(Router), browse };
  }

  function lookups(browse: BrowseServiceStub): number {
    return browse.getAccount.mock.calls.length + browse.getRepo.mock.calls.length + browse.getCommit.mock.calls.length;
  }

  // Declared the other way round, /po/x/1 would bind provider='po' and never reach the editor.
  it('routes /po/... to the Spark pages without running a vanity guard', async () => {
    const { harness, router, browse } = await setup();

    for (const [url, path] of [
      ['/po/x/1', 'po/:type/:id'],
      ['/po/r/123/edit', 'po/:type/:id/edit'],
      ['/po/x/new', 'po/:type/new'],
      ['/po/x', 'po/:type'],
      ['/query/q1', 'query/:queryId'],
    ]) {
      await harness.navigateByUrl(url);
      expect(router.url).toBe(url);
      expect(leaf(router).routeConfig?.path).toBe(path);
    }
    expect(lookups(browse)).toBe(0);
  });

  it('routes the auth pages without running a vanity guard', async () => {
    const { harness, router, browse } = await setup();

    await harness.navigateByUrl('/sign-in');
    expect(leaf(router).routeConfig?.path).toBe('sign-in');
    await harness.navigateByUrl('/passkeys');
    expect(leaf(router).routeConfig?.path).toBe('passkeys');
    expect(lookups(browse)).toBe(0);
  });

  it('sends / and /home to the Home persistent object', async () => {
    const { harness, router } = await setup();

    await harness.navigateByUrl('/');
    expect(router.url).toBe(HOME_URL);
    await harness.navigateByUrl('/po/x');  // leave Home, so the next redirect is observable
    await harness.navigateByUrl('/home');
    expect(router.url).toBe(HOME_URL);
  });

  it('forwards the forge-scoped vanity URLs through their guards into /po/...', async () => {
    const { harness, router, browse } = await setup();

    await harness.navigateByUrl('/github/a/mintplayer');
    expect(browse.getAccount).toHaveBeenCalledWith('github', 'mintplayer');
    expect(router.url).toBe('/po/account/a1');

    await harness.navigateByUrl('/gitlab/r/acme/widgets?flag=unit');
    expect(browse.getRepo).toHaveBeenCalledWith('gitlab', 'acme', 'widgets');
    expect(router.url).toBe('/po/repository/r1?flag=unit');

    await harness.navigateByUrl('/github/r/acme/widgets/c/abc123');
    expect(browse.getCommit).toHaveBeenCalledWith('github', 'acme', 'widgets', 'abc123');
    expect(router.url).toBe('/po/commit/c1');
  });

  it('keeps the file viewer a page of its own, with no lookup', async () => {
    const { harness, router, browse } = await setup();

    await harness.navigateByUrl('/github/r/acme/widgets/c/abc123/f');

    expect(leaf(router).routeConfig?.path).toBe(':provider/r/:owner/:repo/c/:sha/f');
    expect(leaf(router).paramMap.get('provider')).toBe('github');
    expect(lookups(browse)).toBe(0);
  });
});
