import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { EntityType, PersistentObject } from '@mintplayer/ng-spark/models';
import { SPARK_SERVER_ORIGIN } from './provide-spark-hydration';
import { SparkSeoService, provideSparkSeo } from './spark-seo.service';

/**
 * The detail page's SEO tags (docs/spark_ssr_PRD.md D12): the model mapping, the per-type hook, robots, and
 * nothing left behind for the next page.
 */
describe('SparkSeoService', () => {
  const songType = {
    id: 't1', name: 'Song', attributes: [],
    seo: { indexable: true, schemaType: 'MusicRecording', description: 'Lyrics', properties: { datePublished: 'Released' } },
  } as unknown as EntityType;

  const song = {
    id: 'Songs/1', name: 'Song', objectTypeId: 't1', breadcrumb: 'Halo </script>',
    attributes: [
      { name: 'Lyrics', value: 'Remember those walls' },
      { name: 'Released', value: '2008-01-20' },
    ],
  } as unknown as PersistentObject;

  const create = (providers: unknown[] = []) => {
    TestBed.configureTestingModule({ providers: [{ provide: SPARK_SERVER_ORIGIN, useValue: 'https://example.com' }, ...(providers as any[])] });
    return TestBed.inject(SparkSeoService);
  };

  const meta = (selector: string) => document.head.querySelector(`meta[${selector}]`)?.getAttribute('content') ?? null;
  const jsonLd = () => document.head.querySelector('script[type="application/ld+json"][data-spark-seo]')?.textContent ?? null;

  beforeEach(() => {
    TestBed.resetTestingModule();
    document.head.querySelectorAll('meta, link[rel="canonical"], script[type="application/ld+json"]').forEach(e => e.remove());
    document.title = 'MintPlayer';
    document.head.querySelector('title')?.removeAttribute('data-spark-site');
  });

  it('describes an indexable object from the model mapping', () => {
    create().forObject(songType, song, '/po/song/Songs%2F1/halo-script');

    expect(document.title).toBe('Halo </script> - MintPlayer');
    expect(meta('name="robots"')).toBe('index, follow');
    expect(meta('name="description"')).toBe('Remember those walls');
    expect(meta('property="og:url"')).toBe('https://example.com/po/song/Songs%2F1/halo-script');
    expect(document.head.querySelector('link[rel="canonical"]')?.getAttribute('href')).toBe('https://example.com/po/song/Songs%2F1/halo-script');
    const data = JSON.parse(jsonLd()!);
    expect(data).toMatchObject({ '@type': 'MusicRecording', name: 'Halo </script>', datePublished: '2008-01-20' });
    // A value cannot close the script element.
    expect(jsonLd()).not.toContain('</script>');
  });

  it('merges the type hook over the mapping, given only the presented object', () => {
    let seen: PersistentObject | null = null;
    create([provideSparkSeo({ structuredData: { Song: po => { seen = po; return { byArtist: [{ '@type': 'MusicGroup', name: 'Beyoncé' }] }; } } })])
      .forObject(songType, song, '/po/song/Songs%2F1');

    expect(seen).toBe(song);
    expect(JSON.parse(jsonLd()!).byArtist[0].name).toBe('Beyoncé');
  });

  it('keeps a type without a mapping out of the index', () => {
    create().forObject({ ...songType, seo: undefined } as EntityType, song, '/po/song/Songs%2F1');

    expect(meta('name="robots"')).toBe('noindex');
    expect(jsonLd()).toBeNull();
  });

  it('leaves nothing behind for the next page', () => {
    const seo = create();
    seo.forObject(songType, song, '/po/song/Songs%2F1');

    seo.reset();

    expect(document.title).toBe('MintPlayer');
    expect(meta('name="robots"')).toBeNull();
    expect(meta('property="og:title"')).toBeNull();
    expect(document.head.querySelector('link[rel="canonical"]')).toBeNull();
    expect(jsonLd()).toBeNull();
  });

  it('marks an error or form page noindex', () => {
    const seo = create();
    seo.forObject(songType, song, '/po/song/Songs%2F1');

    seo.noIndex();

    expect(meta('name="robots"')).toBe('noindex');
    expect(meta('name="description"')).toBeNull();
  });

  it('reads the site title back from the element on a hydrating page', () => {
    // The server render already set the object's title and recorded the site's.
    create().forObject(songType, song, '/po/song/Songs%2F1');
    TestBed.resetTestingModule();

    create().forObject(songType, { ...song, breadcrumb: 'Crazy in Love' } as PersistentObject, '/po/song/Songs%2F2');

    expect(document.title).toBe('Crazy in Love - MintPlayer');
  });
});
