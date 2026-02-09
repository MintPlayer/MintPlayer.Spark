using HR.Entities;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;

namespace HR.Indexes;

public class People_Overview : AbstractIndexCreationTask<Person>
{
    public People_Overview()
    {
        Map = people => from person in people
                        select new VPerson
                        {
                            Id = person.Id,
                            FullName = person.FirstName + " " + person.LastName,
                            Email = person.Email,
                        };

        Index(nameof(VPerson.FullName), FieldIndexing.Search);
        StoreAllFields(FieldStorage.Yes);
    }
}

[FromIndex(typeof(People_Overview))]
public class VPerson
{
    public string? Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
}
