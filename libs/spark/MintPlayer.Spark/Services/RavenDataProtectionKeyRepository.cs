using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Persists the ASP.NET Core Data Protection key ring in RavenDB, as documents under
/// <c>DataProtectionKeys/</c> — selected by <c>Spark:DataProtection:Storage=RavenDb</c>.
/// </summary>
/// <remarks>
/// <para>
/// Lifted from CodeCoverage (#460, D5), which wrote it after every redeploy minted a fresh key ring
/// in the container filesystem and signed all users out. ⚠️ The id prefix and the document shape
/// (<see cref="KeyDocument"/> with a single <c>Xml</c> string) are <b>production data</b>:
/// coverage.mintplayer.com's key ring is stored in exactly this form, and changing either would
/// make the existing keys unreadable and sign every user out once. The nested type keeps its name
/// so new documents land in the same <c>KeyDocuments</c> collection as the old ones.
/// </para>
/// <para>
/// Reads use <c>LoadStartingWith</c> — an ACID prefix load, no index involved — so a freshly
/// stored key is never invisible to a subsequent read. Keys are not additionally encrypted at rest,
/// the same posture as the file-system repository: whoever can read the database can read the keys.
/// </para>
/// </remarks>
internal sealed class RavenDataProtectionKeyRepository(IDocumentStore store) : IXmlRepository
{
    /// <summary>The document id prefix. Production data — see the remarks.</summary>
    internal const string IdPrefix = "DataProtectionKeys/";

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

    /// <summary>One key-ring element. Shape and name are production data — see the class remarks.</summary>
    internal sealed class KeyDocument
    {
        public string? Id { get; set; }
        public string Xml { get; set; } = string.Empty;
    }
}
