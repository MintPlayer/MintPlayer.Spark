using HR.Entities;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;

namespace HR.Indexes;

public partial class People_Overview : AbstractIndexCreationTask<Person>
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
                            Company = person.Company,
                        };

        // Applies the indexing declared by [Search]; generated from the attributes.
        IndexSearchFields();
        StoreAllFields(FieldStorage.Yes);
    }
}

[FromIndex(typeof(People_Overview))]
public partial class VPerson
{
    public string? Id { get; set; }
    [Search]
    public string FullName { get; set; } = string.Empty;

    public string? Email { get; set; }
    [Reference(typeof(Company))]
    public string? Company { get; set; }
}
