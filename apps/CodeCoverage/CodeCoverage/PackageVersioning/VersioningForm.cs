using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// How the versioning policies read on the Account and Repository pages (#153 PRD D16): a forge without
/// <see cref="Forge.IForgeVersioning"/> draws none of them. Unlike the PR size labels, nothing is said about why: the
/// policies would do nothing on that forge, so the page leaves them out.
/// </summary>
/// <remarks>
/// <c>ShowedOn</c> is layout only, so each attribute is made read-only too. A value posted anyway is harmless: no
/// versioning recipient runs for a forge without the capability.
/// </remarks>
public static class VersioningForm
{
    public static readonly string[] Policies =
        [nameof(Repository.PrTitleCorrection), nameof(Repository.ReleaseOnTag), nameof(Repository.AutoCutReleaseBranches)];

    public static void Shape(PersistentObject po, bool supported)
    {
        if (supported) return;
        foreach (var attribute in po.Attributes.Where(a => Policies.Contains(a.Name)))
        {
            attribute.IsReadOnly = true;
            attribute.ShowedOn = EShowedOn.None;
        }
    }
}
