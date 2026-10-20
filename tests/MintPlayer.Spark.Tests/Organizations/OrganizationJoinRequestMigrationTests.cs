using System.Text;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Migrations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Organizations.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// S22 (<c>docs/commerce_PRD.md</c> §7, §8; D26): <see cref="M_202610191700_OrganizationJoinRequestsToApprovals"/> turns
/// every pending join request stored the pre-D26 way into an <see cref="ApprovalRequest"/>. The seeds are raw documents
/// in exactly the shape <c>SparkOrganizations.RequestToJoinAsync</c> stored before D26 (an <c>OrganizationInvitation</c>
/// with <c>Kind = JoinRequest</c>, the requester fields, the token hash, the CLR type and <c>@expires</c>), written
/// without the current class, which no longer has those fields.
/// </summary>
public class OrganizationJoinRequestMigrationTests : SparkTestDriver
{
    internal const string LegacyClrType = "MintPlayer.Spark.Organizations.Models.OrganizationInvitation, MintPlayer.Spark.Organizations";
    internal const string LegacyToken = "legacy-mailed-token";

    private const string Acme = "Organizations/acme";
    private const string PendingId = "OrganizationInvitations/0a5e0000000040008000000000000001";
    private const string ExpiredId = "OrganizationInvitations/0a5e0000000040008000000000000002";
    private const string OrphanId = "OrganizationInvitations/0a5e0000000040008000000000000003";
    private const string InvitationId = "OrganizationInvitations/0a5e0000000040008000000000000004";

    private static readonly TimeSpan Retention = TimeSpan.FromDays(365);

    /// <summary>A pre-D26 join request, as <c>RequestToJoinAsync</c> stored it (enums by name, dates as ISO-8601 UTC).</summary>
    internal static Task PutLegacyJoinRequestAsync(IDocumentStore store, string id, string organizationId, string requester, DateTime createdAtUtc, DateTime expiresAtUtc, string token = LegacyToken, string role = "Member")
        => PutRawAsync(store, id, new JsonObject
        {
            ["OrganizationId"] = organizationId,
            ["Kind"] = "JoinRequest",
            ["Email"] = requester + "@test.local",
            ["UserId"] = "SparkUsers/" + requester,
            ["UserName"] = requester,
            ["Role"] = role,
            ["TokenHash"] = SparkOrganizations.Hash(token),
            ["CreatedBy"] = "SparkUsers/" + requester,
            ["CreatedAtUtc"] = createdAtUtc.ToString("O"),
            ["ExpiresAtUtc"] = expiresAtUtc.ToString("O"),
            ["@metadata"] = new JsonObject
            {
                ["@collection"] = "OrganizationInvitations",
                ["Raven-Clr-Type"] = LegacyClrType,
                ["@expires"] = expiresAtUtc.ToString("O"),
            },
        });

    /// <summary>Stores <paramref name="document"/> as it is (with its <c>@metadata</c>), bypassing the session's serializer.</summary>
    internal static async Task PutRawAsync(IDocumentStore store, string id, JsonObject document)
    {
        using var session = store.OpenAsyncSession();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        var blittable = await session.Advanced.Context.ReadForMemoryAsync(stream, id, CancellationToken.None);
        await session.Advanced.RequestExecutor.ExecuteAsync(new PutDocumentCommand(store.Conventions, id, null, blittable), session.Advanced.Context);
    }

    private async Task SeedAsync(DateTime now)
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Organization { Id = Acme, Name = "Acme", CreatedBy = "SparkUsers/ann", CreatedAtUtc = now.AddDays(-30) }, Acme);
            foreach (var (user, role) in new[] { ("ann", OrganizationRole.Owner), ("ole", OrganizationRole.Owner), ("ada", OrganizationRole.Admin), ("max", OrganizationRole.Member) })
                await session.StoreAsync(new OrganizationMembership
                {
                    Id = OrganizationMembership.IdFor("SparkUsers/" + user, Acme),
                    OrganizationId = Acme,
                    UserId = "SparkUsers/" + user,
                    UserName = user,
                    Role = role,
                    JoinedAtUtc = now.AddDays(-30),
                });
            await session.SaveChangesAsync();
        }

        await PutLegacyJoinRequestAsync(Store, PendingId, Acme, "rex", now.AddDays(-2), now.AddDays(12));
        await PutLegacyJoinRequestAsync(Store, ExpiredId, Acme, "sam", now.AddDays(-15), now.AddHours(-1));
        await PutLegacyJoinRequestAsync(Store, OrphanId, "Organizations/gone", "tom", now.AddDays(-1), now.AddDays(13));
        // A real invitation in the same collection: not a join request, left alone.
        await PutRawAsync(Store, InvitationId, new JsonObject
        {
            ["OrganizationId"] = Acme,
            ["Kind"] = "Invitation",
            ["Email"] = "bob@test.local",
            ["Role"] = "Member",
            ["TokenHash"] = SparkOrganizations.Hash("invitation-token"),
            ["CreatedBy"] = "SparkUsers/ann",
            ["CreatedAtUtc"] = now.AddDays(-1).ToString("O"),
            ["ExpiresAtUtc"] = now.AddDays(13).ToString("O"),
            ["@metadata"] = new JsonObject
            {
                ["@collection"] = "OrganizationInvitations",
                ["Raven-Clr-Type"] = LegacyClrType,
                ["@expires"] = now.AddDays(13).ToString("O"),
            },
        });
    }

    private async Task<T?> LoadAsync<T>(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    [Fact]
    public async Task Before_the_migration_the_join_requests_are_invitations_and_no_approval_request_exists()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(now);

        (await LoadAsync<OrganizationJoinRequestUpgrade.LegacyJoinRequest>(PendingId))!.Kind.Should().Be("JoinRequest", "the seed is the pre-D26 shape");
        (await LoadAsync<ApprovalRequest>(OrganizationJoinRequestUpgrade.RequestIdFor(PendingId))).Should().BeNull();
    }

    [Fact]
    public async Task A_pending_join_request_becomes_an_approval_request_and_expired_or_orphaned_ones_are_deleted()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(now);

        var result = await OrganizationJoinRequestUpgrade.RunAsync(Store, now, Retention);

        result.Should().Be(new OrganizationJoinRequestUpgrade.Result(Converted: 1, Expired: 1, Orphaned: 1));
        var requestId = OrganizationJoinRequestUpgrade.RequestIdFor(PendingId);
        requestId.Should().Be("ApprovalRequests/0a5e0000000040008000000000000001", "the invitation's guid, so the old link finds it");

        var request = (await LoadAsync<ApprovalRequest>(requestId))!;
        request.Should().NotBeNull();
        request.Kind.Should().Be(OrganizationJoinApproval.Name);
        request.SubjectType.Should().Be("Organization");
        request.SubjectId.Should().Be(Acme);
        request.SubjectKey.Should().Be("SparkUsers/rex", "keyed by the requester, as a new request is");
        request.KindAndType.Should().Be("organizations.join|Organization");
        request.Status.Should().Be(ApprovalStatuses.Pending);
        request.RequestedBy.Should().Be("SparkUsers/rex");
        request.RequestedAtUtc.Should().BeCloseTo(now.AddDays(-2), TimeSpan.FromSeconds(1));
        request.OrganizationId.Should().Be(Acme);
        request.Title.Should().Be("rex asks to join Acme");
        request.Summary.Should().Be("rex (rex@test.local) asks to join Acme as Member.");
        request.Url.Should().Be("/po/organization/Organizations%2Facme");
        // Every current Owner and Admin, no Member.
        request.DeciderIds.Should().BeEquivalentTo(["SparkUsers/ann", "SparkUsers/ole", "SparkUsers/ada"]);
        request.DecideByUtc.Should().BeCloseTo(now.AddDays(12), TimeSpan.FromSeconds(1), "the invitation's old expiry");
        request.NotifiedAtUtc.HasValue.Should().BeTrue("the Owners were mailed already: the sweep must not tell anyone again");
        request.Data[OrganizationJoinRequests.RoleKey].Should().Be("Member");
        request.Data[OrganizationJoinRequests.LegacyTokenHashKey].Should().Be(SparkOrganizations.Hash(LegacyToken));
        request.History.Should().ContainSingle().Which.Status.Should().Be(ApprovalStatuses.Pending);
        request.ExpiresAtUtc!.Value.Should().BeCloseTo(now.AddDays(12) + Retention, TimeSpan.FromSeconds(1));

        (await LoadAsync<OrganizationJoinRequestUpgrade.LegacyJoinRequest>(PendingId)).Should().BeNull("the old document goes with the conversion");
        (await LoadAsync<OrganizationJoinRequestUpgrade.LegacyJoinRequest>(ExpiredId)).Should().BeNull("an expired join request is only deleted");
        (await LoadAsync<OrganizationJoinRequestUpgrade.LegacyJoinRequest>(OrphanId)).Should().BeNull("nobody could decide one whose organization is gone");
        (await LoadAsync<ApprovalRequest>(OrganizationJoinRequestUpgrade.RequestIdFor(ExpiredId))).Should().BeNull();
        (await LoadAsync<ApprovalRequest>(OrganizationJoinRequestUpgrade.RequestIdFor(OrphanId))).Should().BeNull();
        (await LoadAsync<OrganizationInvitation>(InvitationId)).Should().NotBeNull("an invitation is not a join request");

        using (var session = Store.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<ApprovalRequest>(requestId);
            (session.Advanced.GetMetadataFor(stored)["@collection"] as string).Should().Be("ApprovalRequests");
        }

        (await Store.Maintenance.SendAsync(new GetIndexNamesOperation(0, 1024)))
            .Where(name => name.StartsWith("Auto/OrganizationInvitations", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("the migration scans the collection: indexes belong to the application");
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(now);
        await OrganizationJoinRequestUpgrade.RunAsync(Store, now, Retention);
        var requestId = OrganizationJoinRequestUpgrade.RequestIdFor(PendingId);
        string? changeVector;
        using (var session = Store.OpenAsyncSession())
            changeVector = session.Advanced.GetChangeVectorFor(await session.LoadAsync<ApprovalRequest>(requestId));

        var again = await OrganizationJoinRequestUpgrade.RunAsync(Store, now.AddMinutes(1), Retention);

        again.Should().Be(new OrganizationJoinRequestUpgrade.Result(0, 0, 0));
        using (var session = Store.OpenAsyncSession())
            session.Advanced.GetChangeVectorFor(await session.LoadAsync<ApprovalRequest>(requestId)).Should().Be(changeVector, "no document is rewritten");
        (await LoadAsync<OrganizationInvitation>(InvitationId)).Should().NotBeNull();
    }
}
