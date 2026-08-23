import { describe, expect, it } from 'vitest';
import { AttributeValuePipe } from './attribute-value.pipe';
import { EntityType, PersistentObject } from '@mintplayer/ng-spark/models';

/**
 * How an `AsDetail` cell is summarised in a grid.
 *
 * Observed against Fleet at `/po/company/Companies%2F1d509…`: the Address column rendered the
 * literal text `(object)` while the payload carried
 * `attributes[Address].object.breadcrumb === "Deinzestraat 231, 9700 Oudenaarde"`.
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
    const item = personWithAddress('Deinzestraat 231, 9700 Oudenaarde', {
      Street: 'Deinzestraat 231', PostalCode: '9700', City: 'Oudenaarde',
    });

    expect(run(item)).toBe('Deinzestraat 231, 9700 Oudenaarde');
  });

  /**
   * The regression itself. Before the fix this returned "(object)": the `{Crumb}` template found
   * no `Crumb` attribute, `applyFieldTemplate` yielded an empty string, and the placeholder won.
   */
  it('does not fall back to a placeholder when the template names a property the model omits', () => {
    const item = personWithAddress('Deinzestraat 231, 9700 Oudenaarde', {
      Street: 'Deinzestraat 231', PostalCode: '9700', City: 'Oudenaarde',
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
    const item = personWithAddress(null, { Street: 'Deinzestraat 231', PostalCode: '9700', City: 'Oudenaarde' });

    expect(run(item)).toBe('Deinzestraat 231, 9700, Oudenaarde');
  });

  it('renders an empty cell for an empty object, not a placeholder', () => {
    const item = personWithAddress(null, { Street: null, PostalCode: null, City: null });

    expect(run(item)).toBe('');
  });
});
