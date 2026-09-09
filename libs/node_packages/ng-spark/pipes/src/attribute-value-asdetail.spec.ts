import { describe, expect, it } from 'vitest';
import { AttributeValuePipe } from './attribute-value.pipe';
import { EntityType, PersistentObject } from '@mintplayer/ng-spark/models';

/**
 * How an `AsDetail` cell is summarised in a grid.
 *
 * Observed against Fleet at `/po/company/Companies%2F1d509…`: the Address column rendered the
 * literal text `(object)` while the payload carried
 * `attributes[Address].object.breadcrumb === "Voorbeeldstraat 1, 1000 Brussel"`.
 *
 * The cause is structural rather than incidental. HR declares
 * `[Breadcrumb, IgnoreProperty] public string Crumb => $"{Street}, {PostalCode} {City}"`, so the
 * Address type's breadcrumb template is `"{Crumb}"` — and `[IgnoreProperty]` keeps `Crumb` out of
 * the model, so no attribute by that name is ever projected. Resolving that template client-side
 * cannot succeed for any row, ever. The server resolves it against the real entity and sends the
 * answer; this pipe was recomputing it and discarding the result.
 */

const addressType: EntityType = {
  id: 't-address',
  name: 'Address',
  alias: 'address',
  clrType: 'HR.Entities.Address',
  breadcrumb: '{Crumb}',
  attributes: [],
} as any;

const personType: EntityType = {
  id: 't-person',
  name: 'Person',
  alias: 'person',
  clrType: 'HR.Entities.Person',
  attributes: [
    { id: 'a-addr', name: 'Address', dataType: 'AsDetail', asDetailType: 'HR.Entities.Address', isVisible: true, order: 1 } as any,
  ],
} as any;

/** The real payload shape, trimmed: nested scalars present, `Crumb` absent by design. */
function personWithAddress(breadcrumb: string | null, values: Record<string, string | null>): PersistentObject {
  return {
    id: 'People/1',
    objectTypeId: 't-person',
    attributes: [
      {
        name: 'Address',
        dataType: 'AsDetail',
        isArray: false,
        value: null,
        breadcrumb: null,
        object: {
          id: null,
          name: breadcrumb,
          objectTypeId: 't-address',
          breadcrumb,
          attributes: Object.entries(values).map(([name, value]) => ({ name, value, dataType: 'string' })),
        },
      } as any,
    ],
  } as any;
}

const run = (item: PersistentObject) =>
  new AttributeValuePipe().transform('Address', item, personType, {}, [personType, addressType]);

describe('AttributeValuePipe — AsDetail summary', () => {
  it('uses the breadcrumb the server resolved', () => {
    const item = personWithAddress('Voorbeeldstraat 1, 1000 Brussel', {
      Street: 'Voorbeeldstraat 1', PostalCode: '1000', City: 'Brussel',
    });

    expect(run(item)).toBe('Voorbeeldstraat 1, 1000 Brussel');
  });

  /**
   * The regression itself. Before the fix this returned "(object)": the `{Crumb}` template found
   * no `Crumb` attribute, `applyFieldTemplate` yielded an empty string, and the placeholder won.
   */
  it('does not fall back to a placeholder when the template names a property the model omits', () => {
    const item = personWithAddress('Voorbeeldstraat 1, 1000 Brussel', {
      Street: 'Voorbeeldstraat 1', PostalCode: '1000', City: 'Brussel',
    });

    expect(run(item)).not.toBe('(object)');
    expect(run(item)).not.toContain('object');
  });

  /**
   * A row whose address was never filled in. The server sends the template's honest output, and
   * showing it is right: the cell reflects the data rather than hiding it behind a placeholder.
   */
  it('passes through a sparse breadcrumb rather than substituting a placeholder', () => {
    const item = personWithAddress(',  ', { Street: null, PostalCode: null, City: null });

    expect(run(item)).toBe(',  ');
  });

  it('joins the scalar values when the server sent no breadcrumb at all', () => {
    const item = personWithAddress(null, { Street: 'Voorbeeldstraat 1', PostalCode: '1000', City: 'Brussel' });

    expect(run(item)).toBe('Voorbeeldstraat 1, 1000, Brussel');
  });

  it('renders an empty cell for an empty object, not a placeholder', () => {
    const item = personWithAddress(null, { Street: null, PostalCode: null, City: null });

    expect(run(item)).toBe('');
  });

  /**
   * #384 — the server's OTHER placeholder. `EntityMapper` substitutes the CLR type name when a
   * template renders blank, and this pipe printed it verbatim: a `Build` whose `Feedback.State`
   * was unset showed the literal "BuildFeedback" in the cell, which reads as a real value.
   *
   * `selfBreadcrumb` has filtered exactly this since it was written, but only for the two pipes
   * that flatten a row into a dict first. This one reads `attr.object.breadcrumb` off the wire and
   * so was never covered.
   */
  it('does not print the CLR type name the server substitutes for a blank template', () => {
    const item = personWithAddress('Address', { Street: null, PostalCode: null, City: null });

    expect(run(item)).toBe('');
  });

  it('filters the type name even when scalars remain, falling through to them', () => {
    // The placeholder is not data, so the joined scalars are strictly more use than "Address".
    const item = personWithAddress('Address', { Street: 'Voorbeeldstraat 1', PostalCode: '1000', City: null });

    expect(run(item)).toBe('Voorbeeldstraat 1, 1000');
  });

  /**
   * The scalar join enumerates the flattened dict, and `nestedPoToDict` stashes the row key and
   * the row's own breadcrumb in it. Latent until the filter above made this branch reachable while
   * a breadcrumb was present: before that, the only way here was the server sending none, which is
   * exactly when there is nothing stashed to leak.
   */
  it('does not join the reserved row key or breadcrumb into the fallback text', () => {
    const item = personWithAddress('Address', { Street: 'Voorbeeldstraat 1', PostalCode: null, City: null });
    (item.attributes[0] as any).object.id = 'a1b2c3d4e5f6';

    expect(run(item)).toBe('Voorbeeldstraat 1');
  });

  it('keeps a real breadcrumb that merely resembles the type name', () => {
    // Guard on the filter being too eager: only an exact match on the short name is a placeholder.
    const item = personWithAddress('Address 12', { Street: 'Address 12', PostalCode: null, City: null });

    expect(run(item)).toBe('Address 12');
  });
});
