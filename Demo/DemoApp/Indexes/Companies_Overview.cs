using DemoApp.Data;
using DemoApp.Library.Entities;
using Raven.Client.Documents.Indexes;

namespace DemoApp.Indexes;

public class Companies_Overview : AbstractIndexCreationTask<Company>
{
    public Companies_Overview()
    {
        Map = companies => from company in companies
                           select new VCompany
                           {
                               Id = company.Id,
                               Name = company.Name,
                               Website = company.Website,
                               EmployeeCount = company.EmployeeCount
                           };

        Index(nameof(VCompany.Name), FieldIndexing.Search);
        StoreAllFields(FieldStorage.Yes);
    }
}
