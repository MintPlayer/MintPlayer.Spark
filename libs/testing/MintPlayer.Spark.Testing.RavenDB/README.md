# MintPlayer.Spark.Testing.RavenDB

RavenDB for [MintPlayer.Spark.Testing](../MintPlayer.Spark.Testing/README.md): an embedded RavenDB server
(`RavenDB.TestDriver`) for Spark's test drivers.

```xml
<PackageReference Include="MintPlayer.Spark.Testing" />
<PackageReference Include="MintPlayer.Spark.Testing.RavenDB" />
```

- **Provider-neutral tests:** a class deriving from `SparkTestDriver` runs on RavenDB when this is the project's only
  provider, or when `SPARK_TEST_DATABASE=RavenDB` picks it among several.
- **RavenDB-specific tests:** `RavenSparkTestDriver` (a fresh database per test case, with a ready `IDocumentStore
  Store`) and `RavenSparkSharedDatabase` (one per class). `Store.AsSparkTestDatabase()` hands the store to
  `SparkEndpointFactory`.
- JSON fixture seeding, index deployment and waits, and the RQL recorder.

RavenDB 7.x needs a licence even for the embedded server: the `RAVENDB_LICENSE` environment variable (its JSON), or a
`raven-license.log` file at the repository root. See [the Testing README](../MintPlayer.Spark.Testing/README.md) for
the details.
