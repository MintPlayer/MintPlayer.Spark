using CodeCoverage.PackageVersioning;
using MintPlayer.Spark.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// #153 M12 (PRD D16): a forge without <c>IForgeVersioning</c> draws none of the versioning policies, and leaves every
/// other attribute as the model has it.
/// </summary>
public class VersioningFormTests
{
    private static PersistentObject Form() => new()
    {
        Name = "Repository",
        ObjectTypeId = Guid.NewGuid(),
        Attributes =
        [
            new PersistentObjectAttribute { Name = "PrTitleCorrection" },
            new PersistentObjectAttribute { Name = "ReleaseOnTag" },
            new PersistentObjectAttribute { Name = "AutoCutReleaseBranches" },
            new PersistentObjectAttribute { Name = "PrSizeLabels" },
        ],
    };

    [Fact]
    public void A_forge_without_the_capability_hides_the_policies_and_makes_them_read_only()
    {
        var po = Form();

        VersioningForm.Shape(po, supported: false);

        po.Attributes.Where(a => VersioningForm.Policies.Contains(a.Name))
            .Should().HaveCount(3).And.OnlyContain(a => a.ShowedOn == EShowedOn.None && a.IsReadOnly);
        var other = po.Attributes.Single(a => a.Name == "PrSizeLabels");
        other.ShowedOn.Should().Be(EShowedOn.Query | EShowedOn.PersistentObject);
        other.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void A_forge_with_the_capability_keeps_the_model_layout()
    {
        var po = Form();

        VersioningForm.Shape(po, supported: true);

        po.Attributes.Should().OnlyContain(a => a.ShowedOn == (EShowedOn.Query | EShowedOn.PersistentObject) && !a.IsReadOnly);
    }
}
