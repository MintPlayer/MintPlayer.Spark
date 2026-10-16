using CodeCoverage.Entities;
using CodeCoverage.Services;
using NSubstitute;

namespace CodeCoverage.Tests;

/// <summary>Helpers for <see cref="ISparkVisibility"/> substitutes.</summary>
internal static class SparkVisibilityDoubles
{
    /// <summary>
    /// The "manages" questions (R39, D-O12) answered from the substitute's own <see cref="ISparkVisibility.GetAllowedOwnersAsync"/>
    /// and <see cref="ISparkVisibility.CanManageOwnerAsync"/>, whatever they are configured to later: every visible owner is a
    /// managed one, and nobody administers a single repository. What the tests written before the two questions split assumed.
    /// </summary>
    public static ISparkVisibility ManagingWhatItSees(this ISparkVisibility visibility)
    {
        visibility.GetManagedOwnersAsync().Returns(_ => visibility.GetAllowedOwnersAsync());
        visibility.CanManageRepositoryAsync(Arg.Any<Repository>())
            .Returns(call => visibility.CanManageOwnerAsync(call.Arg<Repository>().OwnerKey));
        visibility.GetAdministeredRepositoryIdsAsync().Returns(Task.FromResult(Array.Empty<string>()));
        return visibility;
    }
}
