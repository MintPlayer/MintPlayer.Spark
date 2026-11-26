# MintPlayer.Spark.Relational

The relational core that [MintPlayer.Spark.SqlServer](../MintPlayer.Spark.SqlServer/README.md) and
[MintPlayer.Spark.PostgreSql](../MintPlayer.Spark.PostgreSql/README.md) share. You don't reference it yourself: add one
of those two and call `spark.UseSqlServer()` or `spark.UsePostgreSql()`.

It holds:

- the EF Core model Spark builds from the app model: a table per stored type, child and join tables, JSON columns;
- the unit of work behind `ISparkSession`, with optimistic concurrency (a stale save is a 409);
- constraint errors turned into validation messages that name the attribute;
- the framework's own tables in the `spark` schema: atomic values, reservations, revisions, tombstones and
  data-protection keys;
- the change feed that live updates, subscriptions and Messaging poll;
- the migration runner that applies the framework schema, the app's migrations and its data migrations at startup.

See [the SQL Server and PostgreSQL guide](../../../docs/guide-relational-stores.md).
