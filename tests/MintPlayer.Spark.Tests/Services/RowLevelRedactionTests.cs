using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// M4 (#236) — per-viewer attribute redaction: <c>GetProtectedAttributesAsync</c> names the
/// attributes of a specific row this caller must not see, and the framework nulls them out of the
/// mapped payload. Redacts rather than omits (dropping the attribute would break name-indexed
/// clients and leak the rule via schema mismatch), shields the same attributes from write-back,
/// and reaches into AsDetail children via dotted names — the one place a row filter can't.
/// The motivating case is Coverage's <c>Repository.BadgeToken</c>: readable row, secret field.
/// </summary>
public class RowLevelRedactionTests : SparkTestDriver
{
    private static readonly Guid RepoTypeId = Guid.Parse("e9e9e9e9-9999-9999-9999-e9e9e9e9e9e9");

    public class Repo
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string BadgeToken { get; set; } = string.Empty;
    }

    /// <summary>Stands in for an index projection over Repo.</summary>
    public class VRepo
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string BadgeToken { get; set; } = string.Empty;
    }

    /// <summary>alice manages her own repos and may see their tokens; every other row's token is redacted.</summary>
    public class RepoActions : DefaultPersistentObjectActions<Repo>
    {
        public RepoActions(IEntityMapper entityMapper, IHttpContextAccessor? accessor = null)
            : base(entityMapper, accessor) { }

        public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Repo entity)
            => Task.FromResult<IReadOnlyCollection<string>?>(
                entity.Owner == "alice" ? null : ["BadgeToken"]);
    }

    /// <summary>Protects a column inside the embedded AsDetail rows.</summary>
    public class DottedRepoActions : DefaultPersistentObjectActions<Repo>
    {
        public DottedRepoActions(IEntityMapper entityMapper) : base(entityMapper) { }

        public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Repo entity)
            => Task.FromResult<IReadOnlyCollection<string>?>(["Jobs.Salary"]);
    }

    private static IModelLoader CreateModelLoader()
    {
        var repoDef = new EntityTypeDefinition
        {
            Id = RepoTypeId,
            Name = "Repo",
            ClrType = typeof(Repo).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 1 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Owner", DataType = "string", Order = 2 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "BadgeToken", DataType = "string", Order = 3 },
            ],
            Breadcrumb = "{Name}",
        };
        var modelLoader = Substitute.For<IModelLoader>();
        modelLoader.GetEntityType(RepoTypeId).Returns(repoDef);
        modelLoader.GetEntityTypeByClrType(typeof(Repo).FullName!).Returns(repoDef);
        return modelLoader;
    }

    private static (RowSecurity RowSecurity, EntityMapper Mapper) CreateSubjects(IHttpContextAccessor? accessor = null)
    {
        var modelLoader = CreateModelLoader();
        var mapper = new EntityMapper(modelLoader);
        var actionsResolver = Substitute.For<IActionsResolver>();
        actionsResolver.ResolveForType(typeof(Repo)).Returns(new RepoActions(mapper));
        return (new RowSecurity(actionsResolver, null, accessor), mapper);
    }

    [Fact]
    public async Task A_protected_attribute_is_redacted_for_this_row_and_kept_for_that_one()
    {
        var (rowSecurity, mapper) = CreateSubjects();
        var mine = new Repo { Id = "repos/1", Name = "mine", Owner = "alice", BadgeToken = "tok-1" };
        var foreign = new Repo { Id = "repos/2", Name = "public", Owner = "bob", BadgeToken = "tok-2" };

        var minePo = mapper.ToPersistentObject(mine, RepoTypeId);
        var foreignPo = mapper.ToPersistentObject(foreign, RepoTypeId);

        using var session = Store.OpenAsyncSession();
        await rowSecurity.RedactAsync(
            session, [(minePo, mine), (foreignPo, foreign)], typeof(Repo), typeof(Repo), "Query");

        minePo["BadgeToken"].Value.Should().Be("tok-1", "the caller manages this row");
        foreignPo["BadgeToken"].Value.Should().BeNull("redaction is per row, per caller");
        foreignPo["BadgeToken"].IsVisible.Should().BeFalse();
        foreignPo.Attributes.Should().Contain(a => a.Name == "BadgeToken",
            "redact, don't omit — dropping the attribute breaks name-indexed clients and leaks the rule");
        foreignPo["Name"].Value.Should().Be("public", "only the named attributes are touched");
    }

    [Fact]
    public async Task Projection_rows_are_judged_against_their_base_documents()
    {
        string mineId, foreignId;
        using (var session = Store.OpenAsyncSession())
        {
            var mine = new Repo { Name = "mine", Owner = "alice", BadgeToken = "tok-1" };
            var foreign = new Repo { Name = "public", Owner = "bob", BadgeToken = "tok-2" };
            await session.StoreAsync(mine);
            await session.StoreAsync(foreign);
            await session.SaveChangesAsync();
            mineId = mine.Id!;
            foreignId = foreign.Id!;
        }

        var (rowSecurity, _) = CreateSubjects();
        var minePo = ProjectionPo(mineId, "tok-1");
        var foreignPo = ProjectionPo(foreignId, "tok-2");
        var mineRow = new VRepo { Id = mineId, BadgeToken = "tok-1" };
        var foreignRow = new VRepo { Id = foreignId, BadgeToken = "tok-2" };

        using var redactSession = Store.OpenAsyncSession();
        await rowSecurity.RedactAsync(
            redactSession,
            [(minePo, mineRow), (foreignPo, foreignRow)],
            typeof(Repo), typeof(VRepo), "Query");

        minePo["BadgeToken"].Value.Should().Be("tok-1");
        foreignPo["BadgeToken"].Value.Should().BeNull(
            "the projection carries the token, but the rule is asked against the base document");
        redactSession.Advanced.NumberOfRequests.Should().Be(1, "base documents load in one batch");
    }

    [Fact]
    public async Task A_projected_row_with_no_base_document_gets_everything_redacted()
    {
        var (rowSecurity, _) = CreateSubjects();
        var po = ProjectionPo("repos/gone", "tok-x");
        var row = new VRepo { Id = "repos/gone", BadgeToken = "tok-x" };

        using var session = Store.OpenAsyncSession();
        await rowSecurity.RedactAsync(session, [(po, row)], typeof(Repo), typeof(VRepo), "Query");

        po.Attributes.Should().OnlyContain(a => a.Value == null && !a.IsVisible,
            "the rule can't be asked without the document — unverifiable is not shown");
    }

    [Fact]
    public async Task A_type_without_the_hook_is_untouched()
    {
        var modelLoader = CreateModelLoader();
        var mapper = new EntityMapper(modelLoader);
        var actionsResolver = Substitute.For<IActionsResolver>();
        actionsResolver.ResolveForType(typeof(Repo))
            .Returns(new DefaultPersistentObjectActions<Repo>(mapper));
        var rowSecurity = new RowSecurity(actionsResolver);

        var repo = new Repo { Id = "repos/1", Name = "n", Owner = "bob", BadgeToken = "tok" };
        var po = mapper.ToPersistentObject(repo, RepoTypeId);

        using var session = Store.OpenAsyncSession();
        await rowSecurity.RedactAsync(session, [(po, repo)], typeof(Repo), typeof(Repo), "Query");

        po["BadgeToken"].Value.Should().Be("tok");
        session.Advanced.NumberOfRequests.Should().Be(0, "no hook, no work");
    }

    [Fact]
    public async Task A_dotted_name_redacts_a_column_inside_AsDetail_children()
    {
        var modelLoader = CreateModelLoader();
        var mapper = new EntityMapper(modelLoader);
        var actionsResolver = Substitute.For<IActionsResolver>();
        actionsResolver.ResolveForType(typeof(Repo)).Returns(new DottedRepoActions(mapper));
        var rowSecurity = new RowSecurity(actionsResolver);

        var child = new PersistentObject
        {
            Name = "Job",
            ObjectTypeId = Guid.NewGuid(),
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Title", Value = "Dev" },
                new PersistentObjectAttribute { Name = "Salary", Value = 100_000 },
            ],
        };
        var po = new PersistentObject
        {
            Name = "Repo",
            ObjectTypeId = RepoTypeId,
            Attributes =
            [
                new PersistentObjectAttributeAsDetail
                {
                    Name = "Jobs", DataType = "AsDetail", IsArray = true, Objects = [child],
                },
            ],
        };

        var repo = new Repo { Id = "repos/1", Owner = "bob" };
        using var session = Store.OpenAsyncSession();
        await rowSecurity.RedactAsync(session, [(po, repo)], typeof(Repo), typeof(Repo), "Query");

        child["Salary"].Value.Should().BeNull(
            "embedded rows aren't rows — the row filter can't reach them, redaction must");
        child["Salary"].IsVisible.Should().BeFalse();
        child["Title"].Value.Should().Be("Dev");
    }

    [Fact]
    public async Task A_redacted_attribute_cannot_be_written_back_over_the_secret()
    {
        string repoId;
        using (var session = Store.OpenAsyncSession())
        {
            var repo = new Repo { Name = "public", Owner = "bob", BadgeToken = "the-secret" };
            await session.StoreAsync(repo);
            await session.SaveChangesAsync();
            repoId = repo.Id!;
        }

        var actions = new RepoActions(new EntityMapper(CreateModelLoader()));
        var po = new PersistentObject
        {
            Id = repoId,
            ObjectTypeId = RepoTypeId,
            Name = "Repo",
            Attributes =
            [
                new() { Name = "Name", DataType = "string", Value = "renamed", IsValueChanged = true },
                new() { Name = "Owner", DataType = "string", Value = "bob", IsValueChanged = false },
                new() { Name = "BadgeToken", DataType = "string", Value = "hijacked", IsValueChanged = true },
            ],
        };

        using var editSession = Store.OpenAsyncSession();
        var saved = await actions.OnSaveAsync(editSession, po);

        saved.BadgeToken.Should().Be("the-secret",
            "a client that received a redacted value (or a malicious one) must not clobber the "
            + "stored secret on write-back");
        saved.Name.Should().Be("renamed", "unprotected attributes still merge normally");
    }

    [Fact]
    public async Task The_system_context_sees_full_values()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(SparkSystemContext.ClaimType, "module")], "test")),
        };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);

        var (rowSecurity, mapper) = CreateSubjects(accessor);
        var foreign = new Repo { Id = "repos/2", Name = "public", Owner = "bob", BadgeToken = "tok-2" };
        var po = mapper.ToPersistentObject(foreign, RepoTypeId);

        using var session = Store.OpenAsyncSession();
        await rowSecurity.RedactAsync(session, [(po, foreign)], typeof(Repo), typeof(Repo), "Query");

        po["BadgeToken"].Value.Should().Be("tok-2", "sync must replicate full values");
    }

    private static PersistentObject ProjectionPo(string id, string token) => new()
    {
        Id = id,
        ObjectTypeId = RepoTypeId,
        Name = "Repo",
        Attributes =
        [
            new() { Name = "Name", Value = "x" },
            new() { Name = "BadgeToken", Value = token },
        ],
    };
}
