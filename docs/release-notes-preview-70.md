# Release notes — `10.0.0-preview.70` · `@mintplayer/ng-spark` `22.9.0`

One feature: **attribute descriptions, rendered as an [i] tooltip beside the label** (#348).

Additive on both sides. Apps on `preview.69` / `22.8.0` upgrade and see no change until a model
file declares a `description` or a property gains a `///` summary.

---

## The feature

Attributes in `App_Data/Model/*.json` can carry a `description` — a `TranslatedString`, like
`label`. The client shows it as a focusable [i] beside the label on the form, the detail page, grid
column headers, AsDetail headers and the reference picker, with the text in a tooltip that opens
on hover and on focus. Custom renderers need no change.

On `--spark-synchronize-model`, the English text is seeded from C#:

1. `[System.ComponentModel.Description]` on the property, else
2. the property's `/// <summary>`, carried into the assembly by the new
   `AttributeDescriptionsGenerator` as `[Conditional("DEBUG")]` assembly-level rows — so a Release
   build ships none of it, else
3. nothing; the JSON is left as you wrote it.

C# owns `en` when it has text; JSON owns the other languages. `--spark-verify-model` now fails on a
stale `en`, since the model hash deliberately ignores descriptions. Full guide:
[`docs/guide-attribute-descriptions.md`](guide-attribute-descriptions.md).

## What to do in your app

- **Entity libraries** that want `///` summaries harvested must reference the analyzer:
  `<PackageReference Include="MintPlayer.Spark.SourceGenerators" PrivateAssets="all" />`. Hosts
  already have it. Without it, `[Description]` and hand-written JSON still work.
- Nothing else. No JSON migration, no hash re-stamp.

## Also in this release

- `GenerateIndexGenerator` no longer emits index classes into a compilation that does not reference
  `RavenDB.Client`. Taking the Spark analyzer in an entity library that uses `[GenerateIndex]` used
  to fail with CS0400; the host, which references Raven, still compiles those indexes from the
  referenced assembly.

## Surface

| | Added |
|---|---|
| Abstractions | `EntityAttributeDefinition.Description`, `QueryColumn.Description`, `SparkAttributeDescriptionAttribute` |
| Source generators | `AttributeDescriptionsGenerator` → `SparkAttributeDescriptions.g.cs` |
| Synchronizer | `[Description]`/summary seeding of `description.en`; description drift in `--spark-verify-model` |
| `@mintplayer/ng-spark` | entry point `attribute-description` (`SparkAttributeDescriptionComponent`); `description?` on `EntityAttributeDefinition` and `SparkCellColumn` |

No breaking changes. No shims, nothing removed.
