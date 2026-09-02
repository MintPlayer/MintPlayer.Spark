# PRD — Attribute descriptions: model-JSON help text rendered as an [i] tooltip beside the attribute label

**Status:** Draft
**Issue:** [#348](https://github.com/MintPlayer/MintPlayer.Spark/issues/348)
**Branch:** `feat/issue-348-attribute-descriptions`
**Plan:** `docs/issue_348_plan.md`
**Base:** `master` @ `0ea11110`
**Release:** `10.0.0-preview.70` · `@mintplayer/ng-spark` + `@mintplayer/ng-spark-auth` `22.9.0`
**Breaking changes:** allowed — the libraries are in preview. None are needed for this issue; everything is additive.

---

## Problem

Attributes in the model JSON (`App_Data/Model/<Entity>.json`) carry a `label` but no help text. A user looking at a form field named "Reference display type" has no way to learn what it means without leaving the page. Developers, meanwhile, already write that explanation twice: once as a `/// <summary>` on the C# property for IntelliSense, and once verbally when someone asks.

The issue asks two questions:

1. **Where do descriptions live** — in the model JSON beside the rest of the attribute metadata, or in a source-generated C# file hosted by the middleware at a dedicated endpoint?
2. **How do descriptions get from C# into that store** — `System.ComponentModel.DescriptionAttribute`, XML documentation comments, or something else?

And it asks for one rendering: an [i] icon/button beside the attribute label, carrying a tooltip with the description.

## Prior art

- **Vidyano** has an attribute-level "tooltip"/"description" concept in its model metadata, authored in the model store and shown next to the label in the UI. Its text is translatable like every other label. Our shape follows that precedent: help text is model metadata, not code.
- **Every other user-visible string in our model JSON is already a `TranslatedString`**: entity `description` (rendered as the page heading), attribute `label`, validation `message`, program-unit and group `name`, query `description`. `docs/guide-translated-strings.md` documents the `{ "en": …, "fr": …, "nl": … }` wire shape and the client's `resolveTranslation` fallback chain.
- **Custom actions already have a `description` field** in their JSON "for documentation/tooltips" (`docs/guide-custom-actions.md`) — the closest existing precedent for where an attribute description belongs.
- **`MintPlayer.SourceGenerators.DescriptionSourceGenerator`** (external package, `C:\Repos\MintPlayer.Dotnet.Tools`) harvests `///` comments with `GetLeadingTrivia()` and re-emits them as `[System.ComponentModel.Description]` on a **partial type**. It is the "process XML markup somehow" prior art the issue links.
- **`GenerateIndexAttribute.Description`** is already read by a Spark generator and re-emitted as `[Description]` on a generated class (`GenerateIndexGenerator.Producer.cs:245-246`) — the in-repo precedent for "generator carries doc text into compiled metadata".

## Investigation findings

### F1 — The model JSON already has a slot shape for this, and the synchronizer already preserves it

`EntityAttributeDefinition` (`libs/spark/MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs:79-180`) has `Label : TranslatedString?` and no description. The synchronizer's update branch (`libs/spark/MintPlayer.Spark/Services/ModelSynchronizer.cs:640-708`) mutates the *deserialized existing* attribute object in place and only reassigns the structural fields (`DataType`, `Order`, `ReferenceType`, `Query`, `IsArray`, `IsSortable`, `AsDetailType`, `LookupReferenceType`, `InCollectionType`, `InQueryType`, `ShowedOn`). Anything else — `Label`, `Rules`, `Renderer`, `Group`, … — survives re-sync simply by never being assigned. A hand-authored `description` would therefore round-trip through `--spark-synchronize-model` with **zero** synchronizer changes.

### F2 — `description` is already declared non-structural by the model-hash gate

`libs/spark/MintPlayer.Spark.Abstractions/Model/ModelFileShape.cs:36-38,138-143`: `StructuralAttributeFields` is a whitelist, and its doc comment already lists `description` as presentational and ignored. Adding the field will not trip `modelHashes.json` at startup. It **must not** be added to that whitelist, nor to `SparkModelShape.Describe` (`Model/SparkModelShape.cs:51,74`), or translating a tooltip would refuse app startup.

### F3 — There are two independent channels to the client, and the schema channel is enough for most sites

- Schema, once per type: `GET /spark/entity-type/{id}` (`Endpoints/EntityTypes/Get.cs:17-32`) serializes `EntityTypeDefinition` itself; TS mirror `libs/node_packages/ng-spark/models/src/entity-type.ts:16`.
- Per object, every request: `GET /spark/po/{type}/{id}` serializes `PersistentObjectAttribute` (`Abstractions/PersistentObject.cs:128+`), built by the 14-field copy in `EntityMapper.FromDefinition` (`Services/EntityMapper.cs:394-445`, `Label = def.Label` at `:400` and `:434`) and hand-serialized by `PersistentObjectAttributeJsonConverter.cs:150-171` + `KnownFieldNames` `:206-210`.
- Grid columns are a third shape: `QueryResultProjector.cs:48-62` (`Label = a.Label` at `:51`) → `QueryColumn` / `SparkCellColumn` (`models/src/query-result.ts:29-58`).

Putting the description on the wire per attribute per object grows every PO and every AsDetail row for a value that is constant per type. The schema channel already reaches the form and detail hosts; which shape each label site actually reads is spike S1.

### F4 — Labels are emitted in one template per host, not per renderer

There is no shared attribute-label component, but each host emits its label exactly once inside a reused `ng-template`:

| Host | Site |
|---|---|
| Edit + create form | `po-form/src/spark-po-form.component.html:45-50` (the required-marker `*` at `:47-49` is where the [i] goes) |
| Detail page | `po-detail/src/spark-po-detail.component.html:86` |
| Query grid header | `grid/src/spark-query-grid.component.html:42-46` (inside `*bsDatatableColumn … sortable: true`) |
| AsDetail sub-table `<th>` | `spark-po-form.component.html:204`, `:270`; `spark-po-detail.component.html:95` |
| Incidental | reference-picker modal header `spark-reference-picker.component.html:65-68`; `[title]` bindings at `spark-po-form.component.html:63,99,133,156` |

The card view emits no attribute labels (delegates rows to the grid). Custom renderers (`docs/guide-custom-attribute-renderers.md`) replace the **value cell only**; the host still emits the label, so they get the [i] for free, and a renderer that declares `attribute` / `column` receives the description on that object automatically.

### F5 — `@mintplayer/ng-bootstrap/tooltip` is present, accessible, and unused in Spark today

`BsTooltipDirective` (structural, `*bsTooltip="'top'|'bottom'|'start'|'end'"`, content is arbitrary template markup) is placed on a child *inside* the trigger; the trigger is the parent element and needs `position-relative`. It opens on `mouseenter` **and `focusin`**, closes on leave / `focusout` / Escape / window blur, keeps open while the pointer is over the overlay, and sets `aria-describedby` on the trigger while shown with `role="tooltip"` on the overlay (`tooltip/src/directive/tooltip.directive.ts:40-45,131-149`). Spark pins `@mintplayer/ng-bootstrap ^22.17.0` (root `package.json:32`) and the entry point exists in the installed 22.17.0; `ng-spark/package.json` peer-declares `^22.13.0` — whether that range needs raising is spike S3. There are **zero** `bsTooltip`/`bsPopover` usages in `libs/node_packages` or `apps` today.

The icon set is **Bootstrap Icons** (`bootstrap-icons 1.13.1`), not Font Awesome; `<spark-icon name="info-circle">` falls back to class `bi bi-info-circle` without registry changes (`icon/src/spark-icon.component.ts:38`).

### F6 — A source generator cannot put `[Description]` on an ordinary property, but it can emit a lookup

`DescriptionSourceGenerator` emits `[Description(...)] partial class X { }` — a *second partial declaration* of the type. That trick has no property equivalent: an auto-property on a POCO entity cannot be re-declared from generated code (C# 13 partial properties require the user to write the defining part as `partial`, which is not something we can ask of every entity property). Its `TODO` at `DescriptionSourceGenerator.cs:36` ("might also be used on methods and properties") is therefore not a small extension.

What a generator **can** do is emit a lookup that reflection reads back: either a static class of `const string` fields, or assembly-level attributes `[assembly: SparkAttributeDescription(typeof(Person), nameof(Person.FirstName), "…")]`. Two facts decide between them:

- `const` fields do not vanish after compilation. They remain in the assembly as literal metadata fields (`FieldInfo.GetRawConstantValue()`); only their *uses* are inlined. A const class ships in Release unless the generator is made conditional, and it needs a naming convention to be discoverable.
- Assembly-level attributes need no convention (`Assembly.GetCustomAttributes<T>()` yields type + property + text in one step), and if the attribute class carries `[Conditional("DEBUG")]` **the compiler omits every application of it when `DEBUG` is not defined**. That gives "development only" for free, with no generator knowledge of the build configuration and nothing shipped in Release.

### F7 — Where the generator gets the text from is the real choice; the flag question is unchanged

Roslyn only produces *structured* documentation (`ISymbol.GetDocumentationCommentXml()`, `DocumentationCommentTriviaSyntax` with resolved `cref` IDs like `P:Ns.Type.Prop`) when the compilation's `DocumentationMode` is at least `Parse`, which the SDK sets when `GenerateDocumentationFile=true`. Without the flag the generator sees `///` lines as raw trivia text — which is exactly why `DescriptionSourceGenerator` regexes lines and leaves `cref` unresolved. So:

- **No flag** → trivia scraping: works everywhere, but line-based, `<see cref="Company"/>` is whatever the author typed, no `<inheritdoc/>`, and partial-type docs may be seen twice.
- **Flag on** → structured API: clean `<summary>` XML, resolved crefs, and (in the generator) no dependency on the emitted `.xml` file's location at all.

A hybrid is cheap: prefer the structured XML when non-empty, fall back to trivia otherwise. Consumers who care about crefs turn the flag on; everyone else still gets a summary. `GenerateDocumentationFile` is set **nowhere** in this repo today.

### F8 — The synchronizer already runs offline with the entity assemblies loaded, and already reflects attributes per property

`--spark-synchronize-model` / `--spark-verify-model` run pre-`Build()`, need no RavenDB, and build the catalog from the module registry's declared assemblies (`SparkDevelopmentExtensions.cs:20-21,445-459`). `ModelSynchronizer.cs:575-577` already reads `[Reference]`, `[LookupReference]`, `[Sortable]` via `GetCachedCustomAttribute<T>()` per property. Reading `[Description]` there is a one-line addition; reading generator-emitted assembly attributes for `property.DeclaringType.Assembly` is a small per-assembly cache at the same point. Because the text is compiled into the entity assembly, there is no file to locate — unlike parsing the compiler's `.xml` doc file, which would depend on `Assembly.Location`, copy-to-output behaviour, and the Docker sync stage.

### F9 — Only `label` seeding on create is the existing precedent, and it is the wrong precedent for this field

New attributes get `Label = TranslatedString.Create(AddSpacesToCamelCase(name))` in the create branch only (`ModelSynchronizer.cs:717`). If descriptions were seeded on create only, adding a `/// <summary>` to a property whose attribute already exists in JSON — i.e. every attribute in every existing app — would do nothing. The seeding rule has to run on update too, and it has to be a fixed point (`reference_synchronize_fixed_point`): running sync twice must produce byte-identical JSON.

### F10 — No JSON schema exists to update

No `$schema`, no `schemas/` folder, no Spark Editor PRD in the repo (`docs/issue_324_PRD.md:559` is the only mention). The de-facto schema is `EntityTypeDefinition.cs` ↔ `entity-type.ts`, changed in lockstep, plus the prose tables in the guides.

### F11 — Naming: `description` already means "heading" at entity level

`EntityTypeDefinition.Description` (`:7`) is rendered as the page heading of create/edit pages. An attribute-level `description` is help text. The two are distinguishable by level, and `description` is what the issue, Vidyano, and the custom-action JSON all call it. Keeping the name and documenting the semantic per level beats inventing `tooltip`/`hint`.

### F12 — The Spark generator project has every hook this needs

`libs/source_generators/MintPlayer.Spark.SourceGenerators/`: all generators derive from `IncrementalGenerator`, `PersistentObjectNamesGenerator` already scans entity types, `GenerateIndexGenerator` already carries doc text into emitted attributes, tests live in `tests/MintPlayer.Spark.SourceGenerators.Tests/{Generators,Snapshots,VerifyResults}`, and packaging is via `MintPlayer.Spark.AllFeatures.csproj:39-53` (`analyzers/dotnet/cs`). A generator that only emits attributes needs no line in `SparkFullGenerator.Producer.cs`.

## Options

### Where descriptions live

| | Option | Pros | Cons |
|---|---|---|---|
| **A ★** | **In the model JSON**, `attributes[].description : TranslatedString?`, presentational | Translatable like `label`; survives sync (F1); no hash impact (F2); author-editable without a rebuild; visible to any future editor tooling (F10); one metadata channel; same place as custom-action descriptions | Requires a bridge from C# into JSON (the sync pipeline, F8) |
| B | Source-generated C# file hosted at a middleware endpoint (the issue's alternative) | No JSON churn; text lives next to the property | Monolingual; a second metadata channel the client has to fetch and join by type+attribute name; not editable without recompiling; invisible to editor tooling; the generator can't see runtime-only shape (renamed attributes, `[IgnoreProperty]`) |
| C | Reflect at request time in `EntityMapper` | Zero authoring pipeline | Hot path; monolingual; production deploy must carry the text; nothing for the query-column shape |

**Decision: A.** B and C are rejected, not deferred.

### How text gets from C# into the JSON

| | Option | Pros | Cons |
|---|---|---|---|
| 1 | Hand-author in JSON only | Zero C# work; fully multilingual | No IntelliSense benefit; duplicate authoring if a `<summary>` already exists |
| 2 | `System.ComponentModel.DescriptionAttribute`, read at sync (F8) | One line to read; explicit "this is user-facing" intent; no build flag | Plaintext, monolingual, no `cref`; VS hover does **not** show it |
| 3 | XML `<summary>` → compiler `.xml` doc file → parsed at sync | No generator | Hard requirement on `GenerateDocumentationFile`; the sync must *find* the file (`Assembly.Location`, copy-to-output for referenced libraries, Docker sync stage); file present in production output unless stripped |
| **4 ★** | **XML `<summary>` → Spark source generator → `[Conditional("DEBUG")]` assembly-level lookup attributes → read by reflection at sync** (F6, F7, F8, F12) | Developers already write these; VS hover + clickable `<see cref>` for free; text is *in the assembly*, so sync reads it through the same reflection path as `[Description]` — no file to locate; **nothing ships in Release**; flag is optional (hybrid: structured XML when available, trivia otherwise) | A new generator with snapshot tests; trivia fallback has the usual limits (unresolved `cref`, no `<inheritdoc/>`); monolingual seed; a terse IntelliSense summary is not always good user-facing copy |
| 5 | A Spark-specific multilingual attribute `[SparkDescription(en, fr, nl)]` | Translations in code | Translations belong in JSON/translation files everywhere else; a third attribute vocabulary; rejected |

**Decision: 1 + 2 + 4, layered.** JSON is the store and is always authorable (1). At sync time a C# source may seed the **English** text: `[Description]` if present (2), else the generator-emitted summary for that property (4). Option 3 is rejected in favour of 4 because 4 removes the file-location dependency and ships nothing to production. Option 5 is rejected. Emission shape is assembly-level attributes, not a const class (F6).

### Who wins when both C# and JSON have text

| | Rule | Fixed point? | Hand edits? |
|---|---|---|---|
| i | Seed only when JSON has none | yes | `en` and others authoritative once set — but C# text changes never propagate (silent drift) |
| ii ★ | **C# owns `en` whenever a C# source exists; JSON owns every other language and owns `en` only when no C# source exists** | yes | Translators edit `fr`/`nl` freely; fixing English means fixing the `<summary>`, which is where a developer expects it |
| iii | Provenance marker (`descriptionSource: "clr"`) like the #275 query provenance | yes | Most flexible; adds a field only tooling reads; over-engineered for a string |

**Decision: ii**, unless spike S2 shows `--spark-verify-model` cannot report the resulting `en` drift as a diff (it should — that is the point).

## Design

### D1 — `description : TranslatedString?` on `EntityAttributeDefinition`, presentational

Added at `EntityTypeDefinition.cs:79+` beside `Label`, camelCase `description`, `WhenWritingNull` so untouched models change by zero bytes. Mirrored in `entity-type.ts:16+`. Not in `StructuralAttributeFields`, not in `SparkModelShape`. Semantics: *help text for the person filling in or reading the attribute*, distinct from the entity-level `description` (heading).

### D2 — `SparkAttributeDescriptionAttribute` + `AttributeDescriptionsGenerator`

In `MintPlayer.Spark.Abstractions`:

```csharp
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[Conditional("DEBUG")]
public sealed class SparkAttributeDescriptionAttribute(Type type, string property, string summary) : Attribute { … }
```

`[Conditional("DEBUG")]` means Release builds contain no applications of it (F6). The type itself stays in Abstractions (it is tiny and reflection needs it in Debug).

New `AttributeDescriptionsGenerator` in `MintPlayer.Spark.SourceGenerators`: for every type the synchronizer would treat as a Spark entity (same predicate as `PersistentObjectNamesGenerator`), for every public instance property not marked `[IgnoreProperty]`, resolve a summary:

1. `symbol.GetDocumentationCommentXml()` → `<summary>` when non-empty (flag on);
2. else the leading `///` trivia parsed with the same sanitizer rules (flag off);
3. else nothing — no attribute emitted.

Rendering to plain text (both paths): `<para>` → newline, `<c>`/`<code>` → verbatim, `<see cref="X:A.B.C"/>` → `C` (last identifier segment; for raw trivia, the text inside the quotes reduced the same way), `<see langword="x"/>` → `x`, `<paramref>`/`<typeparamref>` → the name, remaining tags stripped, entities decoded, whitespace collapsed per line. `<inheritdoc/>` yields nothing. Emits one file `SparkAttributeDescriptions.g.cs` of `[assembly: …]` lines, sorted by type then property for stable snapshots. Incremental over property declarations; no dependency on `AdditionalTexts`.

### D3 — Seeding at sync: `[Description]` → generated summary → nothing

In `ModelSynchronizer`'s per-property loop (`:575-577`):

1. `property.GetCachedCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description`, trimmed, if non-empty;
2. else the `SparkAttributeDescriptionAttribute` for `(property.DeclaringType, property.Name)` from a per-assembly cache built with `Assembly.GetCustomAttributes<SparkAttributeDescriptionAttribute>()` (walk the base-type chain so inherited properties resolve against the declaring type);
3. else no seed.

Apply rule ii in **both** the create and update branches: with a seed, `description["en"] = seed` (creating the `TranslatedString` if needed, preserving other keys, canonical key order per S2); without a seed, leave `description` untouched. Result is a fixed point. A Release-built assembly (no attributes) behaves exactly like "no C# source", by construction — but the synchronizer logs one info line when it finds zero `SparkAttributeDescriptionAttribute`s on an assembly that has Spark entities, so a Release-configured sync is diagnosable.

### D4 — Wire shape follows the label sites, not the other way round

- `QueryColumn`/`SparkCellColumn` gets `description` via `QueryResultProjector.cs:51` — one entry per column per result, negligible.
- `PersistentObjectAttribute` gets `description` **only if** spike S1 shows a label site reads the per-object shape and cannot reach the entity-type schema. If added: `PersistentObject.cs:132`, `EntityMapper.cs:400` + `:434`, `WriteSharedFields`, `KnownFieldNames`, `persistent-object-attribute.ts`.

### D5 — One tiny presentational component, dropped into each label site

`<spark-attribute-description [description]="…" />` in a new `@mintplayer/ng-spark/attribute-description` secondary entry point (folder + `ng-package.json` + `index.ts`, per `ng-spark-library-structure`). Renders nothing when the resolved translation is empty; otherwise:

```html
<button type="button" class="btn btn-link p-0 ms-1 position-relative align-baseline"
        [attr.aria-label]="'Description' | sparkTranslate">
  <spark-icon name="info-circle" />
  <span *bsTooltip="'top'" class="spark-attribute-description">{{ description | resolveTranslation }}</span>
</button>
```

`white-space: pre-line` so `<para>` newlines survive. Dropped into the sites listed in F4: form label, detail `<dt>`, grid header, the three AsDetail `<th>`s, and the reference-picker header. In the grid header the button must stop propagation so the [i] does not toggle the sort (spike S3). Peer range of `@mintplayer/ng-bootstrap` in `ng-spark/package.json` raised to the first version that ships `tooltip/` (spike S3).

### D6 — Demo coverage exercises all three authoring surfaces and both generator paths

`apps/DemoApp/DemoApp.Library`: `Person` gets one property with a `/// <summary>` containing a `<see cref>`, one with `[Description]`, and `Person.json` gets a hand-authored `fr`/`nl` for one of them plus a JSON-only description on a third attribute. `DemoApp.Library` turns `GenerateDocumentationFile` on (structured path); `apps/HR` documents one property with the flag **off** (trivia path). After sync, both JSON files show the expected text and `--spark-verify-model` is clean. A Release build of DemoApp.Library is inspected to confirm zero `SparkAttributeDescriptionAttribute` applications.

### D7 — Nothing changes in `MintPlayer.SourceGenerators`

`DescriptionSourceGenerator` keeps doing what it does for types. Its property `TODO` is not the mechanism this issue uses (F6); we do not extend it. The sanitizer rules in D2 are re-implemented in Spark's generator (small, tested), not shared, to avoid coupling Spark's release to the external package.

## Acceptance criteria

- **AC1** A `description` on an attribute in model JSON survives `--spark-synchronize-model` byte-for-byte, in all languages, when the property has no `[Description]` and no generated summary.
- **AC2** A `[Description("x")]` on a property sets `description.en = "x"` on sync for a *pre-existing* attribute and for a new one; `fr`/`nl` already in the JSON are preserved.
- **AC3** A `/// <summary>` seeds `en` via the generated attribute; `<see cref="…"/>` renders as the simple member name; `<para>` becomes a newline — with `GenerateDocumentationFile` on **and** off.
- **AC4** `[Description]` wins over the generated summary when both exist.
- **AC5** Running sync twice yields identical files (fixed point) in every AC1–AC4 configuration.
- **AC6** Adding or translating a `description` does not change `modelHashes.json` and does not refuse startup.
- **AC7** `--spark-verify-model` reports a diff when `en` in JSON differs from the C# seed.
- **AC8** Form label, detail `<dt>`, grid header, and AsDetail headers render an [i] button only when the resolved description is non-empty; the button is keyboard-focusable and the tooltip shows on focus and hover with `aria-describedby` set.
- **AC9** Clicking the [i] in a sortable grid header does not change the sort.
- **AC10** A custom edit/detail/column renderer receives `description` on its `attribute`/`column` input without renderer changes.
- **AC11** The tooltip text switches language with `SparkLanguageService.setLanguage`.
- **AC12** A Release build of an entity assembly contains no `SparkAttributeDescriptionAttribute` applications; sync against it behaves as "no C# source" and logs one info line.
- **AC13** Generator snapshot: `[IgnoreProperty]` properties, undocumented properties, and `<inheritdoc/>` properties emit nothing; output ordering is stable.

## Breaking changes

Preview rules apply (breaks ship as minors; majors stay platform-locked). **None.** All fields are optional and serialized `WhenWritingNull`; the client component renders nothing for absent descriptions; the new attribute type is additive; the generator emits nothing for undocumented code. The `@mintplayer/ng-bootstrap` peer-range raise in `ng-spark` is additive for every consumer already on the workspace pin.

No shims, no `[Obsolete]` — nothing is removed.

## Out of scope (genuinely not being done)

- **Rich text / Markdown / HTML in descriptions.** Tooltip content is plain text with newlines. HTML would make a JSON field a script sink and needs sanitizing rules the framework does not have; revisit only if a consumer asks.
- **Descriptions on queries, tabs, groups, custom actions, program units.** Queries and custom actions already have `description`; tabs/groups have no demand yet. Same mechanism would apply; not built here.
- **Parsing the compiler's `.xml` doc file at sync time** (option 3). Superseded by the generator; keeping both would be two readers for one fact.
- **Extending `DescriptionSourceGenerator` to properties** (F6, D7).
- **A Spark MSBuild target that turns on `GenerateDocumentationFile` for consumers.** The generator works without it; the flag only upgrades cref fidelity, and forcing it would emit CS1591 into every consumer project.
- **Multilingual C# attribute** (option 5). Translations live in JSON.
- **`<inheritdoc/>` resolution.** Neither path expands it; a property that relies on it gets no seed and is documented as such.
- **Emitting descriptions in Release builds** (e.g. for a runtime endpoint). `[Conditional("DEBUG")]` is the point; the JSON is the production artefact.
- **Spark Editor JSON schema.** Does not exist yet (F10); when it does, `description` is one field to add.

## Spikes

Time-boxed; results are recorded in the plan under M0 and may reshape the milestone they name.

| # | Question | Answered by |
|---|---|---|
| S1 | Which TS shape does each label site in F4 read (`EntityAttributeDefinition` vs `PersistentObjectAttribute` vs `SparkCellColumn`)? Does the per-object wire field (D4) need to exist at all? | Read the four host components + their inputs; prototype on branch |
| S2 | Does `--spark-verify-model` diff the whole file (so an `en` seed change is reported, AC7), or only the structural hash? Is rule ii a fixed point through `TranslatedString` serialization (key order, empty-dictionary handling)? | Run verify on DemoApp with a deliberately stale `en`; run sync twice and `git diff` |
| S3 | Which `@mintplayer/ng-bootstrap` version introduced `tooltip/`? Does `*bsTooltip` position correctly inside a `*bsDatatableColumn` header and inside `bsColFormLabel`; does a `<button>` inside the sortable header need `stopPropagation`; does a `<button>` inside `<label for>` steal focus? | Check the ng-bootstrap git history; prototype the component in DemoApp |
| S4 | Generator input: with `GenerateDocumentationFile` off, what exactly does the generator see for `///` on a property — `SingleLineDocumentationCommentTrivia` with a structure, or plain trivia text? With it on, what does `GetDocumentationCommentXml()` return for `<see cref="Company"/>`, `<see cref="Company.Name"/>`, `<see langword="null"/>`, `<para>`, `<c>`, `<inheritdoc/>`, a property on a nested type, a property on a generic type? Are both paths available in the same incremental pipeline without a full-compilation dependency? | A throwaway generator test in `MintPlayer.Spark.SourceGenerators.Tests` over a fixture file, run under both `DocumentationMode`s |
| S5 | Does `[Conditional("DEBUG")]` on an attribute class really drop assembly-level applications in a Release build of the entity library, and does the app's `--spark-synchronize-model` in Debug see them when the entity library is a `ProjectReference` (Debug) and when it is a NuGet package (Release-built)? What does the Docker sync stage build with? | Build DemoApp.Library Debug + Release, inspect with `ildasm`/`System.Reflection.Metadata`; read `apps/*/Dockerfile` |
