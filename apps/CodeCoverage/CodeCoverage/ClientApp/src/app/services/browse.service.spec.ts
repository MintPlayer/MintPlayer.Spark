import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { BrowseService, coveragePercent } from './browse.service';

describe('coveragePercent', () => {
  it('reports covered lines as a percentage of coverable lines', () => {
    expect(coveragePercent({ linesCovered: 75, linesCoverable: 100 } as never)).toBe(75);
    expect(coveragePercent({ linesCovered: 1, linesCoverable: 3 } as never)).toBeCloseTo(33.333, 3);
  });

  // The upload contract is explicit that 0/0 is no data, not full coverage. This
  // is the front-end half of that rule: a repository with nothing measured must
  // render as "—", never as a green 100% bar.
  it('returns null when nothing is coverable', () => {
    expect(coveragePercent({ linesCovered: 0, linesCoverable: 0 } as never)).toBeNull();
  });

  it('returns null when there is no summary at all', () => {
    expect(coveragePercent(null)).toBeNull();
    expect(coveragePercent(undefined)).toBeNull();
  });

  it('reports zero for a measured file that covers nothing', () => {
    expect(coveragePercent({ linesCovered: 0, linesCoverable: 40 } as never)).toBe(0);
  });
});

describe('BrowseService', () => {
  // Hostile route values: a '/' inside a segment must not split the route, a space must not
  // end it, and a '#' must not truncate the URL into a fragment.
  const provider = 'git hub';
  const owner = 'my org/x';
  const name = 'repo#1';
  const sha = 'ab/cd';
  const P = 'git%20hub';
  const O = 'my%20org%2Fx';
  const N = 'repo%231';
  const S = 'ab%2Fcd';
  const repoBase = `/api/browse/repos/${P}/${O}/${N}`;

  let service: BrowseService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(BrowseService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Asserts exactly one request to `url` with `method`, answers it, and returns it. */
  function answer(method: string, url: string, body: object) {
    const req = http.expectOne((r) => r.url === url);
    expect(req.request.method).toBe(method);
    req.flush(body);
    return req.request;
  }

  it('getAccount / getAccountRepos / getSparklines encode provider and login', async () => {
    const account = service.getAccount(provider, owner);
    answer('GET', `/api/browse/accounts/${P}/${O}`, { id: 'a1', login: owner });
    expect(await account).toEqual({ id: 'a1', login: owner });

    const repos = service.getAccountRepos(provider, owner);
    answer('GET', `/api/browse/accounts/${P}/${O}/repos`, []);
    expect(await repos).toEqual([]);

    const sparklines = service.getSparklines(provider, owner);
    answer('GET', `/api/browse/accounts/${P}/${O}/sparklines`, { r: [1, 2] });
    expect(await sparklines).toEqual({ r: [1, 2] });
  });

  it('getRepo and getBranches encode every route segment', async () => {
    const repo = service.getRepo(provider, owner, name);
    answer('GET', repoBase, { id: 'r1' });
    expect(await repo).toEqual({ id: 'r1' });

    const branches = service.getBranches(provider, owner, name);
    answer('GET', `${repoBase}/branches`, ['main']);
    expect(await branches).toEqual(['main']);
  });

  it('getHistory sends ?branch= only when a branch is given', async () => {
    const withBranch = service.getHistory(provider, owner, name, 'feat/x y');
    const req = answer('GET', `${repoBase}/history`, []);
    expect(req.params.get('branch')).toBe('feat/x y');
    await withBranch;

    const without = service.getHistory(provider, owner, name);
    expect(answer('GET', `${repoBase}/history`, []).params.keys()).toEqual([]);
    await without;

    // '' is "the default branch" to callers; it must not become ?branch=.
    const empty = service.getHistory(provider, owner, name, '');
    expect(answer('GET', `${repoBase}/history`, []).params.has('branch')).toBe(false);
    await empty;
  });

  it('getCommits sends ?branch= only when a branch is given', async () => {
    const withBranch = service.getCommits(provider, owner, name, 'main');
    expect(answer('GET', `${repoBase}/commits`, []).params.get('branch')).toBe('main');
    await withBranch;

    const without = service.getCommits(provider, owner, name);
    expect(answer('GET', `${repoBase}/commits`, []).params.keys()).toEqual([]);
    await without;
  });

  it('getCommit and getHierarchy encode the sha', async () => {
    const commit = service.getCommit(provider, owner, name, sha);
    answer('GET', `${repoBase}/commits/${S}`, { id: 'c1' });
    expect(await commit).toEqual({ id: 'c1' });

    const hierarchy = service.getHierarchy(provider, owner, name, sha);
    answer('GET', `${repoBase}/commits/${S}/hierarchy`, { id: '/', name: 'root' });
    expect(await hierarchy).toEqual({ id: '/', name: 'root' });
  });

  it('getTree sends path and flag only when present', async () => {
    const both = service.getTree(provider, owner, name, sha, 'src/a b#', 'unit');
    const req = answer('GET', `${repoBase}/commits/${S}/tree`, { entries: [] });
    expect(req.params.get('path')).toBe('src/a b#');
    expect(req.params.get('flag')).toBe('unit');
    // The '#' travels encoded in the query string, never as a fragment.
    expect(req.urlWithParams).toContain('%23');
    await both;

    const none = service.getTree(provider, owner, name, sha);
    expect(answer('GET', `${repoBase}/commits/${S}/tree`, { entries: [] }).params.keys()).toEqual([]);
    await none;

    const flagOnly = service.getTree(provider, owner, name, sha, undefined, 'e2e');
    expect(answer('GET', `${repoBase}/commits/${S}/tree`, { entries: [] }).params.keys()).toEqual(['flag']);
    await flagOnly;
  });

  it('getFile always sends the path as a query parameter', async () => {
    const file = service.getFile(provider, owner, name, sha, 'src/my file#.ts');
    const req = answer('GET', `${repoBase}/commits/${S}/file`, { path: 'x' });
    expect(req.params.get('path')).toBe('src/my file#.ts');
    expect(req.urlWithParams).not.toContain('#');
    expect(await file).toEqual({ path: 'x' });
  });

  it('rotateBadgeToken POSTs to the repository settings endpoint', async () => {
    const result = service.rotateBadgeToken(provider, owner, name);
    const req = answer('POST', `/api/repos/${P}/${O}/${N}/settings/badge-token`, { badgeToken: 't0k' });
    expect(req.body).toEqual({});
    expect(await result).toEqual({ badgeToken: 't0k' });
  });

  it('rejects when the server answers with an error', async () => {
    const repo = service.getRepo(provider, owner, name);
    http.expectOne(repoBase).flush('nope', { status: 404, statusText: 'Not Found' });
    await expect(repo).rejects.toMatchObject({ status: 404 });
  });
});
