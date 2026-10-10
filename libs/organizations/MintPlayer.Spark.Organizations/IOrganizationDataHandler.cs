using MintPlayer.Spark.Organizations.Models;

namespace MintPlayer.Spark.Organizations;

/// <summary>
/// The application's data owned by an organization (<c>docs/seo_analyzer_PRD.md</c> D26): what goes when
/// the organization is deleted, and what an Owner's data export carries. Register one per kind of data,
/// <c>services.AddScoped&lt;IOrganizationDataHandler, SiteData&gt;()</c>; every registration runs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DeleteAsync"/> runs <b>before</b> the package deletes the organization, its memberships
/// and its invitations, in registration order. A handler that throws stops the deletion with the
/// organization intact, so a retry runs every handler again: make it idempotent (delete what is there).
/// Large sets belong in a <c>DeleteByQueryOperation</c>, not a session.
/// </para>
/// <para>
/// <see cref="ExportAsync"/> is asked for every organization the exporting user owns (their personal
/// one included), and its result is serialized under <see cref="Name"/> in the account's
/// <c>personal-data</c> export (<c>GET /spark/auth/manage/personal-data</c>, section <c>organizations</c>).
/// </para>
/// </remarks>
public interface IOrganizationDataHandler
{
    /// <summary>The key of this handler's data in an export. Unique per application.</summary>
    string Name { get; }

    /// <summary>The organization's data of this kind, for an export; <see langword="null"/> omits it.</summary>
    Task<object?> ExportAsync(Organization organization, CancellationToken cancellationToken);

    /// <summary>Deletes the organization's data of this kind. Idempotent.</summary>
    Task DeleteAsync(Organization organization, CancellationToken cancellationToken);
}
