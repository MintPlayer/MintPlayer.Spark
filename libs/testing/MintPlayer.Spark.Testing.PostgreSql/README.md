# MintPlayer.Spark.Testing.PostgreSql

PostgreSQL for [MintPlayer.Spark.Testing](../MintPlayer.Spark.Testing/README.md): each test, or each class, gets a
database of its own, its schema built from the model as the app's migrations would build it. It is dropped afterwards.

```xml
<PackageReference Include="MintPlayer.Spark.Testing" />
<PackageReference Include="MintPlayer.Spark.Testing.PostgreSql" />
```

The server is `SPARK_TEST_POSTGRESQL` when it is set. Otherwise it is an embedded PostgreSQL 18 (no Docker needed): the
named instance `spark-tests`, started once and reused by later runs.

A class deriving from `SparkTestDriver` runs on PostgreSQL when this is the project's only provider, or when
`SPARK_TEST_DATABASE=PostgreSql` picks it among several. See
[the SQL Server and PostgreSQL guide](../../../docs/guide-relational-stores.md#testing).
