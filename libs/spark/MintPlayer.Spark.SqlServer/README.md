# MintPlayer.Spark.SqlServer

Runs a Spark app on **SQL Server** (2022 or later) instead of RavenDB. The model, `security.json`, the menu, the
translations, the actions and the feature packages stay the same.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseSqlServer();
    spark.UseContext<AppContext>();
});
```

The connection string is `ConnectionStrings:Spark`.

- **A table per stored type,** mapped by EF Core from the Spark model. Value objects and collections are JSON columns,
  and string comparisons ignore case, as on RavenDB.
- **Schema changes are EF migrations.** `--spark-synchronize-model` writes the next one into `Migrations/SqlServer`
  when you reference [MintPlayer.Spark.Relational.Design](../MintPlayer.Spark.Relational.Design/README.md). The app
  applies its migrations at startup, or with `--spark-migrate` when `Spark:Database:ApplyMigrations` is `false`.
- **`SparkView<TRow>` views are SQL views:** a view declared once in C# is a RavenDB index on RavenDB and a
  `CREATE VIEW` here.
- **What a SQL store can't offer is refused at compile time (SPARK060).** That is RavenDB-to-RavenDB replication.
  Live updates, subscriptions and Messaging poll a change stamp; History reads a revisions table.

See [the SQL Server and PostgreSQL guide](../../../docs/guide-relational-stores.md).
