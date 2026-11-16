using CodeCoverage.Actions;
using MintPlayer.Spark.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>#649 D12: the "SDK from global.json" checkbox follows the form on every refresh.</summary>
public class SetUpFeaturesActionsTests
{
    private static PersistentObject Form(bool dotnet, bool dependencyUpdates, bool releaseLines, bool sdk) => new()
    {
        Name = "SetUpFeatures",
        ObjectTypeId = Guid.NewGuid(),
        Attributes =
        [
            new PersistentObjectAttribute { Name = "DependencyUpdates", DataType = "boolean", Value = dependencyUpdates },
            new PersistentObjectAttribute { Name = "ReleaseLines", DataType = "boolean", Value = releaseLines },
            new PersistentObjectAttribute { Name = "SdkFromGlobalJson", DataType = "boolean", Value = sdk, IsReadOnly = true, ShowedOn = EShowedOn.None },
            new PersistentObjectAttribute { Name = SetUpFeaturesActions.DotnetRepository, DataType = "boolean", Value = dotnet, IsReadOnly = true, ShowedOn = EShowedOn.None },
        ],
    };

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void A_net_repository_shows_the_checkbox_enabled_only_with_dependency_updates_or_release_lines(bool dependencyUpdates, bool releaseLines, bool enabled)
    {
        var form = Form(dotnet: true, dependencyUpdates, releaseLines, sdk: true);

        SetUpFeaturesActions.Shape(form);

        form["SdkFromGlobalJson"].ShowedOn.Should().Be(EShowedOn.PersistentObject);
        form["SdkFromGlobalJson"].IsReadOnly.Should().Be(!enabled);
        form["SdkFromGlobalJson"].GetValue<bool>().Should().Be(enabled, "a disabled checkbox is unticked");
    }

    [Fact]
    public void Another_repository_never_shows_it()
    {
        var form = Form(dotnet: false, dependencyUpdates: true, releaseLines: true, sdk: false);

        SetUpFeaturesActions.Shape(form);

        form["SdkFromGlobalJson"].ShowedOn.Should().Be(EShowedOn.None);
    }
}
