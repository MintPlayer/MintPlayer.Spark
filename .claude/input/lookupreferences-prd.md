# LookupReferences Implementation PRD

## Overview

LookupReferences allow database entity properties (`int`, `string`, `Enum`, etc.) to be displayed as dropdowns in the web application. Unlike the existing `[Reference]` attribute (which references other entities), LookupReferences provide a fixed or dynamic set of key-value pairs where:
- **Key**: The value stored in RavenDB
- **Values**: Translated display text for the UI

## Goals

1. Implement static (transient) LookupReferences defined in code
2. Implement dynamic (persistent) LookupReferences stored in RavenDB
3. Expose API endpoints for CRUD operations on dynamic LookupReferences
4. Integrate with the Angular frontend to render dropdowns
5. Support multi-language translations via `TranslatedString`

---

## Backend Implementation

### 1. Core Types (MintPlayer.Spark.Abstractions)

#### TranslatedString.cs
```csharp
namespace MintPlayer.Spark.Abstractions;

public class TranslatedString
{
    public Dictionary<string, string> Translations { get; set; } = new();

    public string GetValue(string culture)
    {
        if (Translations.TryGetValue(culture, out var value))
            return value;

        // Fallback: try base culture (e.g., "en" from "en-US")
        var baseCulture = culture.Split('-')[0];
        if (Translations.TryGetValue(baseCulture, out value))
            return value;

        // Fallback: return first available or empty
        return Translations.Values.FirstOrDefault() ?? string.Empty;
    }

    public static TranslatedString Create(string en, string? fr = null, string? nl = null)
    {
        var ts = new TranslatedString();
        ts.Translations["en"] = en;
        if (fr != null) ts.Translations["fr"] = fr;
        if (nl != null) ts.Translations["nl"] = nl;
        return ts;
    }
}
```

#### LookupReferenceAttribute.cs
```csharp
namespace MintPlayer.Spark.Abstractions;

[AttributeUsage(AttributeTargets.Property)]
public sealed class LookupReferenceAttribute : Attribute
{
    public Type LookupType { get; }

    public LookupReferenceAttribute(Type lookupType)
    {
        LookupType = lookupType;
    }
}
```

#### TransientLookupReference.cs (Base for static lookups)
```csharp
namespace MintPlayer.Spark.Abstractions;

public abstract class TransientLookupReference
{
    public required string Key { get; init; }
    public string? Description { get; init; }
    public required TranslatedString Values { get; init; }

    /// <summary>
    /// Helper method to create TranslatedString inline
    /// </summary>
    protected static TranslatedString _TS(string en, string? fr = null, string? nl = null)
        => TranslatedString.Create(en, fr, nl);
}
```

#### DynamicLookupReference.cs (Base for database-stored lookups)
```csharp
namespace MintPlayer.Spark.Abstractions;

public class EmptyValue { }

public class LookupReferenceValue<TValue> where TValue : new()
{
    public required string Key { get; set; }
    public required TranslatedString Values { get; set; }
    public bool IsActive { get; set; } = true;
    public TValue Extra { get; set; } = new();
}

public abstract class DynamicLookupReference : DynamicLookupReference<EmptyValue>
{
}

public abstract class DynamicLookupReference<TValue> where TValue : new()
{
    public string? Id { get; set; }  // e.g., "LookupReferences/CarBrand"
    public required string Name { get; set; }
    public List<LookupReferenceValue<TValue>> Values { get; set; } = new();
}
```

### 2. LookupReference Discovery Service

Create a service to discover all LookupReference types in the application:

#### ILookupReferenceDiscoveryService.cs
```csharp
namespace MintPlayer.Spark.Abstractions.Services;

public interface ILookupReferenceDiscoveryService
{
    IReadOnlyCollection<LookupReferenceInfo> GetAllLookupReferences();
    LookupReferenceInfo? GetLookupReference(string name);
    bool IsTransient(string name);
    bool IsDynamic(string name);
}

public class LookupReferenceInfo
{
    public required string Name { get; init; }
    public required Type Type { get; init; }
    public required bool IsTransient { get; init; }
    public Type? ValueType { get; init; }  // For dynamic: the TValue type
}
```

#### LookupReferenceDiscoveryService.cs (in MintPlayer.Spark)
```csharp
namespace MintPlayer.Spark.Services;

public class LookupReferenceDiscoveryService : ILookupReferenceDiscoveryService
{
    private readonly Dictionary<string, LookupReferenceInfo> _lookupReferences = new();

    public LookupReferenceDiscoveryService(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            DiscoverLookupReferences(assembly);
        }
    }

    private void DiscoverLookupReferences(Assembly assembly)
    {
        var types = assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsClass);

        foreach (var type in types)
        {
            if (typeof(TransientLookupReference).IsAssignableFrom(type))
            {
                _lookupReferences[type.Name] = new LookupReferenceInfo
                {
                    Name = type.Name,
                    Type = type,
                    IsTransient = true
                };
            }
            else if (IsAssignableToGenericType(type, typeof(DynamicLookupReference<>)))
            {
                var valueType = GetGenericArgument(type, typeof(DynamicLookupReference<>));
                _lookupReferences[type.Name] = new LookupReferenceInfo
                {
                    Name = type.Name,
                    Type = type,
                    IsTransient = false,
                    ValueType = valueType
                };
            }
        }
    }

    public IReadOnlyCollection<LookupReferenceInfo> GetAllLookupReferences()
        => _lookupReferences.Values.ToList();

    public LookupReferenceInfo? GetLookupReference(string name)
        => _lookupReferences.GetValueOrDefault(name);

    public bool IsTransient(string name)
        => _lookupReferences.TryGetValue(name, out var info) && info.IsTransient;

    public bool IsDynamic(string name)
        => _lookupReferences.TryGetValue(name, out var info) && !info.IsTransient;

    // Helper methods for generic type checking...
}
```

### 3. LookupReference Data Service

#### ILookupReferenceService.cs
```csharp
namespace MintPlayer.Spark.Abstractions.Services;

public interface ILookupReferenceService
{
    Task<IEnumerable<LookupReferenceListItem>> GetAllAsync();
    Task<LookupReferenceDto?> GetAsync(string name);
    Task<LookupReferenceValueDto> AddValueAsync(string name, LookupReferenceValueDto value);
    Task<LookupReferenceValueDto> UpdateValueAsync(string name, string key, LookupReferenceValueDto value);
    Task DeleteValueAsync(string name, string key);
}

public class LookupReferenceListItem
{
    public required string Name { get; set; }
    public required bool IsTransient { get; set; }
    public int ValueCount { get; set; }
}

public class LookupReferenceDto
{
    public required string Name { get; set; }
    public required bool IsTransient { get; set; }
    public List<LookupReferenceValueDto> Values { get; set; } = new();
}

public class LookupReferenceValueDto
{
    public required string Key { get; set; }
    public required Dictionary<string, string> Translations { get; set; }
    public bool IsActive { get; set; } = true;
    public Dictionary<string, object>? Extra { get; set; }
}
```

### 4. Update EntityAttributeDefinition

Add support for LookupReference in the model definition:

```csharp
// In EntityAttributeDefinition.cs - add these properties:
public string? LookupReferenceType { get; set; }  // e.g., "CarStatus", "CarBrand"
```

### 5. Update ModelSynchronizer

Extend `ModelSynchronizer.cs` to detect `[LookupReference]` attributes and include them in the generated JSON model files.

---

## API Endpoints

Add these endpoints in `SparkMiddleware.cs`:

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/spark/lookupref` | List all LookupReferences |
| GET | `/spark/lookupref/{name}` | Get values for a specific LookupReference |
| POST | `/spark/lookupref/{name}` | Add value (dynamic only) |
| PUT | `/spark/lookupref/{name}/{key}` | Update value (dynamic only) |
| DELETE | `/spark/lookupref/{name}/{key}` | Delete value (dynamic only) |

### Response Examples

**GET /spark/lookupref**
```json
[
  { "name": "CarStatus", "isTransient": true, "valueCount": 3 },
  { "name": "CarBrand", "isTransient": false, "valueCount": 12 }
]
```

**GET /spark/lookupref/CarStatus**
```json
{
  "name": "CarStatus",
  "isTransient": true,
  "values": [
    {
      "key": "InUse",
      "translations": { "en": "In use", "fr": "En usage", "nl": "In gebruik" },
      "isActive": true,
      "extra": { "allowOnCarNotes": true }
    },
    {
      "key": "OnParking",
      "translations": { "en": "In parking lot", "fr": "Dans le parking", "nl": "Op parking" },
      "isActive": true,
      "extra": { "allowOnCarNotes": false }
    }
  ]
}
```

---

## RavenDB Storage

Dynamic LookupReferences are stored in a `LookupReferences` collection:

```json
{
  "Id": "LookupReferences/CarBrand",
  "Name": "CarBrand",
  "Values": [
    {
      "Key": "Toyota",
      "Values": {
        "Translations": { "en": "Toyota", "fr": "Toyota", "nl": "Toyota" }
      },
      "IsActive": true,
      "Extra": {
        "CountryOfOrigin": "Countries/Japan",
        "FoundedYear": 1937
      }
    }
  ]
}
```

---

## Frontend Implementation

### 1. TypeScript Models

#### lookup-reference.ts
```typescript
export interface LookupReferenceListItem {
  name: string;
  isTransient: boolean;
  valueCount: number;
}

export interface LookupReference {
  name: string;
  isTransient: boolean;
  values: LookupReferenceValue[];
}

export interface LookupReferenceValue {
  key: string;
  translations: Record<string, string>;
  isActive: boolean;
  extra?: Record<string, unknown>;
}
```

### 2. Update EntityAttributeDefinition

```typescript
// In entity-type.ts - add:
export interface EntityAttributeDefinition {
  // ... existing properties
  lookupReferenceType?: string;  // Name of the LookupReference
}
```

### 3. LookupReference Service

```typescript
@Injectable({ providedIn: 'root' })
export class LookupReferenceService {
  constructor(private http: HttpClient) {}

  getAll(): Observable<LookupReferenceListItem[]> {
    return this.http.get<LookupReferenceListItem[]>('/spark/lookupref');
  }

  get(name: string): Observable<LookupReference> {
    return this.http.get<LookupReference>(`/spark/lookupref/${name}`);
  }

  addValue(name: string, value: LookupReferenceValue): Observable<LookupReferenceValue> {
    return this.http.post<LookupReferenceValue>(`/spark/lookupref/${name}`, value);
  }

  updateValue(name: string, key: string, value: LookupReferenceValue): Observable<LookupReferenceValue> {
    return this.http.put<LookupReferenceValue>(`/spark/lookupref/${name}/${key}`, value);
  }

  deleteValue(name: string, key: string): Observable<void> {
    return this.http.delete<void>(`/spark/lookupref/${name}/${key}`);
  }
}
```

### 4. Update po-form.component.ts

Add dropdown rendering for LookupReference attributes:

```typescript
// Add to component class:
lookupReferenceCache = new Map<string, LookupReference>();

async loadLookupReference(name: string): Promise<LookupReference> {
  if (!this.lookupReferenceCache.has(name)) {
    const lr = await firstValueFrom(this.lookupReferenceService.get(name));
    this.lookupReferenceCache.set(name, lr);
  }
  return this.lookupReferenceCache.get(name)!;
}

getLookupDisplayValue(attr: PersistentObjectAttribute): string {
  const lr = this.lookupReferenceCache.get(attr.lookupReferenceType!);
  if (!lr) return attr.value?.toString() ?? '';

  const item = lr.values.find(v => v.key === attr.value);
  if (!item) return attr.value?.toString() ?? '';

  // Use current language or fallback
  return item.translations[this.currentLanguage] ??
         item.translations['en'] ??
         Object.values(item.translations)[0] ??
         attr.value?.toString() ?? '';
}
```

### 5. Update po-form.component.html

```html
@if (attr.lookupReferenceType) {
  <bs-select
    [items]="getLookupOptions(attr)"
    [(ngModel)]="attr.value"
    [itemLabelSelector]="getLookupLabel"
    [itemValueSelector]="getLookupValue">
  </bs-select>
} @else if (attr.dataType === 'Reference') {
  <!-- existing reference handling -->
}
```

---

## Implementation Steps

### Phase 1: Core Backend Types
1. [ ] Create `TranslatedString.cs` in Abstractions
2. [ ] Create `LookupReferenceAttribute.cs` in Abstractions
3. [ ] Create `TransientLookupReference.cs` in Abstractions
4. [ ] Create `DynamicLookupReference.cs` and related types in Abstractions

### Phase 2: Discovery & Service Layer
5. [ ] Create `ILookupReferenceDiscoveryService` interface
6. [ ] Implement `LookupReferenceDiscoveryService`
7. [ ] Create `ILookupReferenceService` interface and DTOs
8. [ ] Implement `LookupReferenceService` (handles both transient and dynamic)

### Phase 3: Model Integration
9. [ ] Update `EntityAttributeDefinition` to include `LookupReferenceType`
10. [ ] Update `ModelSynchronizer` to detect `[LookupReference]` attributes
11. [ ] Update JSON model generation to include lookupReferenceType

### Phase 4: API Endpoints
12. [ ] Add `GET /spark/lookupref` endpoint
13. [ ] Add `GET /spark/lookupref/{name}` endpoint
14. [ ] Add `POST /spark/lookupref/{name}` endpoint (dynamic only)
15. [ ] Add `PUT /spark/lookupref/{name}/{key}` endpoint (dynamic only)
16. [ ] Add `DELETE /spark/lookupref/{name}/{key}` endpoint (dynamic only)

### Phase 5: Frontend
17. [ ] Create TypeScript models for LookupReference
18. [ ] Create `LookupReferenceService` in Angular
19. [ ] Update `EntityAttributeDefinition` TypeScript interface
20. [ ] Update `po-form.component` to render dropdowns for LookupReference attributes
21. [ ] Add caching for LookupReference values

### Phase 6: Demo & Testing
22. [ ] Create example `CarStatus` (TransientLookupReference) in DemoApp
23. [ ] Create example `CarBrand` (DynamicLookupReference) in DemoApp
24. [ ] Add `Car` entity with both lookup types
25. [ ] Test CRUD operations on dynamic lookups
26. [ ] Test dropdown rendering in UI

---

## Files to Create/Modify

### New Files (Abstractions)
- `MintPlayer.Spark.Abstractions/TranslatedString.cs`
- `MintPlayer.Spark.Abstractions/LookupReferenceAttribute.cs`
- `MintPlayer.Spark.Abstractions/TransientLookupReference.cs`
- `MintPlayer.Spark.Abstractions/DynamicLookupReference.cs`

### New Files (Spark Library)
- `MintPlayer.Spark/Services/LookupReferenceDiscoveryService.cs`
- `MintPlayer.Spark/Services/LookupReferenceService.cs`

### New Files (Frontend)
- `ClientApp/src/app/core/models/lookup-reference.ts`
- `ClientApp/src/app/core/services/lookup-reference.service.ts`

### Modified Files
- `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs` - Add `LookupReferenceType`
- `MintPlayer.Spark/Services/ModelSynchronizer.cs` - Detect `[LookupReference]`
- `MintPlayer.Spark/SparkMiddleware.cs` - Add endpoints
- `ClientApp/src/app/core/models/entity-type.ts` - Add `lookupReferenceType`
- `ClientApp/src/app/components/po-form/po-form.component.ts` - Dropdown logic
- `ClientApp/src/app/components/po-form/po-form.component.html` - Dropdown template

### Demo Files
- `Demo/DemoApp/Data/Car.cs` - Example entity
- `Demo/DemoApp/LookupReferences/CarStatus.cs` - Transient example
- `Demo/DemoApp/LookupReferences/CarBrand.cs` - Dynamic example

---

## Differences from Existing Reference Attribute

| Aspect | Reference | LookupReference |
|--------|-----------|-----------------|
| Target | Another entity type | Key-value pairs |
| UI | Modal with search | Dropdown |
| Storage | Entity ID (string) | Any type (string, int, enum) |
| Values | Queried from DB | Static or from LookupReferences collection |
| Translations | Uses entity's breadcrumb | Uses TranslatedString |

---

## Open Questions

1. Should dynamic LookupReferences support soft-delete (IsActive=false) or hard delete?
   - **Recommendation**: Soft-delete by default to preserve referential integrity

2. Should we add validation to prevent deleting lookup values that are in use?
   - **Recommendation**: Yes, return error if value is referenced by existing entities

3. Should extra properties on TransientLookupReference be exposed via the API?
   - **Recommendation**: Yes, include them in the response for frontend flexibility
