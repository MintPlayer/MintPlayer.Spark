using HR.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents.Session;

namespace HR.Migrations;

/// <summary>
/// Seeds a clean 3-level breadcrumb chain so the recursive resolution is visible end-to-end:
/// <c>Person (Ada Lovelace) → Company (Acme Corp) → Profession (Software Engineering)</c>.
/// Renders as "Ada Lovelace @ Acme Corp · Software Engineering".
/// </summary>
public partial class M_202606081200_BreadcrumbDemo : ISparkMigration
{
    public static long Version => 202606081200;
    public static string? Description => "Seed 3-level breadcrumb demo (Person → Company → Profession)";

    [Inject] private readonly IAsyncDocumentSession session;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        await session.StoreAsync(
            new Profession { Description = "Software Engineering", Regime = "Full-time" },
            "Professions/demo-sweng", cancellationToken);

        await session.StoreAsync(
            new Company { Name = "Acme Corp", Website = "https://acme.example", EmployeeCount = 250, Sector = "Professions/demo-sweng" },
            "Companies/demo-acme", cancellationToken);

        await session.StoreAsync(
            new Person { FirstName = "Ada", LastName = "Lovelace", Email = "ada@acme.example", Company = "Companies/demo-acme" },
            "People/demo-ada", cancellationToken);

        await session.SaveChangesAsync(cancellationToken);
    }
}
