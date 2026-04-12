# PRD: Contextual Lookup References

## Problem

Spark's current attribute types don't support **context-dependent dropdowns** — where a field's options depend on data from the same entity or a parent entity.

**Example:** In the WebhooksDemo, `EventColumnMapping` has a `TargetColumnOptionId` field that should show a dropdown of project board columns (Todo, In Progress, Done). These columns come from the sibling `Columns[]` AsDetail array on the parent `GitHubProject`. Currently it renders as a plain text field because:

- **LookupReference** — global values, not per-parent
- **TransientLookupReference** — static code-defined list, not per-parent
- **Reference** — requires a root entity with its own DB collection

None support options derived from the current entity's context.

## Solution: `[ContextualLookupReference]` Attribute

A new declarative attribute that tells the framework: "populate this dropdown from a sibling property on the parent entity."

```csharp
public class EventColumnMapping
{
    [LookupReference(typeof(WebhookEventType))]
    public string? WebhookEvent { get; set; }

    [ContextualLookupReference("Columns", "OptionId", "Name")]
    public string? TargetColumnOptionId { get; set; }

    public bool MoveLinkedIssues { get; set; }
}
```

Parameters:
- `"Columns"` — the source property on the parent entity providing the options
- `"OptionId"` — the key property on each source item (stored as the field value)
- `"Name"` — the display property on each source item (shown in the dropdown)

## Design: Frontend-First Resolution

The parent form data is already loaded in the Angular client. The backend only needs to provide **metadata** (which property, which key, which display); the frontend resolves options locally from `formData()` with no additional HTTP call.

This is simpler, faster, and reactive — when the user edits the source array (adds/removes columns), the dropdown updates immediately.

---

## Implementation

### 1. New C# Attribute

**File:** `MintPlayer.Spark.Abstractions/ContextualLookupReferenceAttribute.cs`

```csharp
[AttributeUsage(AttributeTargets.Property)]
public sealed class ContextualLookupReferenceAttribute : Attribute
{
    public string SourceProperty { get; }
    public string KeyProperty { get; }
    public string DisplayProperty { get; }

    public ContextualLookupReferenceAttribute(
        string sourceProperty, string keyProperty, string displayProperty)
    {
        SourceProperty = sourceProperty;
        KeyProperty = keyProperty;
        DisplayProperty = displayProperty;
    }
}
```

### 2. Model Definition Changes

**File:** `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs`

Add to `EntityAttributeDefinition`:

```csharp
public string? ContextualLookupSource { get; set; }
public string? ContextualLookupKeyProperty { get; set; }
public string? ContextualLookupDisplayProperty { get; set; }
```

### 3. Model Synchronizer Changes

**File:** `MintPlayer.Spark/Services/ModelSynchronizer.cs`

Detect `ContextualLookupReferenceAttribute` on properties and populate the three new fields during model sync.

### 4. Generated JSON Model

After synchronization, `EventColumnMapping.json`:

```json
{
  "name": "TargetColumnOptionId",
  "dataType": "string",
  "contextualLookupSource": "Columns",
  "contextualLookupKeyProperty": "OptionId",
  "contextualLookupDisplayProperty": "Name"
}
```

### 5. Frontend Model Changes

**File:** `node_packages/ng-spark/src/lib/models/entity-type.ts`

Add to `EntityAttributeDefinition`:

```typescript
contextualLookupSource?: string;
contextualLookupKeyProperty?: string;
contextualLookupDisplayProperty?: string;
```

### 6. Frontend po-form Changes

**File:** `node_packages/ng-spark/src/lib/components/po-form/spark-po-form.component.ts`

Add helper method:

```typescript
getContextualLookupOptions(
  col: EntityAttributeDefinition,
  parentFormData: Record<string, any>
): { key: string; display: string }[] {
  if (!col.contextualLookupSource) return [];
  const sourceArray = parentFormData[col.contextualLookupSource];
  if (!Array.isArray(sourceArray)) return [];
  return sourceArray
    .map(item => ({
      key: item[col.contextualLookupKeyProperty!],
      display: item[col.contextualLookupDisplayProperty!]
    }))
    .filter(o => o.key != null);
}
```

Add `parentFormData` input for modal AsDetail editing:

```typescript
parentFormData = input<Record<string, any> | null>(null);
```

**File:** `node_packages/ng-spark/src/lib/components/po-form/spark-po-form.component.html`

In the AsDetail inline editing loop, add between `Reference` check and `else`:

```html
} @else if (col.contextualLookupSource) {
  <bs-select
    [(ngModel)]="row[col.name]"
    (ngModelChange)="onFieldChange()">
    <option [ngValue]="null">{{ 'selectPlaceholder' | t }}</option>
    @for (option of getContextualLookupOptions(col, formData()); track option.key) {
      <option [ngValue]="option.key">{{ option.display }}</option>
    }
  </bs-select>
```

### 7. Also Fix: AsDetail Inline LookupReference (Existing Gap)

The inline AsDetail editor currently doesn't render `lookupReferenceType` attributes as dropdowns (they fall through to plain `<input>`). Fix by:

1. Loading lookup references for AsDetail column definitions in `loadLookupReferenceOptions()`
2. Adding a `lookupReferenceType` template branch in the inline AsDetail loop

---

## WebhooksDemo Changes

**File:** `Demo/WebhooksDemo/WebhooksDemo.Library/Entities/EventColumnMapping.cs`

```csharp
[ContextualLookupReference("Columns", "OptionId", "Name")]
public string? TargetColumnOptionId { get; set; }
```

**File:** `Demo/WebhooksDemo/WebhooksDemo/App_Data/Model/GitHubProject.json`

Set `editMode: "inline"` on the `EventMappings` attribute for inline table editing.

---

## Key Files

| File | Change |
|------|--------|
| `MintPlayer.Spark.Abstractions/ContextualLookupReferenceAttribute.cs` | New attribute class |
| `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs` | Add 3 properties to `EntityAttributeDefinition` |
| `MintPlayer.Spark/Services/ModelSynchronizer.cs` | Detect attribute, populate model fields |
| `node_packages/ng-spark/src/lib/models/entity-type.ts` | Add 3 TypeScript properties |
| `node_packages/ng-spark/src/lib/components/po-form/spark-po-form.component.ts` | Add helper methods + parentFormData input |
| `node_packages/ng-spark/src/lib/components/po-form/spark-po-form.component.html` | Add template branches for contextual + fix lookupRef in AsDetail |
| `Demo/WebhooksDemo/WebhooksDemo.Library/Entities/EventColumnMapping.cs` | Apply attribute |

## Verification

1. Run `dotnet run --spark-synchronize-model` — verify `contextualLookupSource` appears in JSON
2. Start the app, navigate to a GitHubProject detail page, click Edit
3. The `EventMappings` inline table should show:
   - **Webhook Event** — dropdown from WebhookEventType TransientLookupReference
   - **Target Column Option Id** — dropdown with "Todo", "In Progress", "Done" from parent's Columns
   - **Move Linked Issues** — toggle/checkbox
4. Add/remove a column in the Columns table → the Target Column dropdown updates immediately
