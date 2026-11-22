using CodeCoverage.CustomActions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Files.Storage;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>
/// The Administrators' "Remove custom avatar" (#700 M11, PRD Q15): the account falls back to the forge's avatar and the bytes are
/// erased, only after the clear committed. Who may run it is <c>security.json</c>'s (<c>ClearAvatar/Account</c>, Administrators).
/// </summary>
public class ClearAvatarActionTests : CoverageRavenTest
{
    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 1);
    private static readonly Guid AccountType = AccountAvatar.AccountType;

    private sealed record Harness(ClearAvatarAction Action, IFileStore Files, IClientAccessor Client);

    private static Harness CreateAction(IAsyncDocumentSession session)
    {
        var client = Substitute.For<IClientAccessor>();
        var manager = Substitute.For<IManager>();
        manager.Client.Returns(client);
        var files = Substitute.For<IFileStore>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        services.AddSingleton(manager);
        services.AddSingleton(files);
        services.AddScoped<ClearAvatarAction>();
        return new Harness(services.BuildServiceProvider().GetRequiredService<ClearAvatarAction>(), files, client);
    }

    private static SparkImage Avatar() => new()
    {
        FileId = "f-1", FileName = "me.png", ContentType = "image/png", ScanStatus = SparkFileScanStatus.Clean,
        Variants = { ["thumb"] = new SparkImageVariant { FileId = "f-1.thumb", ContentType = "image/webp" } },
    };

    private static async Task SeedAsync(IDocumentStore store, SparkImage? avatar)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { GitHubId = 1, Login = "acme", AvatarUrl = "https://avatars.example/acme.png", CustomAvatar = avatar }, AccountId);
        await session.StoreAsync(new Repository { GitHubId = 9, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme" }, "Repositories/9");
        await session.SaveChangesAsync();
    }

    private static CustomActionArgs ArgsFor(string id, Guid type) => new()
    {
        Parent = new PersistentObject { Id = id, Name = "Account", ObjectTypeId = type },
    };

    private static async Task<Account> LoadAsync(IDocumentStore store)
    {
        using var verify = store.OpenAsyncSession();
        return await verify.LoadAsync<Account>(AccountId);
    }

    [Fact]
    public async Task Clears_the_avatar_and_erases_its_bytes()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, Avatar());

        Harness harness;
        using (var session = store.OpenAsyncSession())
        {
            harness = CreateAction(session);
            await harness.Action.ExecuteAsync(ArgsFor(AccountId, AccountType));
        }

        var account = await LoadAsync(store);
        account.CustomAvatar.Should().BeNull();
        AccountAvatar.Resolve(account).Should().Be("https://avatars.example/acme.png");
        await harness.Files.Received(1).EraseAsync(AccountId, "f-1", Arg.Any<CancellationToken>());
    }

    /// <summary>A failed erase after the commit leaves unreferenced bytes for the reconcile, never an account pointing at nothing.</summary>
    [Fact]
    public async Task A_failed_erase_still_leaves_the_avatar_cleared()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, Avatar());

        using (var session = store.OpenAsyncSession())
        {
            var harness = CreateAction(session);
            harness.Files.EraseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("store down")));
            await harness.Action.ExecuteAsync(ArgsFor(AccountId, AccountType));
        }

        (await LoadAsync(store)).CustomAvatar.Should().BeNull();
    }

    [Fact]
    public async Task An_account_without_a_custom_avatar_is_left_alone()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, avatar: null);

        using var session = store.OpenAsyncSession();
        var harness = CreateAction(session);
        await harness.Action.ExecuteAsync(ArgsFor(AccountId, AccountType));

        await harness.Files.DidNotReceiveWithAnyArgs().EraseAsync(default!, default!, default);
        harness.Client.Received(1).Notify(Arg.Is<string>(m => m.Contains("no custom avatar")), NotificationKind.Info);
    }

    /// <summary>The parent's type decides, not its id: a load by id alone would read any document as an account.</summary>
    [Fact]
    public async Task A_parent_of_another_type_is_refused()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, Avatar());

        using (var session = store.OpenAsyncSession())
        {
            var harness = CreateAction(session);
            await harness.Action.ExecuteAsync(ArgsFor(AccountId, Guid.NewGuid()));
            await harness.Files.DidNotReceiveWithAnyArgs().EraseAsync(default!, default!, default);
        }

        (await LoadAsync(store)).CustomAvatar.Should().NotBeNull();
    }
}
