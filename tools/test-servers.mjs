// Which test database servers this process can reach, for Nx's cache key (#536 M9): a `runtime` input of the test
// targets whose outcome depends on them (MintPlayer.Spark.Tests, MintPlayer.Spark.Relational.Tests). A suite that
// skipped its SQL Server cases because SPARK_TEST_SQLSERVER was unset must not be replayed from the cache in a run that
// sets it, which is what an `env` input alone cannot say: CI's connection strings carry a password made per run.
//
// Prints whether each variable is set, and the image CI started (SPARK_TEST_*_IMAGE), never a connection string.
const servers = ['SPARK_TEST_SQLSERVER', 'SPARK_TEST_POSTGRESQL'];
console.log(servers.map((name) => `${name}=${process.env[name] ? process.env[`${name}_IMAGE`] || 'set' : 'unset'}`).join(' '));
