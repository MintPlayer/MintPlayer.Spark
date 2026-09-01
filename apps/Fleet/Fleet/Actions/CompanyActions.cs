using System.Runtime.CompilerServices;
using Fleet.Replicated;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;

namespace Fleet.Actions;

public partial class CompanyActions : DefaultPersistentObjectActions<Company>
{
    // Backs the StreamCompanies streaming query (WebSocket). Companies are a replicated
    // read-only copy from HR; this streams a small in-memory snapshot with periodic
    // employee-count drift so the live channel demonstrates incremental patches even
    // before HR replication has populated the collection.
    public override async IAsyncEnumerable<IReadOnlyList<Company>> StreamItems(
        StreamingQueryArgs args,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var companies = new List<Company>
        {
            new() { Id = "companies/contoso", Name = "Contoso", Website = "https://contoso.example", EmployeeCount = 1200 },
            new() { Id = "companies/fabrikam", Name = "Fabrikam", Website = "https://fabrikam.example", EmployeeCount = 340 },
            new() { Id = "companies/initech", Name = "Initech", Website = "https://initech.example", EmployeeCount = 58 },
        };

        // Initial snapshot
        yield return companies;

        // Continuous employee-count drift
        var random = new Random();
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellationToken);

            var company = companies[random.Next(companies.Count)];
            company.EmployeeCount = Math.Max(0, company.EmployeeCount + random.Next(-3, 4));

            yield return companies;
        }
    }
}
