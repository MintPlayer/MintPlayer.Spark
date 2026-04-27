using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// PermissionService is the framework-internal seam between endpoint handlers and the
/// optional <see cref="IAccessControl"/> implementation. Two contracts to pin: when
/// IAccessControl isn't registered (auth package not installed) the service is permissive,
/// and the resource string is built from the action+target pair the same way every caller
/// expects (otherwise security.json rules silently miss).
/// </summary>
public class PermissionServiceTests
{
    private readonly IAccessControl _accessControl = Substitute.For<IAccessControl>();

    [Fact]
    public async Task EnsureAuthorizedAsync_is_a_no_op_when_IAccessControl_is_not_registered()
    {
        var service = new PermissionService(accessControl: null);

        var act = async () => await service.EnsureAuthorizedAsync("Read", "Person");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAuthorizedAsync_returns_when_IAccessControl_allows()
    {
        _accessControl.IsAllowedAsync("Read/Person", Arg.Any<CancellationToken>()).Returns(true);
        var service = new PermissionService(_accessControl);

        var act = async () => await service.EnsureAuthorizedAsync("Read", "Person");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAuthorizedAsync_throws_SparkAccessDeniedException_when_denied()
    {
        _accessControl.IsAllowedAsync("Read/Person", Arg.Any<CancellationToken>()).Returns(false);
        var service = new PermissionService(_accessControl);

        var act = async () => await service.EnsureAuthorizedAsync("Read", "Person");

        var ex = await act.Should().ThrowAsync<SparkAccessDeniedException>();
        ex.Which.Message.Should().Contain("Read/Person");
    }

    [Fact]
    public async Task EnsureAuthorizedAsync_builds_resource_string_as_action_slash_target()
    {
        // The exact string is the contract the security.json rules match against.
        _accessControl.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = new PermissionService(_accessControl);

        await service.EnsureAuthorizedAsync("EditNewDelete", "DemoApp.Person");

        await _accessControl.Received(1).IsAllowedAsync("EditNewDelete/DemoApp.Person", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsAllowedAsync_returns_true_when_IAccessControl_is_not_registered()
    {
        var service = new PermissionService(accessControl: null);

        (await service.IsAllowedAsync("Read", "Person")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_delegates_to_IAccessControl()
    {
        _accessControl.IsAllowedAsync("Read/Person", Arg.Any<CancellationToken>()).Returns(true);
        _accessControl.IsAllowedAsync("Delete/Person", Arg.Any<CancellationToken>()).Returns(false);
        var service = new PermissionService(_accessControl);

        (await service.IsAllowedAsync("Read", "Person")).Should().BeTrue();
        (await service.IsAllowedAsync("Delete", "Person")).Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_propagates_cancellation_token()
    {
        var cts = new CancellationTokenSource();
        _accessControl.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = new PermissionService(_accessControl);

        await service.IsAllowedAsync("Read", "Person", cts.Token);

        await _accessControl.Received(1).IsAllowedAsync(Arg.Any<string>(), cts.Token);
    }
}
