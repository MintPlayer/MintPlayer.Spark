using DemoApp.Library.Entities;
using Raven.Client.Documents.Indexes;

namespace DemoApp.Indexes;

/// <summary>
/// Cars keyed by the company that owns them — the index behind the <c>company-cars</c> sub-query on
/// a Company's detail page.
/// </summary>
/// <remarks>
/// <para>
/// A second index over <c>Car</c>, beside <c>Cars_Overview</c>. It exists rather than reusing that
/// one for a single reason: <c>VCar</c> projects <c>OwnerFullName</c> (a display string) but not the
/// owner's <b>id</b>, and a sub-query filters on the id. Adding the id to <c>VCar</c> would have
/// worked too; a dedicated index keeps the list screen's projection about the list screen, and
/// demonstrates the multi-index binding a query gets through its <c>indexName</c>.
/// </para>
/// <para>
/// <c>Cars_Overview</c> stays the entity's <b>default</b> index — the one a query without its own
/// <c>indexName</c> falls back to. This one is reached only by the query that names it.
/// </para>
/// </remarks>
public partial class Company_Cars : AbstractIndexCreationTask<Car>
{
    public Company_Cars()
    {
        Map = cars => from car in cars
                      let owner = LoadDocument<Company>(car.Owner)
                      select new VCompanyCar
                      {
                          Id = car.Id,
                          CompanyId = car.Owner,
                          LicensePlate = car.LicensePlate,
                          Model = car.Model,
                          Year = car.Year,
                          Status = car.Status,
                          OwnerFullName = owner != null ? owner.Name : null,
                      };

        // Stored so the projection comes back from the INDEX rather than being re-materialized from
        // the document — without it, OwnerFullName (which only the index computes) reads as null.
        StoreAllFields(FieldStorage.Yes);
    }
}
