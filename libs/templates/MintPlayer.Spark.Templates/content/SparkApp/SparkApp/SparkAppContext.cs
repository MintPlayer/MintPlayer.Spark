using MintPlayer.Spark;
using SparkApp.Library.Entities;

namespace SparkApp;

/// <summary>The application's entity roots: each queryable here is a collection the UI can list.</summary>
public partial class SparkAppContext : SparkContext
{
    public IQueryable<Person> People => Session.Query<Person>();
}
