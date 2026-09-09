using CodeCoverage.Actions;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Exceptions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.ApiTokens;

/// <summary>
/// An upload token may only be scoped to repositories the caller actually manages.
/// </summary>
/// <remarks>
/// ⚠️ <b>Nothing else enforces this.</b> A reference ARRAY is written straight through —
/// <c>EntityMapper</c> hands the posted value to the property and returns, bypassing the collection
/// guard that binds a scalar reference's id to its type — so whatever ids the client posts are what
/// get stored. And <c>EnsureRowSaveAllowedAsync</c> re-applies only the <c>AccountLogin</c> row
/// filter, which says nothing about repository ownership.
/// <para>
/// So the check in <c>OnBeforeSaveAsync</c> is the entire defence, on a production credential path.
/// Delete it and every test in this file must go red; if any stays green it was not testing the
/// guard.
/// </para>
/// </remarks>
public class ApiTokenRepositoryScopeTests : CoverageRavenTest
{
    private const string Managed = "acme";
    private const string Foreign = "other";

    private static ApiTokenActions CreateActions(IAsyncDocumentSession session, params string[] owners)
    {
        var visibility = Substitute.For<ISparkVisibility>();
        visibility.GetAllowedOwnersAsync().Returns(Task.FromResult(owners));
        visibility.CanManageOwnerAsync(Arg.Any<string>())
            .Returns(call => Task.FromResult(owners.Contains(call.Arg<string>(), StringComparer.OrdinalIgnoreCase)));

        var ctor = typeof(ApiTokenActions).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p =>
            {
                if (p.ParameterType == typeof(ISparkVisibility)) return visibility;
                if (p.ParameterType == typeof(IAsyncDocumentSession)) return session;
                // Explicit: the create path reads HttpContext?.User, so this must be a real
                // substitute rather than the null the catch below would supply.
                if (p.ParameterType == typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor))
                {
                    // ⚠️ HttpContext must be explicitly null. NSubstitute auto-substitutes it
                    // otherwise (it is an abstract class with an accessible constructor), which
                    // makes 'principal is null' false and sends the create path into UserManager --
                    // a concrete class that cannot be proxied and is therefore null here.
                    var accessor = Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
                    accessor.HttpContext.Returns((Microsoft.AspNetCore.Http.HttpContext?)null);
                    return accessor;
                }
                try { return Substitute.For([p.ParameterType], null); }
                catch { return null; }
            })
            .ToArray();

        var unsupplied = ctor.GetParameters()
            .Where((p, i) => args[i] is null && !p.ParameterType.Name.StartsWith("UserManager"))
            .Select(p => p.ParameterType.Name)
            .ToArray();
        if (unsupplied.Length > 0)
            throw new InvalidOperationException("Could not supply: " + string.Join(", ", unsupplied));

        return (ApiTokenActions)ctor.Invoke(args);
    }

    private static async Task SeedAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { GitHubId = 1, Login = Managed }, Account.DocumentId(1));
        await session.StoreAsync(new Repository
        {
            GitHubId = 10, Name = "mine", FullName = $"{Managed}/mine", OwnerLogin = Managed,
        }, Repository.DocumentId(10));
        await session.StoreAsync(new Repository
        {
            GitHubId = 11, Name = "also-mine", FullName = $"{Managed}/also-mine", OwnerLogin = Managed,
        }, Repository.DocumentId(11));
        await session.StoreAsync(new Repository
        {
            GitHubId = 20, Name = "theirs", FullName = $"{Foreign}/theirs", OwnerLogin = Foreign,
        }, Repository.DocumentId(20));
        await session.SaveChangesAsync();
    }


    /// <summary>A minimal PO; the hook under test reads the entity, not the PO.</summary>
    private static PersistentObject Po() => new() { Name = "ApiToken", ObjectTypeId = Guid.NewGuid() };

    private static ApiToken NewToken(params string[] repositoryIds) => new()
    {
        AccountLogin = Managed,
        Description = "ci",
        GithubRepositories = [.. repositoryIds],
    };

    /// <summary>The escalation attempt: a token scoped to a repository the caller does not manage.</summary>
    [Fact]
    public async Task A_repository_the_caller_does_not_manage_is_refused()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions(session, Managed);
        var token = NewToken(Repository.DocumentId(20));

        var act = async () => await actions.OnBeforeSaveAsync(Po(), token);

        await act.Should().ThrowAsync<SparkValidationException>();
    }

    /// <summary>
    /// One foreign id among managed ones must refuse the whole save — a partial accept would store
    /// the very id that was refused.
    /// </summary>
    [Fact]
    public async Task One_foreign_repository_refuses_the_whole_token()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions(session, Managed);
        var token = NewToken(Repository.DocumentId(10), Repository.DocumentId(20));

        var act = async () => await actions.OnBeforeSaveAsync(Po(), token);

        await act.Should().ThrowAsync<SparkValidationException>();
    }

    /// <summary>
    /// An id that resolves to nothing is refused exactly as an unauthorized one is, so the error
    /// cannot be used to discover which repository ids exist.
    /// </summary>
    [Fact]
    public async Task An_unknown_repository_id_is_refused_like_an_unauthorized_one()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions(session, Managed);

        var unknown = await Record.ExceptionAsync(() =>
            actions.OnBeforeSaveAsync(Po(), NewToken("Repositories/999999")));
        var foreignId = await Record.ExceptionAsync(() =>
            actions.OnBeforeSaveAsync(Po(), NewToken(Repository.DocumentId(20))));

        unknown.Should().BeOfType<SparkValidationException>();
        foreignId.Should().BeOfType<SparkValidationException>();
        unknown!.Message.Should().Be(foreignId!.Message, "unknown and unauthorized must be indistinguishable");
    }

    [Fact]
    public async Task Managed_repositories_are_accepted_and_scope_becomes_Repository()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions(session, Managed);
        var token = NewToken(Repository.DocumentId(10), Repository.DocumentId(11));

        await actions.OnBeforeSaveAsync(Po(), token);

        token.GithubRepositories.Should().HaveCount(2);
        token.Scope.Should().Be("Repository");
    }

    [Fact]
    public async Task An_empty_list_is_account_scoped()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions(session, Managed);
        var token = NewToken();

        await actions.OnBeforeSaveAsync(Po(), token);

        token.Scope.Should().Be("Account");
    }

    [Fact]
    public async Task Duplicate_ids_are_collapsed()
    {
        // A repeated id would otherwise become a repeated claim on the wire.
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions(session, Managed);
        var token = NewToken(Repository.DocumentId(10), Repository.DocumentId(10));

        await actions.OnBeforeSaveAsync(Po(), token);

        token.GithubRepositories.Should().Equal(Repository.DocumentId(10));
    }

    /// <summary>
    /// ⚠️ The case the original hook could not have caught: it returned early on an edit
    /// (<c>if (!string.IsNullOrEmpty(entity.Hash)) return;</c>), which would have left the scope of
    /// an EXISTING token entirely unguarded — the easier attack, since the token already exists.
    /// </summary>
    [Fact]
    public async Task An_edit_is_validated_too_not_only_a_create()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions(session, Managed);

        var existing = NewToken(Repository.DocumentId(20));
        existing.Hash = "already-minted";   // an edit: the credential exists

        var act = async () => await actions.OnBeforeSaveAsync(Po(), existing);

        await act.Should().ThrowAsync<SparkValidationException>();
    }
}
