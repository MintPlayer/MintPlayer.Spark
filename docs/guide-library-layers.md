# Library layers: what a package ships, and what your application overrides

A Spark library can ship its own `App_Data` files: a model type, actions, texts, rights, menu
entries, moderation defaults. The application never copies them. It references the library, and its
own `App_Data` files compose **on top**, per key. There is one engine for every kind, in the runtime
and in the generators alike (`libs/spark/Shared/Layering/`), so what the build checks is what startup
composes.

`MintPlayer.Spark.Authorization` ships `SparkUser` and the whole passkeys page this way (model,
actions, rights, translations); `MintPlayer.Spark.Moderation` ships its reputation table, privileges
and the rights on its `Moderation` pseudo-type; the core library ships New, Edit and Delete and the
`common.*` texts.

---

## Seeing the composed result: `--spark-describe`

```
dotnet run -- --spark-describe <kind> [name] [--layers]
```

| Kind | Name narrows to |
|---|---|
| `actions` | an action |
| `model` | a type (`SparkUser`) |
| `translations` | a key prefix (`auth.passkey`) |
| `security` (or `rights`) | a right's key or resource |
| `programUnits` | a program unit or group id |
| `moderation` | a section (`Privileges`) |

Without `--layers` it prints the composed JSON. Rights print as a `key | resource | group | effect`
table, resolved the way the evaluator reads them, with the inert rights and any problems. With
`--layers` it prints one line per value, `path = value @layer`, where a library is named by its alias
and your file is `app`. The header names the layers in order:

```
actions, composed from: spark (MintPlayer.Spark) → app (App_Data/actions.json)
```

It runs before the host is built, opens no database, and needs no new line in `Program.cs`:
`SynchronizeSparkModelsIfRequested` handles it next to the model verbs. At startup `UseSpark` logs
each library layer once, for example `Spark layers: authorization (MintPlayer.Spark.Authorization)
ships model, translations.`

---

## The engine: one set of rules

- **Layer order is dependency order.** The core (`spark`) comes first, then each library above the
  libraries it was compiled against, by assembly name between unrelated ones, then the application.
- **Objects merge per property.** A property your file states replaces the inherited one. One it
  leaves out keeps it.
- **Arrays of identified elements merge per element key**: a model attribute by `name`, a right by
  `key`, a program unit by `id`. Arrays of primitives are replaced whole.
- **`null` resets a property** to the layer below, or removes an action, a translation key or a
  moderation privilege. A keyed element is removed with `{ "<key>": "…", "$remove": true }`, because
  a bare `null` carries no key.
- **The application always wins, silently.** So does a library over a library it depends on.
- **Two unrelated libraries that state one value differently** get a diagnostic at build time
  (SPARK036 for actions, SPARK_TRANS_005 for translations) and a warning at startup. The later one
  wins. Your file decides by stating the value.
- **Strict JSON in every layer.** No comments, no trailing commas and no duplicate keys.
  `_`-prefixed keys (`"_comment"`) and `$schema` are annotations, ignored by the engine.
- **Library layers are compiled in**, so they change only with a rebuild. The application's files
  reload on save (below).

### Per kind

| Kind | Keyed by | Rules of its own |
|---|---|---|
| `actions.json` | action name, ignoring case | an action's properties are replaced whole, object-valued ones too; `"Edit": null` removes an action. See [custom actions](guide-custom-actions.md#layers-the-libraries-actionsjson-and-yours-467-d7) |
| `Model/*.json` | `persistentObject.name`; `attributes`, `tabs`, `groups` and the file's root `queries` by `name` | **an application file naming a library type is a delta**; an `id` can never be changed by a later layer; the sub-query list `persistentObject.queries` is replaced whole |
| `translations.json` | dotted key, then language (ordinal) | each layer is flattened first, so `"a.b"` and `{ "a": { "b": … } }` meet; `"ns": null` removes a whole namespace; a language cannot be removed; your `""` means "not translated yet" and is ignored. See [translated strings](guide-translated-strings.md) |
| `security.json` | each right's `key` | guard rails, tokens, bindings and the opt-out, [below](#rights) |
| `programUnits.json` | groups and their units by `id`, ignoring case | `{ "id": "…", "$remove": true }` removes a group or unit |
| `moderation.json` | every object per property, ignoring case | arrays replaced whole; the result is the lowest-precedence `IConfiguration` source, so appsettings and environment variables still override it. See [moderation](guide-moderation.md) |
| `culture.json` | — | application only; a library cannot ship one |

Not layered: `modelHashes.json` and `securityPosture.txt` (gate outputs) and `oidc-signing-key.json`
(runtime state).

### Model deltas

An application that wants a library type shown differently writes a file naming that type and
states only what differs. QnA's whole `SparkUser.json`:

```json
{
  "persistentObject": {
    "name": "SparkUser",
    "attributes": [ { "name": "UserName", "showedOn": "PersistentObject" } ]
  }
}
```

- **Never copy a library type's file.** A copy is a delta that restates everything: it freezes the
  library's current values into your application, and the next library update no longer reaches you.
- **Never state an `id`.** Library ids are derived (below). The composer refuses a changed one.
- `--spark-synchronize-model` writes only the delta of a library type, never an id the library states
  and never a removal, and no file at all when nothing differs. It seeds no description of a library
  type into your `translations.json`.
- `model.schema.json` does not require `id`, so a delta validates.
- Remove an inherited attribute with `{ "name": "X", "$remove": true }`. The synchronizer never writes
  one: removal is always your decision (#253).

### Rights

Rights are a **keyed set**. Every element of `rights` carries a string `key`; your own keys are any
text without `:` (the five demo apps use their former ids). A library writes its keys bare
(`"passkeys-read"`), and the composer namespaces them with the alias (`authorization:passkeys-read`).

```json
{
  "rights": [
    { "key": "cars-read", "resource": "QueryRead/Car", "groupId": "@authenticated" },
    { "key": "authorization:passkeys-add", "$remove": true }
  ],
  "bindings": { "moderation:moderators": [ "Moderators" ] },
  "libraries": { "moderation": false }
}
```

- **Groups can be tokens.** `groupId` holds a group id, `@anonymous`, `@authenticated` (resolved
  through your `wellKnown`) or a library slot `alias:slot`.
- **Slots are bound by the application** in `"bindings"`, to one or more groups by id or by name
  (names match ignoring case). A slot bound to several groups composes into one right per group.
- **A library's rights are active as soon as it is referenced.** There is no opt-in. Remove one with
  `{ "key": "<alias>:<key>", "$remove": true }`, or switch every right of a library off with
  `"libraries": { "<alias>": false }`. Switched-off rights still appear in `securityPosture.txt`,
  marked inert, so an opt-out is as visible as a grant.
- **You never edit a library's right.** You remove it and add your own.
- **Library layers may only grant.** No `isDenied` and no `isImportant`. A library may grant any verb
  only on what it owns: the types and queries of its own model layers, plus the pseudo-types it
  declares in `"reservedTargets"` (Moderation reserves `Moderation`). A reserved target that names a
  composed model type, or one another library already declares, is refused. It may name groups only
  by token or by its own slots, and it may state only `rights` and `reservedTargets`.

Every problem refuses startup, and `SecurityConfigurationLoader` lists all of them at once. The build
reports the same sentences first:

| | |
|---|---|
| SPARK047 | a library breaks a guard rail (reported in the library's own build, and for referenced libraries in the application's) |
| SPARK048 | a token without its `wellKnown` entry, an unknown `@token`, an unbound slot, a binding to an undeclared group |
| SPARK049 | your file states a library key other than to remove it, removes a key no library ships, or names a library you do not reference in a key prefix, binding or opt-out |

A test host composes rights too. `SparkTestSecurity.Build()` switches off every catalogued library
that ships rights (`SparkTestSecurity.LibraryRightsOff()`), so a fixture states every right it means.

---

## The gates name the layer that moved

**`modelHashes.json`** (version 2) hashes the composed model and records, beside the hashes,
`libraries` (alias to assembly) and `layers`: for each model file, `actions.json` and
`programUnits.json` a library states, the structural hash of what each layer alone states. A drift
message says which layer moved:

```
file SparkUser.json: expected … actual … — layer library 'authorization' (MintPlayer.Spark.Authorization) changed
file SparkUser.json: shipped by library 'authorization' (MintPlayer.Spark.Authorization) but not in modelHashes.json
layers of Model/X.json: the composed structure is unchanged, but the layers stating it moved: …
library <alias> (<assembly>): newly states a layer …
```

A library update that changes a structure therefore turns `--spark-verify-model` red, and the
re-synchronization is the review. The identity is the per-layer structural hash, not the assembly
version, so a version bump alone changes nothing. A version 1 file says it predates the layers.

**`securityPosture.txt`** is a committed text table of every effective right:
`## Reachable without signing in (expanded)`, `## Granted to @anonymous`, `## Rights`,
`## Inert: libraries switched off` and `## Layers: libraries that ship rights`, one row
`group | effect | resource | key | layer` each. `--spark-verify-security` prints the changed lines
(`-`/`+`) and `Changed by layer: <alias> (<assembly>)`, exits 3 on any drift, and adds a
`::warning::` annotation when an anonymous section moved. Adding a library that ships rights, or a
library update that changes them, is a diff in that file in the same pull request.

```
dotnet run -- --spark-verify-model          # exit 3 on drift
dotnet run -- --spark-synchronize-model     # accept it, then commit modelHashes.json
dotnet run -- --spark-verify-security       # exit 3 on drift, 2 when security.json does not compose
dotnet run -- --spark-synchronize-security  # accept it, then commit securityPosture.txt
```

---

## Reload

Your `actions.json`, `translations.json`, `security.json`, `programUnits.json` and `culture.json`
recompose on save: one watcher policy for every kind (every change, create, delete or rename,
debounced 100 ms) and an atomic swap. A file that no longer composes keeps the previous snapshot and
logs why, except `security.json`, which fails closed: every authorization decision throws until the
file composes again. Labels follow a translations reload. The model's structure and `moderation.json`
are read once at startup.

---

## Writing a library that ships layers

1. **Put the files in the library's own `App_Data`**, at the path that names their kind:
   `Model/*.json`, `actions.json`, `translations.json`, `security.json`, `programUnits.json`,
   `moderation.json`.
2. **Name the library** in its csproj. The alias is required, lower-kebab, and permanent in practice:
   it is part of every right key, slot, opt-out and model id the library ships, so changing it
   rewrites them in every consuming application. Choose it like a package id.

   ```xml
   <PropertyGroup>
     <SparkLibraryAlias>my-library</SparkLibraryAlias>
   </PropertyGroup>
   ```

3. **Reference the generator privately.** `LibraryLayersGenerator` compiles each file into
   `[assembly: SparkLayer(alias, kind, path, json)]`. The generator package's `build/` targets add the
   items, with the `Content Remove` a Web SDK library needs (NETSDK1152).

   ```xml
   <PackageReference Include="MintPlayer.Spark.SourceGenerators" Version="…" PrivateAssets="all" />
   ```

   Without `PrivateAssets="all"` the generator flows into every application, runs there twice and
   breaks its build with CS0101 (SPARK044 warns).
4. **Stamp the model ids.** A library's model ids are UUIDv5 over the alias, the type, the member kind
   and the name, written into the file and checked by the generator (SPARK045):

   ```
   npm run stamp:library-model-ids -- <library project folder>
   ```

The layers travel inside the dll, so a `ProjectReference` and a `PackageReference` behave the same.
The application's generator records the layered assemblies (`[assembly: SparkLayerAssemblies(…)]`)
and the run time loads exactly those, because the compiler drops a reference whose types an
application never uses. An application needs nothing for this.

| | |
|---|---|
| SPARK041 | layer files without an alias, or one that is not lower-kebab; nothing is embedded |
| SPARK042 | a generic alias (`core`, `common`, `shared`, …) |
| SPARK043 | two referenced libraries with one alias (startup refuses too) |
| SPARK044 | the generator referenced without `PrivateAssets="all"` |
| SPARK045 | a model id missing or not the derived one |
| SPARK046 | a model file that is not strict JSON or names no type |
| SPARK047 | a `security.json` that breaks a guard rail |
| SPARK_TRANS_001…006 | a `translations.json` the run time would refuse |

`<SparkLibraryLayers>false</SparkLibraryLayers>` opts a class library out. Executables and test
projects never ship layers.

---

## In this repository

- **The Spark targets are imported once**, from the root `Directory.Build.targets`, for every
  project that is an `Exe` and not a test project. A csproj never imports `spark.props`,
  `spark.targets` or the Authorization targets by hand. A tool that is an `Exe` but no Spark
  application opts out with `<SparkApplication>false</SparkApplication>` (`tools/SchemaGenerator`
  does). The library targets gate themselves at execution time on the resolved references, so an
  application without Authorization gets none of its targets. The same file imports the generator
  package's `build/` targets for every in-repo library, since a `ProjectReference` imports nothing.
- **`SparkAppDataDir`** (default `App_Data`) moves the directory. The build passes it to the
  generators and stamps it into the assembly (`AssemblyMetadata`), and every runtime reader resolves
  the directory through `SparkAppData`, so the generators, the run time and the library-layer items
  all read the same place.
- `npm run test:layer-transport` packs the real libraries to a local feed and checks that a package
  reference composes the same as a project reference.

---

## See also

- [Authorization](guide-authorization.md) — the rights grammar the layers compose
- [Custom actions](guide-custom-actions.md) — `actions.json` layers
- [Translated strings](guide-translated-strings.md) — translation layers
- [Model hash](model-hash.md) — the model gate
- [Diagnostics](diagnostics.md) — every id above
- `docs/spark_composition_PRD.md` — the design and the evidence behind each rule
