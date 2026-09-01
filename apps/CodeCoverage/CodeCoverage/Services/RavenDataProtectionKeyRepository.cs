using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Raven.Client.Documents;

namespace CodeCoverage.Services;

/// <summary>
/// Persists the ASP.NET data-protection key ring in RavenDB (documents under
/// "DataProtectionKeys/"). The default keystore is the container filesystem,
/// so every redeploy minted a fresh key ring and invalidated every auth and
/// antiforgery cookie — signing all users out. Storing the keys next to the
/// rest of the state means they live in the raven-data volume and survive
/// container replacement.
///
/// Reads use LoadStartingWith (an ACID prefix load, no index involved), so a
/// freshly stored key is never invisible to a subsequent read. Keys are not
/// additionally encrypted at rest — same posture as the filesystem default;
/// RavenDB is reachable only on the internal compose network, and Spark's
/// generic data endpoints are DenyAll.
/// </summary>
public sealed class RavenDataProtectionKeyRepository : IXmlRepository
{
    private const string IdPrefix = "DataProtectionKeys/";

    private readonly IDocumentStore store;

    public RavenDataProtectionKeyRepository(IDocumentStore store) => this.store = store;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var session = store.OpenSession();
        return session.Advanced.LoadStartingWith<KeyDocument>(IdPrefix, pageSize: 1024)
            .Select(d => XElement.Parse(d.Xml))
            .ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        var name = string.IsNullOrEmpty(friendlyName) ? Guid.NewGuid().ToString("N") : friendlyName;
        using var session = store.OpenSession();
        session.Store(new KeyDocument { Xml = element.ToString(SaveOptions.DisableFormatting) }, IdPrefix + name);
        session.SaveChanges();
    }

    public sealed class KeyDocument
    {
        public string? Id { get; set; }
        public string Xml { get; set; } = string.Empty;
    }
}
