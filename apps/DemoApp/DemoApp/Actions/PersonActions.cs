using MintPlayer.Spark.Abstractions.Authorization;
using DemoApp.Library.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;

namespace DemoApp.Actions;

public partial class PersonActions : DefaultPersistentObjectActions<Person>, ISparkOwnsRowSecurity, IBeforeSave<Person>
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Demo data, published in full on purpose: security.json grants this type to the anonymous role so the sample app works with no sign-in. There is nothing per-caller in a demo Person, so no filter would have anything to narrow. A real application listing people would need one.";

    [Inject] private readonly ISparkSession session;

    public ValueTask OnBeforeSaveAsync(Person entity, SaveContext context)
    {
        if (!string.IsNullOrEmpty(entity.Email))
        {
            entity.Email = entity.Email.Trim().ToLowerInvariant();
        }

        entity.FirstName = entity.FirstName?.Trim() ?? string.Empty;
        entity.LastName = entity.LastName?.Trim() ?? string.Empty;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Custom query: returns people belonging to a specific company.
    /// Source: "Custom.Company_People". Plain LINQ over the collection, so it runs on any store provider.
    /// </summary>
    public IQueryable<Person> Company_People(CustomQueryArgs args)
    {
        args.EnsureParent("Company");
        return session.Query<Person>()
            .Where(p => p.Company == args.Parent!.Id);
    }
}
