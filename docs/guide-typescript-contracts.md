# TypeScript contracts from C# — what you write

Background: commerce PRD §5 X20 item 2 and K12 (`docs/commerce_PRD.md`). Polyglot was checked for this and
rejected there (it maps `long` to `bigint` while the wire sends numbers, and makes enums numbers and records
classes).

---

## The short version

A response record, request body or wire enum the Angular client reads is declared **once, in C#**. A build-time
tool writes its TypeScript into a committed `generated/contracts.ts`, and CI fails when that file differs from
what the tool would write. You never edit the generated file.

| You write | Where |
|---|---|
| `[SparkContract]` on the type | the C# type |
| `[assembly: SparkClientModule("My.Namespace", "<module>")]` once per namespace | the assembly that owns the namespace |
| the regenerate command, then commit the file | see below |

| Where the type lives | The TypeScript goes to | Regenerate with |
|---|---|---|
| a Spark library | `libs/node_packages/ng-spark/<entry>/src/generated/contracts.ts` | `dotnet run --project tools/SchemaGenerator -- contracts` |
| an application | `apps/<App>/<App>/ClientApp/src/app/generated/contracts.ts` | `dotnet run -- --spark-synchronize-contracts` (in the app's folder) |

`--verify` (library) and `--spark-verify-contracts` (application) write nothing and exit **3** when a file is out
of date; both run in the pull-request workflow. Exit **2** means a contract cannot be stated (see "Refused").

---

## An endpoint returns a typed record, never an anonymous object

```csharp
[SparkContract]
public sealed record EntityPermissions(bool CanQuery, bool CanRead /* … */)
{
    public static EntityPermissions None { get; } = new(false, false /* … */);
}

// in the endpoint
return Results.Json(new EntityPermissions(canQuery, canRead /* … */));
```

```ts
// generated: libs/node_packages/ng-spark/models/src/generated/contracts.ts
export interface EntityPermissions {
  canQuery: boolean;
  canRead: boolean;
  // …
}
```

An anonymous object (`Results.Ok(new { … })`) has no type to generate from, so every response the client reads
is a record. A static member (`None`) is not serialized and not generated.

### Claiming the namespace

```csharp
// a library: one ng-spark entry point per namespace
[assembly: SparkClientModule("MintPlayer.Spark.Authorization.Contracts", "@mintplayer/ng-spark/auth/models")]

// an application
[assembly: SparkClientModule("HR.Contracts", SparkClientModuleAttribute.Application)]
```

A namespace claims the namespaces below it too; the longest claim wins. A contract type, or a type one reaches,
in a namespace nobody claims is an **error**: a new namespace cannot silently fall out of the client.

The entry point re-exports its file: `export * from './generated/contracts';` in `<entry>/src/index.ts`. An
application imports from `./generated/contracts` (relative to `src/app`).

A library type an application's contract reaches (a `PersistentObject`, a `TranslatedString`) is **imported** from
the library's module, never re-declared:

```ts
import type { PersistentObject, TranslatedString } from '@mintplayer/ng-spark/models';
```

---

## What the TypeScript says: the wire, not the C#

The generator follows System.Text.Json's rules with ASP.NET Core's web defaults, which every Spark endpoint
serializes with.

| C# | TypeScript | Why |
|---|---|---|
| `string Name` | `name: string;` | camelCase naming policy |
| `[JsonPropertyName("x_y")]` | `x_y: …` | the attribute wins |
| `string? Note` | `note?: string \| null;` | the server writes the null; a client building a request may leave it out |
| `[JsonIgnore(Condition = WhenWritingNull)] string? Note` | `note?: string;` | the server leaves it out instead of writing null |
| `[JsonIgnore]` | (absent) | never on the wire |
| `int`, `long`, `decimal`, `double` | `number` | written as JSON numbers; `long` is **not** `bigint` |
| `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeSpan` | `string` | written as strings |
| `object`, `JsonElement` | `any` | no fixed shape |
| `List<T>`, `IReadOnlyList<T>`, `T[]` | `T[]` | `(T \| null)[]` when the element is nullable |
| `Dictionary<string, T>` | `Record<string, T>` | |
| an enum, no converter | `export enum Kind { A = 0, B = 1 }` | written as its number |
| an enum with `JsonStringEnumConverter` on the type | `export type Kind = 'a' \| 'B';` | written by name; `[JsonStringEnumMemberName]` is honoured |
| the converter on the member only | `'A' \| 'B'` inline | |
| a `[Flags]` enum written by name | `string` | combinations are comma-joined (`"Query, PersistentObject"`) |
| `class Derived : Base` (both contracts) | `interface Derived extends Base` | |
| `[SparkContract("AuthUser")] record SparkAuthUser` | `interface AuthUser` | for a name the client already uses |

The output has **no doc comments**: whether an XML documentation file sits next to an assembly would decide the
bytes, and CI compares bytes. Document the C# type; that is the source of truth.

---

## Refused

The generator stops (exit 2) rather than guess, listing every reason:

- a contract in an unclaimed namespace;
- a type written by a custom `JsonConverter`, because its properties need not be its wire shape. If the converter
  writes exactly the properties (as `PersistentObjectAttribute`'s does), mark the type `[SparkContract]` to say so.
  If it writes something else, write its TypeScript by hand and declare it:
  `[assembly: SparkClientType(typeof(TranslatedString), "TranslatedString", "translated-string")]` (the file is
  relative to the module's source folder);
- open generic types, `[JsonExtensionData]`, `[JsonPolymorphic]` / `[JsonDerivedType]` (the TypeScript would have
  to be a union), interfaces and abstract types without `[SparkContract]`.

---

## Hand-written mirrors

`PersistentObject` and `PersistentObjectAttribute` stay hand-written in `models/src/`, because the client relies
on narrower members than the wire states (a loaded object's `id` is a `string`; a client-built attribute leaves
`isArray` out). The generated types are still emitted, and each mirror carries a compile-time check:

```ts
export type PersistentObjectAttributeMirrorsContract =
  AssertNone<MissingMembers<Wire.PersistentObjectAttributeAsDetail, PersistentObjectAttribute>>;
```

A member added to the C# class fails ng-spark's build until the mirror carries it. That is the drift that once
left `options` off the attribute. `models/src/index.ts` exports the two mirrors explicitly, which shadows the
generated names in the entry point's `export *`.

---

## Now generated

| Module | Types | C# |
|---|---|---|
| `@mintplayer/ng-spark/models` | `EntityPermissions`, `ValidationRule`, `PersistentObjectPermissions`, `PersistentObjectAttributeOption`, `PersistentObjectAttributeAsDetail` (+ the two mirrored types) | `MintPlayer.Spark.Abstractions` |
| `@mintplayer/ng-spark/auth/models` | `AuthUser`, `SparkAuthCapabilities`, `SparkExternalProvider`, `SparkSignInIdentifier` | `MintPlayer.Spark.Authorization.Contracts` |

Two drifts the hand-written TypeScript had are gone: `PersistentObjectAttribute.options` was missing, and six of
the eleven always-sent `EntityPermissions` flags were marked optional.

A library that starts declaring contracts is added to `SparkLibraryContracts.Libraries` in `tools/SchemaGenerator`,
and the tool project references it.
