using DemoApp.Data;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace DemoApp;

public class DemoSparkContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
}
