# MintPlayer.Spark.Attributes

The attributes an entity library applies to its own types: `[GenerateIndex]`, `[Search]`, `[Sortable]`,
`[Reference]`, `[LookupReference]`, `[ValueObject]`, `[ValueKey]`, `[Breadcrumb]`, `[IgnoreProperty]`,
`[IgnoreForIndex]`, `[FromIndex]`, `[DefaultIndex]`, `[SparkActions]`, `[SparkTranslations]` and
`[SparkAttributeDescription]`.

It is a plain `Microsoft.NET.Sdk` package with no dependencies, so a project that holds only entities
can reference it **without** pulling in the ASP.NET Core shared framework (#388). The other types an
entity library uses (`TranslatedString`, lookup references, …) are in `MintPlayer.Spark.Model`.

## The namespace is deliberately not the package name

Every attribute keeps the namespace `MintPlayer.Spark.Abstractions`, where it lived before the split.
Do not "fix" that: the Spark source generators and analyzers identify these attributes by fully
qualified metadata name, and generated code names them with `global::MintPlayer.Spark.Abstractions.…`.
Moving an attribute to a new *assembly* is invisible to both; moving it to a new *namespace* silently
switches generators off.

A project that uses `[GenerateIndex]` or `[ValueObject]` also needs the generator as its own analyzer
reference: analyzer `ProjectReference`s are not transitive.
