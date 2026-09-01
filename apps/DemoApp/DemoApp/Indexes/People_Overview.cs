using DemoApp.Library.Entities;
using Raven.Client.Documents.Indexes;

namespace DemoApp.Indexes;

/// <summary>
/// RavenDB index that projects Person documents to VPerson view models.
/// Computes the FullName property from FirstName and LastName.
/// </summary>
public partial class People_Overview : AbstractIndexCreationTask<Person> // , VPerson
{
    public People_Overview()
    {
        Map = people => from person in people
                        select new VPerson
                        {
                            Id = person.Id,
                            FullName = person.FirstName + " " + person.LastName,
                            FullNameSort = person.FirstName + " " + person.LastName,
                            Email = person.Email,
                            EmailSort = person.Email,
                            IsActive = person.IsActive,
                            Company = person.Company,
                        };

        // Enable full-text search on common fields
        // Applies the indexing declared by [Search]; generated from the attributes.
        IndexSearchFields();

        // Store all fields for projection
        StoreAllFields(FieldStorage.Yes);
    }
}
