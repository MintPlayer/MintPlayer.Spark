# MintPlayer.Spark.Relational.Design

Lets `--spark-synchronize-model` write the EF Core migration for a model change on
[MintPlayer.Spark.SqlServer](../MintPlayer.Spark.SqlServer/README.md) or
[MintPlayer.Spark.PostgreSql](../MintPlayer.Spark.PostgreSql/README.md).

```bash
dotnet add package MintPlayer.Spark.Relational.Design
dotnet run --no-launch-profile -- --spark-synchronize-model
```

Synchronize updates `App_Data/Model/*.json` and, when the schema changed, writes the next migration into
`Migrations/<Provider>` in the host project. Review it and commit it with the model. There is no `dotnet ef` and no
design-time factory.

- **A development dependency:** `dotnet add` writes `PrivateAssets="all"`, so it stays in `bin` and out of every
  publish.
- **Synchronize refuses two migrations that would lose data,** and writes nothing: a table the last snapshot already
  has, created again, and a column dropped and added for the same attribute id (a renamed attribute; a new attribute
  needs a new id).

See [the SQL Server and PostgreSQL guide](../../../docs/guide-relational-stores.md).
