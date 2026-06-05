# PRD: Clickable Reference Links on Detail Pages

## 1. Overview

On the `po-detail` page, Reference attributes are currently displayed as plain text (the resolved breadcrumb/display value). When a reference appears inside an Array AsDetail grid (e.g., `CarreerJob.ProfessionId` inside the `Person.Jobs` table), the user sees "PHP Developer" or ".NET Developer" but cannot click through to that Profession's detail page.

This PRD proposes rendering Reference values as clickable `<a>` links that navigate to the referenced entity's detail page, both for:

1. **Array AsDetail columns** — Reference columns inside AsDetail grids (primary use case)
2. **Top-level Reference attributes** — Standalone Reference fields on the detail page (secondary, for consistency)

### Example

**Before (current):**
| Profession     | Contract Start | Contract End |
|----------------|---------------|--------------|
| .NET Developer | 2020-01-01    | 2023-12-31   |
| PHP Developer  | 2024-01-01    |              |

**After:**
| Profession                          | Contract Start | Contract End |
|-------------------------------------|---------------|--------------|
| [.NET Developer](/po/profession/…)  | 2020-01-01    | 2023-12-31   |
| [PHP Developer](/po/profession/…)   | 2024-01-01    |              |

## 2. Goals

1. Make Reference values inside Array AsDetail grids clickable links to the referenced entity's detail page
2. Make top-level Reference attribute values on the detail page clickable as well
3. Use standard Angular `routerLink` for SPA navigation (no full page reload)
4. Determine the correct route (`/po/:type/:id`) by resolving the referenced entity type's alias from the available metadata

## 3. Non-Goals

- Editing behavior is not affected — the edit page already uses dropdowns/modals for reference selection
- No tooltip/preview on hover (could be added later)
- No changes to the query-list view (references there are already clickable via row click)
- No backend changes needed — all required data is already available

## 4. Available Data

All the data needed to build the link is already present on the frontend:

### 4.1 For Array AsDetail Reference columns

The `po-detail` component already loads:

- **`allEntityTypes: EntityType[]`** — full list of entity types with `clrType` and `alias`
- **`asDetailTypes[attrName]: EntityType`** — the AsDetail entity type (e.g., `CarreerJob`)
- **`asDetailReferenceOptions[parentAttr][colName]: PersistentObject[]`** — resolved reference objects with `id`, `breadcrumb`, `name`
- **`EntityAttributeDefinition.referenceType`** — the CLR type of the referenced entity (e.g., `"HR.Entities.Profession"`)

To build the link we need:
- **Target route type**: Look up `referenceType` in `allEntityTypes` by `clrType` → get `alias || id`
- **Target route id**: The raw cell value (the reference ID, e.g., `"professions/3"`)
- **Display text**: Already resolved via `getAsDetailCellValue()` (e.g., `".NET Developer"`)

### 4.2 For top-level Reference attributes

- **`item.attributes[].breadcrumb`** — display text (already resolved by backend)
- **`item.attributes[].value`** — the reference ID
- **`entityType.attributes[].referenceType`** — CLR type of the referenced entity

## 5. Implementation Plan

### 5.1 Add a helper method to resolve reference route info

Add to `po-detail.component.ts`:

```typescript
/**
 * Returns the routerLink array for a reference, or null if the reference
 * type cannot be resolved.
 */
getReferenceLinkRoute(referenceClrType: string, referenceId: any): string[] | null {
  if (!referenceId || !referenceClrType) return null;
  const targetType = this.allEntityTypes.find(t => t.clrType === referenceClrType);
  if (!targetType) return null;
  return ['/po', targetType.alias || targetType.id, referenceId];
}
```

### 5.2 Update the template — Array AsDetail Reference cells

Change from plain text interpolation to conditional link rendering:

**Before:**
```html
@for (col of getAsDetailColumns(attr); track col.name) {
  <td>{{ getAsDetailCellValue(attr, row, col) }}</td>
}
```

**After:**
```html
@for (col of getAsDetailColumns(attr); track col.name) {
  <td>
    @if (col.dataType === 'Reference' && col.referenceType) {
      @let route = getReferenceLinkRoute(col.referenceType, row[col.name]);
      @if (route) {
        <a [routerLink]="route">{{ getAsDetailCellValue(attr, row, col) }}</a>
      } @else {
        {{ getAsDetailCellValue(attr, row, col) }}
      }
    } @else {
      {{ getAsDetailCellValue(attr, row, col) }}
    }
  </td>
}
```

### 5.3 Update the template — Top-level Reference attributes

Change from plain text to link for Reference attributes in the `@else` branch of the attribute renderer:

**Before:**
```html
} @else {
  {{ getAttributeValue(attr.name) || '-' }}
}
```

**After:**
```html
} @else if (attr.dataType === 'Reference' && attr.referenceType) {
  @let refRoute = getReferenceLinkRoute(attr.referenceType, getRawAttributeValue(attr.name));
  @if (refRoute && getAttributeValue(attr.name)) {
    <a [routerLink]="refRoute">{{ getAttributeValue(attr.name) }}</a>
  } @else {
    {{ getAttributeValue(attr.name) || '-' }}
  }
} @else {
  {{ getAttributeValue(attr.name) || '-' }}
}
```

### 5.4 Add `getRawAttributeValue` helper

For top-level Reference attributes, `getAttributeValue()` already returns the breadcrumb. We need the raw ID value for the route:

```typescript
getRawAttributeValue(attrName: string): any {
  return this.item?.attributes.find(a => a.name === attrName)?.value;
}
```

### 5.5 Apply to all three demo apps

The same changes apply to:
- `Demo/HR/HR/ClientApp/src/app/pages/po-detail/`
- `Demo/DemoApp/DemoApp/ClientApp/src/app/pages/po-detail/`
- `Demo/Fleet/Fleet/ClientApp/src/app/pages/po-detail/`

Each has minor differences (translation approach) but the template structure and component logic are identical for this feature.

## 6. Edge Cases

| Scenario | Behavior |
|----------|----------|
| Reference value is `null`/empty | Render `'-'` or empty — no link |
| Referenced entity type not found in `allEntityTypes` | Fall back to plain text (no link) |
| AsDetail entity type has no reference columns | No change — cells render as plain text as before |
| User clicks link to entity they don't have permission to view | Normal behavior — detail page shows error (existing handling) |
| Reference ID format includes slashes (e.g., `"professions/3"`) | Works as-is — Angular router handles this as a single segment via the `:id` param |

## 7. Styling

- Reference links should use the standard anchor styling (browser/Bootstrap default for `<a>` tags)
- No additional CSS classes needed — `routerLink` on `<a>` handles navigation
- The links should be visually distinguishable from plain text (underline, color) to indicate they're clickable

## 8. Testing

1. **HR Demo**: Navigate to a Person → verify `Jobs` table shows Profession names as clickable links → click through → lands on Profession detail page
2. **HR Demo**: Verify top-level `Company` reference on Person detail is clickable → navigates to Company detail
3. **DemoApp**: Test any entity with Reference attributes
4. **Null references**: Verify null/empty Reference values render as plain text without broken links
5. **Browser back button**: After clicking through a reference link, back button returns to the original detail page

## 9. Impact Assessment

- **Backend**: No changes required
- **Frontend**: Template + 2 small helper methods per demo app
- **Risk**: Low — purely additive UI change, no existing behavior modified
- **Effort**: Small — all data is already available, just needs to be wired up in templates
