# Attribute descriptions

An attribute in a model file can carry a `description`: help text the client shows as an **[i]**
beside the attribute's label, with the text in a tooltip. It appears on the edit and create form,
the detail page, query grid column headers, AsDetail sub-table headers and the reference picker.
Custom attribute renderers get it for free — they replace the value cell, and the host still
renders the label.

```jsonc
{
  "name": "Company",
  "label":       { "en": "Company", "fr": "Entreprise", "nl": "Bedrijf" },
  "description": {
    "en": "The Company this person works for. Pick from the companies list; leave empty for freelancers.",
    "fr": "L'entreprise pour laquelle cette personne travaille. …",
    "nl": "Het bedrijf waarvoor deze persoon werkt. …"
  },
  "dataType": "Reference"
}
```

`description` is a [`TranslatedString`](guide-translated-strings.md) like `label`. It is
**presentational**: adding, editing or translating one never changes the model hash, so it never
refuses startup, and `--spark-synchronize-model` preserves whatever you wrote.

Do not confuse it with the entity-level `description` one level up, which is the page heading.

## Three ways to author the English text

You can always write the JSON by hand. Two C# surfaces can also **seed the English text** on
`--spark-synchronize-model`, so the explanation you already write for IntelliSense reaches the
user without being typed twice:

| Surface | When to use it | Example |
|---|---|---|
| `[System.ComponentModel.Description]` on the property | Explicit user-facing copy that should not double as IntelliSense | `[Description("Whether this person can sign in.")]` |
| `/// <summary>` on the property | You already document the property; the summary reads well to an end user | `/// <summary>The <see cref="Company"/> this person works for.</summary>` |
| JSON only | No C# source, or a virtual attribute with no CLR property | as above |

Precedence on synchronize: `[Description]` beats the summary; either beats nothing.

### Who owns which language

**C# owns `en` whenever it has text for the property. JSON owns every other language, and owns
`en` too when C# is silent.**

- Add a `<summary>` to a property whose attribute already exists in the model file: the next
  synchronize writes `description.en` (first key), keeping any `fr`/`nl` already there.
- Edit the summary: synchronize overwrites `en`; translators' `fr`/`nl` stay. To change the
  English, change the summary — that is where a developer looks for it.
- Hand-edit `en` on a property that has a summary: synchronize puts the summary back, and
  `--spark-verify-model` fails until you do. A stale English description is drift, caught in CI
  like any other, even though the model hash ignores descriptions.
- Remove the summary: the description stays as it is. Synchronize never deletes.

Running synchronize twice always produces byte-identical files.

## How the summary gets from C# into the model

A source generator, `AttributeDescriptionsGenerator`, turns every documented public read/write
property into one assembly-level row in the entity assembly:

```csharp
[assembly: SparkAttributeDescription(typeof(Person), "Company", "The Company this person works for. …")]
```

The synchronizer reads those rows by reflection — the same path it uses for `[Reference]` and
`[Sortable]` — so there is no file to locate and nothing to configure at run time.

`SparkAttributeDescriptionAttribute` is `[Conditional("DEBUG")]`. **A Release build of the entity
assembly contains none of these rows.** Descriptions are development-time input to the model JSON,
which is the production artefact; nothing about them ships. Every path that writes the model in
this repository builds Debug (`dotnet run` without `-c`, the IDE "Synchronize" profiles, the
`--spark-verify-model` step in CI). If you synchronize against a Release build, the assembly looks
like one with no summaries and the synchronizer prints one line saying so:

```
No attribute descriptions found in HR: either its public properties carry no /// summaries, or it was built in Release …
```

### The entity project must run the generator

The generator has to compile **the project that contains the `///` comments** — usually a
`*.Library` project that references only `MintPlayer.Spark.Abstractions`. Add the analyzer there:

```xml
<!-- NuGet consumers -->
<PackageReference Include="MintPlayer.Spark.SourceGenerators" Version="…" PrivateAssets="all" />

<!-- In this repository -->
<ProjectReference Include="..\..\..\libs\source_generators\MintPlayer.Spark.SourceGenerators\MintPlayer.Spark.SourceGenerators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

The other Spark generators in that assembly emit nothing in a library that has no actions,
context or Raven reference. Without the analyzer, `///` summaries are simply not harvested;
`[Description]` and JSON still work.

### `GenerateDocumentationFile` is optional

Without it (the default), the generator reads the raw `///` lines. With it, Roslyn hands the
generator structured XML with resolved `cref`s. Both render the same text for everything the
tooltip shows, so turn it on only if you want it for other reasons; add `CS1591` to `NoWarn` to
silence "missing XML comment" on the rest of the library.

### What the summary renders to

| You write | The tooltip shows |
|---|---|
| `<see cref="Company"/>`, `<see cref="Company.Name"/>` | `Company`, `Name` — the simple member name |
| `<see langword="null"/>` | `null` |
| `<c>9000</c>`, `<code>…</code>` | the text, verbatim |
| `<para>…</para>`, `<br/>` | a line break |
| `<paramref name="x"/>` | `x` |
| a summary wrapped over several source lines | one line |
| `<remarks>`, `<value>`, `<returns>` | nothing — only `<summary>` is used |
| `<inheritdoc/>` | nothing — the compiler does not expand it; write the summary out |

Whitespace collapses; the text is plain — no HTML, no Markdown.

Properties that are static, read-only, non-public, indexers, or marked `[IgnoreProperty]` never
produce a description, matching what the synchronizer turns into attributes.

## On the client

`@mintplayer/ng-spark/attribute-description` exports `SparkAttributeDescriptionComponent`:

```html
<spark-attribute-description [description]="attr.description" position="top" />
```

It renders nothing when the resolved text is empty, so it is safe to include unconditionally. When
there is text it renders a focusable button (`aria-label` = the text) with the `info-circle` icon
and a `*bsTooltip` that opens on hover **and focus**, closes on Escape, and sets
`aria-describedby` while open. Clicks are stopped so the [i] neither sorts a grid column nor moves
focus into the field. The text follows the language the user selected, with the usual `en`
fallback.

`EntityAttributeDefinition.description` and `QueryColumn.description` carry it; the per-object
`PersistentObjectAttribute` does not, because every label site reads the schema.
