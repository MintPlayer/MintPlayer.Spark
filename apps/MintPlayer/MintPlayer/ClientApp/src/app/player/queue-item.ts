import { AS_DETAIL_BREADCRUMBS_KEY, PersistentObject } from '@mintplayer/ng-spark/models';
import { QueueItem } from './player.service';

/** A Media row as the server sends it: the URL, and the resolved name of its type under the breadcrumbs key. */
type MediumRow = Record<string, unknown>;

/**
 * The queue entry for one of `owner`'s media (#601 D22), the same from the Media table's play button and from the
 * song page's header: titled after the owner, subtitled with the medium's type ("Official Audio") as the server
 * resolved it for the Type column.
 */
export function mediumQueueItem(owner: PersistentObject | null | undefined, url: string, row?: MediumRow | null): QueueItem {
  row ??= mediaRows(owner).find(r => r['Value'] === url);
  const type = (row?.[AS_DETAIL_BREADCRUMBS_KEY] as Record<string, string> | undefined)?.['TypeId'];
  return { url, title: owner?.breadcrumb || owner?.name || url, subtitle: type, ownerId: owner?.id };
}

function mediaRows(owner: PersistentObject | null | undefined): MediumRow[] {
  const value = owner?.attributes.find(a => a.name === 'Media')?.value;
  return Array.isArray(value) ? value : [];
}
