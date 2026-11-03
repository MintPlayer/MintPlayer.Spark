import { PersistentObject } from '@mintplayer/ng-spark/models';
import { SparkSeoOptions, attributeText } from '@mintplayer/ng-spark/services';

/**
 * mintplayer.com's SEO beyond the model mapping (docs/spark_ssr_PRD.md D12). Every hook reads only the presented
 * object, so a value the viewer may not see (an artist they may not read) is never named.
 */
export const mintPlayerSeo: SparkSeoOptions = {
  // "Beatles - MintPlayer" is too short for a search result (SeoTester seo.title-length: 30 to 60 characters).
  title: name => {
    const title = `${name} - MintPlayer`;
    return title.length < 30 ? `${name} - MintPlayer, music videos and lyrics` : title;
  },
  siteDescription: 'MintPlayer plays music videos from YouTube, SoundCloud, Vimeo and more, with song lyrics, artists and playlists in one place.',
  image: '/icons/icon-512x512.png',
  description: {
    Song: po => {
      const title = po.breadcrumb || po.name;
      const artists = artistNames(po);
      return `Watch and listen to ${title}${artists.length ? ` by ${artists.join(', ')}` : ''} on MintPlayer, with its music video and lyrics.`;
    },
    Artist: po => `${po.breadcrumb || po.name} on MintPlayer: their songs and music videos, lyrics, and the people in the band.`,
    Person: po => `${po.breadcrumb || po.name} on MintPlayer: the artists and bands they play in, with their songs and music videos.`,
    MediumType: po => `${po.breadcrumb || po.name}: every song on MintPlayer with a medium of this kind, ready to play.`,
    Tag: po => `Songs tagged ${po.breadcrumb || po.name} on MintPlayer: play their music videos and read their lyrics.`,
    Playlist: po => attributeText(po, 'Description')
      ?? `The playlist ${po.breadcrumb || po.name} on MintPlayer: play its songs and music videos one after another.`,
  },
  structuredData: {
    Song: po => {
      const artists = artistNames(po).map(name => ({ '@type': 'MusicGroup', name }));
      return artists.length ? { byArtist: artists } : null;
    },
  },
};

/** A song's artists the viewer may read, by name. */
function artistNames(po: PersistentObject): string[] {
  return asDetailRows(po, 'Artists')
    .map(row => attributeText(row, 'ArtistId'))
    .filter((name): name is string => !!name);
}

// An AsDetail array travels in the attribute's `objects`; its `value` is null.
function asDetailRows(po: PersistentObject, attribute: string): PersistentObject[] {
  return po.attributes.find(a => a.name === attribute)?.objects ?? [];
}
