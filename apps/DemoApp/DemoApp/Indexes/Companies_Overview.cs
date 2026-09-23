using DemoApp.Library.Entities;
using Raven.Client.Documents.Indexes;
using MintPlayer.Spark;

namespace DemoApp.Indexes;

public partial class Companies_Overview : SparkIndexCreationTask<Company>
{
    public Companies_Overview()
    {
        Map = companies => from company in companies
                           select new VCompany
                           {
                               Id = company.Id,
                               Name = company.Name,
                               NameSearch = company.Name,
                               Website = company.Website,
                               EmployeeCount = company.EmployeeCount
                           };

        StoreAllFields(FieldStorage.Yes);
    }
}
