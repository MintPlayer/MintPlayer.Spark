# MintPlayer.Spark.Attributes

The attributes an entity library applies to its own types: `[GenerateIndex]`, `[Search]`, `[Sortable]`,
`[Reference]`, `[LookupReference]`, `[ValueObject]`, `[ValueKey]`, `[Breadcrumb]`, `[IgnoreProperty]`,
`[IgnoreForIndex]`, `[FromIndex]`, `[DefaultIndex]`, the generator-emitted `[SparkLayer]`, `[SparkLayerDependencies]`, `[SparkLayerAssemblies]` and `[SparkPlainRoot]`, and
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

## The default index: `[DefaultIndex(params Type[] entityTypes)]`

`[DefaultIndex]` claims an index as the default of **specific entity types**
([docs/default_index_PRD.md](../../../docs/default_index_PRD.md)): its view shapes each type's model file, and the
type's `Database.*` queries run on it.

- On a single-map index the argument is optional and must be the mapped `T` (`SPARK051`).
- On a multi-map index it is required (`SPARK052`): a referenced assembly carries no constructor bodies, so nothing
  can read its `AddMap<T>` calls. Each listed type needs an `AddMap` (`SPARK053`), and the index must derive from
  `SparkMultiMapIndexCreationTask` so Spark can narrow it to the queried type (`SPARK055`).
- Without a claim, the sole index of a type with a `[FromIndex]` view is its default. Two claims, or several viewed
  indexes and none, are `SPARK009`.
- `[GenerateIndex]` emits its index with the marker unless a hand-written index already claims the entity. Then it
  emits the index without it. `GenerateIndexAttribute.IsDefault` no longer exists.

`[SparkPlainRoot(typeof(T), nameof(Prop))]` is generator output: the Spark generator writes it on the context's
generated partial for each root whose body is `Session.Query<T>()`, and a query-less `[Reference(typeof(T))]` resolves
to that root's query.
