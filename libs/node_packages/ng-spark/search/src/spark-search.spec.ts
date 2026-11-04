import { DOCUMENT } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EnvironmentInjector, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { provideSpark } from '@mintplayer/ng-spark';
import { SparkSearchService } from './spark-search.service';
import { SPARK_SEARCH_OPTIONS, SparkSearchHit } from './spark-search.models';
import { SparkSiteSearchComponent } from './spark-site-search.component';
import { groupSearchHits } from './spark-search-page.component';
import { sparkSearchRoutes, withOpenSearchDescription, withSearch } from './provide-spark-search';

const hit = (entityType: string, id: string, label = id): SparkSearchHit =>
  ({ entityType, typeLabel: entityType.toUpperCase(), id, label, url: `/po/${entityType}/${id}` });

function setup(...features: Parameters<typeof provideSpark>) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideSpark(...features),
    ],
  });
  return { http: TestBed.inject(HttpTestingController), service: TestBed.inject(SparkSearchService) };
}

const searchLinks = () => document.head.querySelectorAll('link[rel="search"][type="application/opensearchdescription+xml"]');

afterEach(() => {
  searchLinks().forEach(link => link.remove());
  TestBed.resetTestingModule();
});

describe('SparkSearchService', () => {
  it('escapes the trimmed term as encodeURIComponent does (the prerender contributor predicts that URL)', () => {
    const { http, service } = setup(withSearch());
    service.search("  rock 'n' roll & co  ").subscribe();
    http.expectOne("/spark/search?q=rock%20'n'%20roll%20%26%20co").flush([]);
    http.verify();
  });

  it('resolve answers null on 404 and the hit otherwise', async () => {
    const { http, service } = setup(withSearch());
    const none = service.resolve('queen');
    http.expectOne('/spark/search/resolve?q=queen').flush(null, { status: 404, statusText: 'Not Found' });
    expect(await none).toBeNull();

    const one = service.resolve('Queen (Artist)');
    http.expectOne('/spark/search/resolve?q=Queen%20(Artist)').flush(hit('artist', 'artists/1'));
    expect(await one).toEqual(hit('artist', 'artists/1'));
  });
});

describe('groupSearchHits', () => {
  it('groups by type in the order of each type\'s first hit, keeping the order inside a group', () => {
    const groups = groupSearchHits([hit('song', 's1'), hit('artist', 'a1'), hit('song', 's2')]);
    expect(groups.map(g => [g.typeLabel, g.hits.map(h => h.id)])).toEqual([['SONG', ['s1', 's2']], ['ARTIST', ['a1']]]);
  });
});

describe('withSearch / sparkSearchRoutes', () => {
  it('defaults to /search and opening a single hit', () => {
    setup(withSearch());
    expect(TestBed.inject(SPARK_SEARCH_OPTIONS)).toEqual({ pagePath: '/search', singleHit: 'item' });
    expect(sparkSearchRoutes()[0].path).toBe('search');
  });
});

describe('withOpenSearchDescription', () => {
  it('adds the <link rel="search"> and withSearch', () => {
    setup(withOpenSearchDescription({ title: 'MintPlayer' }));
    TestBed.inject(EnvironmentInjector);
    const links = searchLinks();
    expect(links.length).toBe(1);
    expect(links[0].getAttribute('title')).toBe('MintPlayer');
    expect(links[0].getAttribute('href')).toBe('/opensearch.xml');
    expect(TestBed.inject(SPARK_SEARCH_OPTIONS).pagePath).toBe('/search');
  });

  it('updates the server-rendered link instead of adding a second one', () => {
    const existing = TestBed.inject(DOCUMENT).createElement('link');
    existing.setAttribute('rel', 'search');
    existing.setAttribute('type', 'application/opensearchdescription+xml');
    existing.setAttribute('title', 'old');
    document.head.appendChild(existing);
    TestBed.resetTestingModule();

    setup(withOpenSearchDescription({ title: 'New', href: '/app/opensearch.xml' }));
    TestBed.inject(EnvironmentInjector);
    const links = searchLinks();
    expect(links.length).toBe(1);
    expect(links[0].getAttribute('title')).toBe('New');
    expect(links[0].getAttribute('href')).toBe('/app/opensearch.xml');
  });

  it('keeps an explicitly listed withSearch', () => {
    setup(withSearch({ pagePath: '/zoeken' }), withOpenSearchDescription({ title: 'X' }));
    expect(TestBed.inject(SPARK_SEARCH_OPTIONS).pagePath).toBe('/zoeken');
  });
});

describe('SparkSiteSearchComponent', () => {
  type Submit = { submit(text: string): Promise<void>; open(hit: SparkSearchHit): void };

  function create(...features: Parameters<typeof provideSpark>) {
    const { http } = setup(...features);
    const router = TestBed.inject(Router);
    const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const box = TestBed.createComponent(SparkSiteSearchComponent).componentInstance as unknown as Submit;
    return { http, box, navigateByUrl, navigate };
  }

  it('Enter on a term that names one object opens it', async () => {
    const { http, box, navigateByUrl } = create(withSearch());
    const done = box.submit(' queen ');
    http.expectOne('/spark/search/resolve?q=queen').flush(hit('artist', 'artists/1'));
    await done;
    expect(navigateByUrl).toHaveBeenCalledWith('/po/artist/artists/1');
  });

  it('Enter on a term that names none or several opens the results page', async () => {
    const { http, box, navigate } = create(withSearch());
    const done = box.submit('top');
    http.expectOne('/spark/search/resolve?q=top').flush(null, { status: 404, statusText: 'Not Found' });
    await done;
    expect(navigate).toHaveBeenCalledWith(['/search'], { queryParams: { q: 'top' } });
  });

  it('singleHit searchPage always lists the results, without a resolve', async () => {
    const { http, box, navigate } = create(withSearch({ singleHit: 'searchPage' }));
    await box.submit('queen');
    http.expectNone(() => true);
    expect(navigate).toHaveBeenCalledWith(['/search'], { queryParams: { q: 'queen' } });
  });

  it('a picked suggestion opens its object', () => {
    const { box, navigateByUrl } = create(withSearch());
    box.open(hit('song', 'songs/7'));
    expect(navigateByUrl).toHaveBeenCalledWith('/po/song/songs/7');
  });
});
