# MintPlayer.Spark.Testing.SqlServer

SQL Server for [MintPlayer.Spark.Testing](../MintPlayer.Spark.Testing/README.md): each test, or each class, gets a
database of its own, its schema built from the model as the app's migrations would build it. It is dropped afterwards.

```xml
<PackageReference Include="MintPlayer.Spark.Testing" />
<PackageReference Include="MintPlayer.Spark.Testing.SqlServer" />
```

The server is the first of these that is available:

1. `SPARK_TEST_SQLSERVER`: a connection string without a database;
2. LocalDB, on Windows;
3. a Testcontainers container, which needs Docker.

A class deriving from `SparkTestDriver` runs on SQL Server when this is the project's only provider, or when
`SPARK_TEST_DATABASE=SqlServer` picks it among several. See
[the SQL Server and PostgreSQL guide](../../../docs/guide-relational-stores.md#testing).
