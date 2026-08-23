import { describe, expect, it } from 'vitest';
import {
  AS_DETAIL_BREADCRUMBS_KEY,
  AS_DETAIL_SELF_BREADCRUMB_KEY,
  dictToNestedPo,
  selfBreadcrumb,
  nestedPoToDict,
  nestedPoToDisplayRow,
} from './as-detail-conversions';
import { EntityType } from './entity-type';
import { PersistentObject } from './persistent-object';
import { PersistentObjectAttribute } from './persistent-object-attribute';

// Minimal attribute factory — only the fields the conversions read.
function attr(partial: Partial<PersistentObjectAttribute> & { name: string }): PersistentObjectAttribute {
  return {
    id: partial.name,
    isRequired: false,
    isVisible: true,
    isReadOnly: false,
    order: 0,
    rules: [],
    dataType: 'string',
    ...partial,
  } as PersistentObjectAttribute;
}

function po(attributes: PersistentObjectAttribute[], over: Partial<PersistentObject> = {}): PersistentObject {
  return { id: 'x', name: 'X', objectTypeId: 't', attributes, ...over };
}

describe('nestedPoToDict', () => {
  it('returns {} for null / undefined', () => {
    expect(nestedPoToDict(null)).toEqual({});
    expect(nestedPoToDict(undefined)).toEqual({});
  });

  it('maps primitive / reference attributes to their value', () => {
    const result = nestedPoToDict(po([
      attr({ name: 'Title', value: 'Song' }),
      attr({ name: 'ArtistId', dataType: 'Reference', value: 'Artists/40' }),
    ]));
    expect(result).toEqual({ Title: 'Song', ArtistId: 'Artists/40' });
  });

  it('recurses single + array AsDetail into inner dicts', () => {
    const result = nestedPoToDict(po([
      attr({
        name: 'Address', dataType: 'AsDetail', isArray: false,
        object: po([attr({ name: 'City', value: 'Brussels' })]),
      }),
      attr({
        name: 'Artists', dataType: 'AsDetail', isArray: true,
        objects: [
          po([attr({ name: 'ArtistId', dataType: 'Reference', value: 'Artists/40' })]),
          po([attr({ name: 'ArtistId', dataType: 'Reference', value: 'Artists/41' })]),
        ],
      }),
    ]));
    expect(result).toEqual({
      Address: { City: 'Brussels' },
      Artists: [{ ArtistId: 'Artists/40' }, { ArtistId: 'Artists/41' }],
    });
  });

  it('does NOT carry breadcrumbs (form-state path)', () => {
    const result = nestedPoToDict(po([
      attr({ name: 'ArtistId', dataType: 'Reference', value: 'Artists/40', breadcrumb: 'Logic' }),
    ]));
    expect(result).toEqual({ ArtistId: 'Artists/40' });
    expect(result[AS_DETAIL_BREADCRUMBS_KEY]).toBeUndefined();
  });
});

describe('nestedPoToDisplayRow', () => {
  it('returns {} for null / undefined', () => {
    expect(nestedPoToDisplayRow(null)).toEqual({});
    expect(nestedPoToDisplayRow(undefined)).toEqual({});
  });

  it('preserves the per-reference server breadcrumb under the reserved key', () => {
    const row = nestedPoToDisplayRow(po([
      attr({ name: 'ArtistId', dataType: 'Reference', value: 'Artists/40', breadcrumb: 'Logic' }),
    ]));
    expect(row['ArtistId']).toBe('Artists/40');
    expect(row[AS_DETAIL_BREADCRUMBS_KEY]).toEqual({ ArtistId: 'Logic' });
  });

  it('omits the side channel entirely when no reference has a server breadcrumb', () => {
    const row = nestedPoToDisplayRow(po([
      attr({ name: 'Title', value: 'Song' }),
      attr({ name: 'ArtistId', dataType: 'Reference', value: 'Artists/40' }),
    ]));
    expect(row).toEqual({ Title: 'Song', ArtistId: 'Artists/40' });
    expect(row[AS_DETAIL_BREADCRUMBS_KEY]).toBeUndefined();
  });

  it('ignores empty-string breadcrumbs and reference arrays', () => {
    const row = nestedPoToDisplayRow(po([
      attr({ name: 'Empty', dataType: 'Reference', value: 'X', breadcrumb: '' }),
      attr({ name: 'Many', dataType: 'Reference', isArray: true, value: ['A', 'B'], breadcrumbs: { A: 'Alpha', B: 'Beta' } }),
    ]));
    expect(row[AS_DETAIL_BREADCRUMBS_KEY]).toBeUndefined();
  });

  it('recurses into nested AsDetail rows, each carrying its own breadcrumb map', () => {
    const row = nestedPoToDisplayRow(po([
      attr({
        name: 'Artists', dataType: 'AsDetail', isArray: true,
        objects: [
          po([attr({ name: 'ArtistId', dataType: 'Reference', value: 'Artists/40', breadcrumb: 'Logic' })]),
        ],
      }),
    ]));
    expect(row['Artists'][0]['ArtistId']).toBe('Artists/40');
    expect(row['Artists'][0][AS_DETAIL_BREADCRUMBS_KEY]).toEqual({ ArtistId: 'Logic' });
  });
});

describe('dictToNestedPo', () => {
  const songArtistType: EntityType = {
    id: 'sa', name: 'SongArtist', clrType: 'Demo.SongArtist',
    attributes: [{ id: 'ArtistId', name: 'ArtistId', dataType: 'Reference', isRequired: false, isVisible: true, isReadOnly: false, order: 0, rules: [] } as any],
  } as EntityType;

  const songType: EntityType = {
    id: 'song', name: 'Song', clrType: 'Demo.Song',
    attributes: [
      { id: 'Title', name: 'Title', dataType: 'string', isRequired: false, isVisible: true, isReadOnly: false, order: 0, rules: [] } as any,
      { id: 'Artists', name: 'Artists', dataType: 'AsDetail', isArray: true, asDetailType: 'Demo.SongArtist', isRequired: false, isVisible: true, isReadOnly: false, order: 1, rules: [] } as any,
    ],
  } as EntityType;

  const resolve = (clr: string) => (clr === 'Demo.SongArtist' ? songArtistType : undefined);

  it('builds a nested PO from a flat dict, scaffolding array AsDetail children', () => {
    const result = dictToNestedPo({ Id: 'Songs/1', Title: 'Song', Artists: [{ ArtistId: 'Artists/40' }] }, songType, resolve);

    expect(result.id).toBe('Songs/1');
    const title = result.attributes.find(a => a.name === 'Title')!;
    expect(title.value).toBe('Song');

    const artists = result.attributes.find(a => a.name === 'Artists')!;
    expect(artists.value).toBeNull();
    expect(artists.objects).toHaveLength(1);
    expect(artists.objects![0].attributes.find(a => a.name === 'ArtistId')!.value).toBe('Artists/40');
  });

  it('round-trips with nestedPoToDict (form save then reload)', () => {
    const original = { Id: 'Songs/1', Title: 'Song', Artists: [{ ArtistId: 'Artists/40' }, { ArtistId: 'Artists/41' }] };
    const flatAgain = nestedPoToDict(dictToNestedPo(original, songType, resolve));
    expect(flatAgain).toEqual({ Title: 'Song', Artists: [{ ArtistId: 'Artists/40' }, { ArtistId: 'Artists/41' }] });
  });

  it('ignores the reserved breadcrumb key if present on the input dict', () => {
    const result = dictToNestedPo({ Title: 'Song', [AS_DETAIL_BREADCRUMBS_KEY]: { ArtistId: 'Logic' }, Artists: [] }, songType, resolve);
    expect(result.attributes.some(a => a.name === AS_DETAIL_BREADCRUMBS_KEY)).toBe(false);
  });
});

/**
 * The object's OWN breadcrumb, as opposed to the per-reference map above.
 *
 * A breadcrumb template can name a property the model does not carry — HR's `Address` renders
 * `{Crumb}`, and `Crumb` is `[Breadcrumb, IgnoreProperty]`. The server resolves it by reflecting
 * over the CLR property; the client structurally cannot. Discarding the resolved string while
 * flattening is what made the edit form show `(click to edit)` where the detail page showed the
 * address.
 */
describe('self breadcrumb', () => {
  const address = po(
    [attr({ name: 'Street', value: 'Abdijsteeg 30' }), attr({ name: 'City', value: 'Oudenaarde' })],
    { id: '', name: 'Address', breadcrumb: 'Abdijsteeg 30, 9700 Oudenaarde' },
  );

  it('nestedPoToDict carries it through the form flatten', () => {
    expect(nestedPoToDict(address)[AS_DETAIL_SELF_BREADCRUMB_KEY]).toBe('Abdijsteeg 30, 9700 Oudenaarde');
  });

  it('nestedPoToDisplayRow carries it through the display flatten', () => {
    expect(nestedPoToDisplayRow(address)[AS_DETAIL_SELF_BREADCRUMB_KEY]).toBe('Abdijsteeg 30, 9700 Oudenaarde');
  });

  /** Keeps the common case byte-for-byte identical to the plain flat dict. */
  it('is absent when the server sent none', () => {
    const bare = po([attr({ name: 'Street', value: 'Main' })], { breadcrumb: undefined });
    expect(AS_DETAIL_SELF_BREADCRUMB_KEY in nestedPoToDict(bare)).toBe(false);
  });

  /**
   * The whole reason a reserved key is safe on the FORM path: the dict is posted back on save, and
   * `dictToNestedPo` walks the entity type's attributes rather than the dict's keys.
   */
  it('never survives into the payload sent back to the server', () => {
    const addressType: EntityType = {
      id: 't', name: 'Address', clrType: 'HR.Entities.Address',
      attributes: [
        { id: 'a', name: 'Street', dataType: 'string', isVisible: true, isReadOnly: false, isRequired: false, isArray: false, order: 1 },
      ],
    } as any;

    const roundTripped = dictToNestedPo(nestedPoToDict(address), addressType, () => undefined);

    expect(roundTripped.attributes.some(a => a.name === AS_DETAIL_SELF_BREADCRUMB_KEY)).toBe(false);
    expect(JSON.stringify(roundTripped)).not.toContain(AS_DETAIL_SELF_BREADCRUMB_KEY);
  });

  describe('selfBreadcrumb', () => {
    const row = { [AS_DETAIL_SELF_BREADCRUMB_KEY]: 'Address' };

    /** EntityMapper.cs:209-211 substitutes the bare type name when the template renders blank. */
    it('filters the placeholder against a short model name', () => {
      expect(selfBreadcrumb(row, 'Address')).toBeNull();
    });

    /** An attribute's `asDetailType` is the full CLR name, so it must match on the last segment. */
    it('filters the placeholder against a full CLR name too', () => {
      expect(selfBreadcrumb(row, 'HR.Entities.Address')).toBeNull();
    });

    it('keeps a real breadcrumb that merely starts with the type name', () => {
      expect(selfBreadcrumb({ [AS_DETAIL_SELF_BREADCRUMB_KEY]: 'Address 12' }, 'Address')).toBe('Address 12');
    });

    it('returns null for a missing, blank, or non-string value', () => {
      expect(selfBreadcrumb({}, 'Address')).toBeNull();
      expect(selfBreadcrumb({ [AS_DETAIL_SELF_BREADCRUMB_KEY]: '   ' }, 'Address')).toBeNull();
      expect(selfBreadcrumb({ [AS_DETAIL_SELF_BREADCRUMB_KEY]: 42 }, 'Address')).toBeNull();
      expect(selfBreadcrumb(null)).toBeNull();
    });
  });
});
