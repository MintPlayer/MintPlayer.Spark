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
                            FullNameSort = person.FirstName + " " + person.LastName,
                            Email = person.Email,
                            Company = person.Company,
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

    /// <summary>
    /// Sort companion for <c>FullName</c>. The base field is analyzed for search, which tokenizes it, so a
    /// person's name — always containing a space — cannot be ordered on. This carries the same value with no
    /// indexing declared, which keeps it a single un-tokenized term.
    /// </summary>
    [IgnoreProperty]
    public string FullNameSort { get; set; } = string.Empty;
    public string? Email { get; set; }
    [Reference(typeof(Company))]
    public string? Company { get; set; }
}
