using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// The account side of X11 (<c>docs/seo_analyzer_PRD.md</c> D21, D26), through the hooks the Authorization
/// package resolves for the application's user type: the personal organization on sign-in, account
/// deletion, and the personal-data export.
/// </summary>
public class OrganizationAccountTests : OrganizationsTestHost
{
    /// <summary>Signs <paramref name="user"/> in through Spark's sign-in manager, as every sign-in path ends.</summary>
    private async Task SignInAsync(SparkUser user)
    {
        using var scope = Factory.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<SparkUser>>();
        signIn.Should().BeAssignableTo<SparkSignInManager<SparkUser>>();
        var stored = await scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>().FindByIdAsync(user.Id!);
        await signIn.SignInAsync(stored!, isPersistent: false);
    }

    [Fact]
    public async Task Signing_in_creates_the_personal_organization_once()
    {
        var ann = await SeedUserAsync("ann");
        (await LoadAsync<Organization>(Organization.PersonalIdFor(ann.Id!))).Should().BeNull();

        await SignInAsync(ann);
        await SignInAsync(ann);

        var personal = await LoadAsync<Organization>(Organization.PersonalIdFor(ann.Id!));
        personal.Should().NotBeNull("the sign-in hook created it (D21)");
        personal!.IsPersonal.Should().BeTrue();
        personal.Name.Should().Be("ann");
        (await MembersAsync(personal.Id!)).Should().ContainSingle().Which.Role.Should().Be(OrganizationRole.Owner);
    }

    private async Task DeleteAccountAsync(SparkUser user)
    {
        using var scope = Factory.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<ISparkAccountDeletionHandler<SparkUser>>().ToList();
        handlers.Should().Contain(h => h.GetType().Namespace == "MintPlayer.Spark.Organizations.Identity", "AddOrganizations registers its handler for any user type");
        foreach (var handler in handlers)
            await handler.OnDeletingAccountAsync(user, CancellationToken.None);
    }

    [Fact]
    public async Task Deleting_an_account_deletes_its_personal_organization_and_its_memberships()
    {
        var sites = new OgDataHandler("sites");
        DataHandlers.Add(sites);
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var personal = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);
        var solo = await SeedOrganizationAsync("Solo", ann);
        var shared = await SeedOrganizationAsync("Shared", ann, (bob, OrganizationRole.Owner));
        var member = await SeedOrganizationAsync("Member", bob, (ann, OrganizationRole.Member));
        await SeedNoteAsync("kept", member.Id);

        await DeleteAccountAsync(ann);

        (await LoadAsync<Organization>(personal.Id!)).Should().BeNull("the personal organization goes with the account (D26)");
        (await LoadAsync<Organization>(solo.Id!)).Should().BeNull("nobody else was in it");
        sites.Deleted.Should().BeEquivalentTo([personal.Id!, solo.Id!]);
        (await LoadAsync<Organization>(shared.Id!)).Should().NotBeNull("another Owner remains");
        (await LoadAsync<Organization>(member.Id!)).Should().NotBeNull();
        (await MembersAsync(shared.Id!)).Select(m => m.UserId).Should().Equal(bob.Id);
        (await MembersAsync(member.Id!)).Select(m => m.UserId).Should().Equal(bob.Id);
        (await Organizations().GetMembershipsAsync(ann.Id!)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_only_Owner_of_an_organization_with_other_members_cannot_delete_the_account_until_ownership_moves()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var personal = await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);
        var acme = await SeedOrganizationAsync("Acme", ann, (bob, OrganizationRole.Member));

        var delete = () => DeleteAccountAsync(ann);

        await delete.Should().ThrowAsync<SparkValidationException>().WithMessage("*Acme*");
        (await LoadAsync<Organization>(personal.Id!)).Should().NotBeNull("nothing is deleted before every check passed");
        (await Organizations().GetRoleAsync(acme.Id!, ann.Id!)).Should().Be(OrganizationRole.Owner);
    }

    [Fact]
    public async Task The_export_lists_every_membership_and_the_owned_organizations_data()
    {
        DataHandlers.Add(new OgDataHandler("sites"));
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await Organizations().EnsurePersonalOrganizationAsync(ann.Id!);
        var acme = await SeedOrganizationAsync("Acme", ann);
        await SeedOrganizationAsync("Beta", bob, (ann, OrganizationRole.Viewer));

        using var scope = Factory.CreateScope();
        var contributor = scope.ServiceProvider.GetServices<ISparkPersonalDataContributor<SparkUser>>().Single(c => c.Name == "organizations");
        var export = JsonSerializer.SerializeToNode(await contributor.GetPersonalDataAsync(ann, CancellationToken.None))!.AsArray();

        export.Select(e => e!["name"]!.GetValue<string>()).Should().BeEquivalentTo(["ann", "Acme", "Beta"]);
        var owned = export.Single(e => e!["name"]!.GetValue<string>() == "Acme")!;
        owned["role"]!.GetValue<string>().Should().Be("Owner");
        owned["data"]!["sites"]!["sites"]![0]!.GetValue<string>().Should().Be("https://acme.example");
        var viewed = export.Single(e => e!["name"]!.GetValue<string>() == "Beta")!;
        viewed["role"]!.GetValue<string>().Should().Be("Viewer");
        viewed["data"].Should().BeNull("only an Owner exports an organization's data");
    }
}
