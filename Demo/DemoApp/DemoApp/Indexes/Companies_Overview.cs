using DemoApp.Library.Entities;
using Raven.Client.Documents.Indexes;

namespace DemoApp.Indexes;

public partial class Companies_Overview : AbstractIndexCreationTask<Company>
{
    public Companies_Overview()
    {
        Map = companies => from company in companies
                           select new VCompany
                           {
                               Id = company.Id,
                               Name = company.Name,
                               NameSort = company.Name,
                               Website = company.Website,
                               EmployeeCount = company.EmployeeCount
                           };

        // Applies the indexing declared by [Search]; generated from the attributes.
        IndexSearchFields();
        StoreAllFields(FieldStorage.Yes);
    }
}
