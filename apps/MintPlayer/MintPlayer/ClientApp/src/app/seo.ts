import { PersistentObject } from '@mintplayer/ng-spark/models';
import { SparkSeoOptions, attributeText } from '@mintplayer/ng-spark/services';

/**
 * The JSON-LD the model mapping cannot express (docs/spark_ssr_PRD.md D12): a song's artists. It reads only the
 * presented object, so an artist the viewer may not see is not named.
 */
export const mintPlayerSeo: SparkSeoOptions = {
  structuredData: {
    Song: po => {
      const artists = asDetailRows(po, 'Artists')
        .map(row => attributeText(row, 'ArtistId'))
        .filter((name): name is string => !!name)
        .map(name => ({ '@type': 'MusicGroup', name }));
      return artists.length ? { byArtist: artists } : null;
    },
  },
};

// An AsDetail array travels in the attribute's `objects`; its `value` is null.
function asDetailRows(po: PersistentObject, attribute: string): PersistentObject[] {
  return po.attributes.find(a => a.name === attribute)?.objects ?? [];
}
