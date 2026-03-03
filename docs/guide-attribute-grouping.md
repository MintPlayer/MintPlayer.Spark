# Attribute Grouping with Tabs and Groups

Spark supports a two-level attribute grouping hierarchy: **Tabs > Groups**. Tabs appear as horizontal tab navigation across the top of the detail and edit views. Each tab contains one or more groups, and each group contains related attributes rendered inside a card with an optional header. This keeps complex entity types with many attributes organized and navigable.

## Overview

The grouping hierarchy is:

```
EntityTypeDefinition
  +-- tabs[]          (top-level navigation)
  +-- groups[]        (cards within a tab)
  +-- attributes[]    (each attribute references a group via group ID)
```

- **Tabs** define the horizontal tab pages. Each tab has an `id`, `name`, `label`, and `order`.
- **Groups** define visual card sections within a tab. Each group references its parent tab via the `tab` field and has its own `id`, `name`, `label`, and `order`.
- **Attributes** reference a group via the `group` field (a GUID pointing to a group's `id`).

Attributes that don't reference any group, and groups that don't reference any tab, are placed on a default "General" tab automatically.

## Step 1: Define Tabs in the Model JSON

Add a `tabs` array to your entity's model JSON file. Each tab needs a unique `id` (GUID), a `name`, an optional `label` (translated string), and an `order` for sorting.

From `Demo/HR/HR/App_Data/Model/Person.json`:

```json
{
  "tabs": [
    {
      "id": "a1b2c3d4-0001-0001-0001-000000000001",
      "name": "General",
      "label": {
        "en": "General",
        "fr": "Général",
        "nl": "Algemeen"
      },
      "order": 1
    },
    {
      "id": "a1b2c3d4-0001-0001-0001-000000000002",
      "name": "Employment",
      "label": {
        "en": "Employment",
        "fr": "Emploi",
        "nl": "Loopbaan"
      },
      "order": 2
    }
  ]
}
```

## Step 2: Define Groups in the Model JSON

Add a `groups` array. Each group references its parent tab by the tab's `id` via the `tab` field. Groups without a `tab` value are placed on the default tab.

```json
{
  "groups": [
    {
      "id": "b1b2c3d4-0002-0002-0002-000000000001",
      "name": "Personal",
      "label": {
        "en": "Personal Information",
        "fr": "Informations personnelles",
        "nl": "Persoonlijke gegevens"
      },
      "tab": "a1b2c3d4-0001-0001-0001-000000000001",
      "order": 1
    },
    {
      "id": "b1b2c3d4-0002-0002-0002-000000000002",
      "name": "Contact",
      "label": {
        "en": "Contact",
        "fr": "Contact",
        "nl": "Contact"
      },
      "tab": "a1b2c3d4-0001-0001-0001-000000000001",
      "order": 2
    },
    {
      "id": "b1b2c3d4-0002-0002-0002-000000000003",
      "name": "Career",
      "label": {
        "en": "Career",
        "fr": "Carrière",
        "nl": "Carrière"
      },
      "tab": "a1b2c3d4-0001-0001-0001-000000000002",
      "order": 1
    }
  ]
}
```

In this example, the "General" tab contains two groups (Personal Information, Contact), and the "Employment" tab contains one group (Career).

## Step 3: Assign Attributes to Groups

On each attribute, set the `group` field to the GUID of the group it belongs to:

```json
{
  "attributes": [
    {
      "id": "bf869c13-0807-4fe1-b82a-777ba92ce9ff",
      "name": "FirstName",
      "group": "b1b2c3d4-0002-0002-0002-000000000001",
      "label": { "en": "First Name", "fr": "Prénom", "nl": "Voornaam" },
      "dataType": "string",
      "isRequired": true,
      "order": 1,
      "showedOn": "PersistentObject"
    },
    {
      "id": "1211e36c-664f-45ec-9fb3-17dfda75e21a",
      "name": "Email",
      "group": "b1b2c3d4-0002-0002-0002-000000000002",
      "label": { "en": "Email", "fr": "E-mail", "nl": "E-mail" },
      "dataType": "string",
      "order": 3,
      "showedOn": "Query, PersistentObject"
    },
    {
      "id": "6364e69b-615c-4f2d-8a9b-76aaccc97254",
      "name": "Company",
      "group": "b1b2c3d4-0002-0002-0002-000000000003",
      "label": { "en": "Company", "fr": "Entreprise", "nl": "Bedrijf" },
      "dataType": "Reference",
      "referenceType": "HR.Entities.Company",
      "query": "GetCompanies",
      "order": 5,
      "showedOn": "PersistentObject"
    }
  ]
}
```

Attributes without a `group` value (or with a `group` that doesn't match any defined group ID) are treated as ungrouped and placed on the default tab.

## C# Data Model

The grouping structures are defined in `MintPlayer.Spark.Abstractions`:

```csharp
public sealed class AttributeTab
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Label { get; set; }
    public int Order { get; set; }
    /// <summary>Number of columns for the grid layout within this tab.</summary>
    public int? ColumnCount { get; set; }
}

public sealed class AttributeGroup
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public TranslatedString? Label { get; set; }
    /// <summary>References an AttributeTab.Id. Null = default tab.</summary>
    public Guid? Tab { get; set; }
    public int Order { get; set; }
}
```

On `EntityTypeDefinition`:

```csharp
public AttributeTab[] Tabs { get; set; } = [];
public AttributeGroup[] Groups { get; set; } = [];
```

On `EntityAttributeDefinition`:

```csharp
/// <summary>References an AttributeGroup.Id. Null = ungrouped.</summary>
public Guid? Group { get; set; }

/// <summary>Number of grid columns this attribute spans within a tab's column layout.</summary>
public int? ColumnSpan { get; set; }
```

## Angular Data Model

The TypeScript interfaces mirror the C# model:

```typescript
export interface AttributeTab {
  id: string;
  name: string;
  label?: TranslatedString;
  order: number;
  columnCount?: number;
}

export interface AttributeGroup {
  id: string;
  name: string;
  label?: TranslatedString;
  tab?: string;       // references AttributeTab.id
  order: number;
}

export interface EntityType {
  // ...
  tabs?: AttributeTab[];
  groups?: AttributeGroup[];
  attributes: EntityAttributeDefinition[];
}

export interface EntityAttributeDefinition {
  // ...
  group?: string;      // references AttributeGroup.id
  columnSpan?: number;
}
```

## How the UI Renders Grouping

The `spark-po-detail` and `spark-po-form` components both use `bs-tab-control` from `@mintplayer/ng-bootstrap` to render tabs:

```html
<bs-tab-control>
  @for (tab of resolvedTabs(); track tab.id) {
    <bs-tab-page>
      <ng-template bsTabPageHeader>{{ tab.label | resolveTranslation:tab.name }}</ng-template>
      <!-- tab content with groups -->
    </bs-tab-page>
  }
</bs-tab-control>
```

Within each tab, groups are rendered as `bs-card` components with optional headers:

```html
@for (group of groupsForTab(tab); track group.id) {
  @if (attrsForGroup(group); as groupAttrs) {
    @if (groupAttrs.length > 0) {
      <bs-card class="d-block m-3">
        @if (group.label) {
          <bs-card-header>{{ group.label | resolveTranslation:group.name }}</bs-card-header>
        }
        <div class="p-3">
          @for (attr of groupAttrs; track attr.id) {
            <!-- render attribute -->
          }
        </div>
      </bs-card>
    }
  }
}
```

### Tab Resolution Logic

The components use a `resolvedTabs` computed signal that handles three scenarios:

1. **Entity has defined tabs**: Those tabs are used, sorted by `order`.
2. **Entity has ungrouped attributes or untabbed groups**: A default "General" tab (with `id: '__default__'`) is prepended before any defined tabs.
3. **Entity has no tabs at all**: Only the default tab is shown (backward compatible flat rendering).

```typescript
private static readonly DEFAULT_TAB: AttributeTab = {
  id: '__default__', name: 'Algemeen',
  label: { nl: 'Algemeen', en: 'General' }, order: 0
};

resolvedTabs = computed((): AttributeTab[] => {
  const et = this.entityType();
  const definedTabs = et?.tabs?.length
    ? [...et.tabs].sort((a, b) => a.order - b.order) : [];
  const hasUngroupedAttrs = this.ungroupedAttributes().length > 0;
  const hasUntabbedGroups = (et?.groups || []).some(g => !g.tab);

  if (hasUngroupedAttrs || hasUntabbedGroups || definedTabs.length === 0) {
    return [SparkPoFormComponent.DEFAULT_TAB, ...definedTabs];
  }
  return definedTabs;
});
```

### Group-to-Tab Mapping

Groups are filtered by their `tab` reference. Groups without a `tab` are placed on the default tab:

```typescript
groupsForTab(tab: AttributeTab): AttributeGroup[] {
  const groups = this.entityType()?.groups || [];
  if (tab.id === '__default__') {
    return groups.filter(g => !g.tab).sort((a, b) => a.order - b.order);
  }
  return groups.filter(g => g.tab === tab.id).sort((a, b) => a.order - b.order);
}
```

### Attribute-to-Group Mapping

Attributes are filtered by their `group` reference:

```typescript
attrsForGroup(group: AttributeGroup): EntityAttributeDefinition[] {
  return this.editableAttributes().filter(a => a.group === group.id);
}
```

## Backward Compatibility

Entities that don't define any `tabs` or `groups` continue to work exactly as before. All attributes are rendered as a flat list on a single default "General" tab within a single card. No changes to existing model JSON files are required.

The backward-compatible behavior activates when:
- `tabs` is empty or absent
- `groups` is empty or absent
- No attributes have a `group` value

## Complete Example

See the HR demo app for a working example:
- `Demo/HR/HR/App_Data/Model/Person.json` -- model JSON with tabs, groups, and group-assigned attributes
- `node_packages/ng-spark/src/lib/components/po-form/spark-po-form.component.html` -- form template with tab/group rendering
- `node_packages/ng-spark/src/lib/components/po-detail/spark-po-detail.component.html` -- detail template with tab/group rendering
- `node_packages/ng-spark/src/lib/models/entity-type.ts` -- TypeScript interfaces for `AttributeTab`, `AttributeGroup`
- `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs` -- C# classes for `AttributeTab`, `AttributeGroup`
