using DemoApp.Data;
using DemoApp.Library.Entities;
using Raven.Client.Documents.Indexes;

namespace DemoApp.Indexes;

/// <summary>
/// RavenDB index that projects Person documents to VPerson view models.
/// Computes the FullName property from FirstName and LastName.
/// </summary>
public class People_Overview : AbstractIndexCreationTask<Person> // , VPerson
{
    public People_Overview()
    {
        Map = people => from person in people
                        select new VPerson
                        {
                            Id = person.Id,
                            FirstName = person.FirstName,
                            LastName = person.LastName,
                            FullName = person.FirstName + " " + person.LastName,
                            Email = person.Email,
                            IsActive = person.IsActive
                        };

        // Enable full-text search on common fields
        Index(nameof(VPerson.FullName), FieldIndexing.Search);
        Index(nameof(VPerson.Email), FieldIndexing.Search);

        // Store all fields for projection
        StoreAllFields(FieldStorage.Yes);
    }
}
