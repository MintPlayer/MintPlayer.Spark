using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// X8 (<c>docs/commerce_PRD.md</c>): the publishable OR mode and custom organization roles, through the real route
/// table. A published <see cref="IOrganizationPublishable"/> row is listed and read by everyone, anonymous included;
/// every other action stays with members; the OR never lifts another policy (expiration); the switcher still narrows
/// a member's lists; flipping the flag needs the <c>Publish</c> role. A custom role (<c>finance</c>) declared with
/// <c>AddOrganizationRole</c> is given on the membership form or through the service, and an action that names it
/// is open to its holders beside the built-in role it names.
/// </summary>
public class OrganizationPublishingTests : OrganizationsTestHost
{
    private static readonly Guid ProductTypeId = Guid.Parse("0a6e0000-0000-4000-8000-000000000101");
    private static readonly Guid ProductsQueryId = Guid.Parse("0a6e0000-0000-4000-8000-000000000111");

    public OrganizationPublishingTests()
    {
        ExtraModels.Add(ProductModel());
        ExtraRights.Add(new Right { Key = "og-products", Resource = "QueryReadEditNewDelete/OgProduct", GroupId = SparkTestSecurity.AuthenticatedGroupId });
        ExtraRights.Add(new Right { Key = "og-products-public", Resource = "QueryRead/OgProduct", GroupId = SparkTestSecurity.AnonymousGroupId });
        ExtraServices = services => services.AddScoped<OgProductActions>();
        ExtraSpark = spark => spark.AddOrganizationRole("finance", "Refunds and payouts");
    }

    // ---- publishing -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_published_row_is_listed_and_read_by_everyone_and_an_unpublished_one_by_members_only()
    {
        var ann = await SeedUserAsync("ann");
        var out_ = await SeedUserAsync("out");
        var acme = await SeedOrganizationAsync("Acme", ann);
        var other = await SeedOrganizationAsync("Other", out_);
        var published = await SeedProductAsync("ticket", acme.Id, isPublished: true);
        var draft = await SeedProductAsync("draft", acme.Id);
        var othersDraft = await SeedProductAsync("other draft", other.Id);

        (await QueryIdsAsync(Wire.Query(ProductsQueryId), null!)).Should().Equal(published.Id!);
        (await SendAsync("/spark/po/load", Load(published.Id!), null)).Status.Should().Be(HttpStatusCode.OK, "published: anonymous visitors read it");
        (await SendAsync("/spark/po/load", Load(draft.Id!), null)).Status.Should().Be(HttpStatusCode.NotFound);

        (await QueryIdsAsync(Wire.Query(ProductsQueryId), out_.Id!)).Should().Equal(new[] { published.Id!, othersDraft.Id! }.OrderBy(i => i));
        (await SendAsync("/spark/po/load", Load(draft.Id!), out_.Id)).Status.Should().Be(HttpStatusCode.NotFound, "missing and forbidden read the same (#453)");

        (await QueryIdsAsync(Wire.Query(ProductsQueryId), ann.Id!)).Should().Equal(new[] { published.Id!, draft.Id! }.OrderBy(i => i));
    }

    [Fact]
    public async Task Publishing_widens_reading_only_and_never_lifts_another_policy()
    {
        var ann = await SeedUserAsync("ann");
        var out_ = await SeedUserAsync("out");
        var acme = await SeedOrganizationAsync("Acme", ann);
        var published = await SeedProductAsync("ticket", acme.Id, isPublished: true);
        var expired = await SeedProductAsync("last year", acme.Id, isPublished: true, expiresAtUtc: DateTime.UtcNow.AddMinutes(-1));

        (await QueryIdsAsync(Wire.Query(ProductsQueryId), null!)).Should().Equal(published.Id!);
        (await SendAsync("/spark/po/load", Load(expired.Id!), null)).Status.Should().Be(HttpStatusCode.NotFound, "the expiration policy still ANDs with the OR");

        var (outsiderEdit, _) = await SendAsync("/spark/po/update", await EditAsync(published.Id!, ("Title", "hijacked")), out_.Id);
        outsiderEdit.Should().Be(HttpStatusCode.NotFound, "Edit still needs Member in the row's organization");
        var (outsiderDelete, _) = await SendAsync("/spark/po/delete", Wire.Typed(ProductTypeId, id: published.Id, etag: await StoredEtag.OfAsync(Store, published.Id!)), out_.Id);
        outsiderDelete.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync<OgProduct>(published.Id!))!.Title.Should().Be("ticket");
    }

    [Fact]
    public async Task With_an_organization_chosen_a_members_list_shows_that_organizations_rows_only()
    {
        var ann = await SeedUserAsync("ann");
        var out_ = await SeedUserAsync("out");
        var acme = await SeedOrganizationAsync("Acme", ann);
        var other = await SeedOrganizationAsync("Other", out_);
        var draft = await SeedProductAsync("draft", acme.Id);
        var othersPublished = await SeedProductAsync("other ticket", other.Id, isPublished: true);

        var chosen = "spark-organization=" + Uri.EscapeDataString(acme.Id!);
        (await QueryIdsAsync(Wire.Query(ProductsQueryId), ann.Id!, chosen)).Should().Equal(draft.Id!);
        (await QueryIdsAsync(Wire.Query(ProductsQueryId), ann.Id!)).Should().Equal(new[] { draft.Id!, othersPublished.Id! }.OrderBy(i => i), "All organizations: published rows of others too");
        (await SendAsync("/spark/po/load", Load(othersPublished.Id!), ann.Id, chosen)).Status.Should().Be(HttpStatusCode.OK, "the switcher narrows lists, never a load");
    }

    [Fact]
    public async Task Publishing_and_unpublishing_need_the_Publish_role()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var ada = await SeedUserAsync("ada");
        var acme = await SeedOrganizationAsync("Acme", ann, (max, OrganizationRole.Member), (ada, OrganizationRole.Admin));
        var draft = await SeedProductAsync("draft", acme.Id);

        var (memberTitle, _) = await SendAsync("/spark/po/update", await EditAsync(draft.Id!, ("Title", "renamed")), max.Id);
        memberTitle.Should().Be(HttpStatusCode.OK, "a Member edits");
        var (memberPublish, _) = await SendAsync("/spark/po/update", await EditAsync(draft.Id!, ("IsPublished", true)), max.Id);
        memberPublish.Should().Be(HttpStatusCode.BadRequest, "OgProduct declares Publish for Admins");
        (await LoadAsync<OgProduct>(draft.Id!))!.IsPublished.Should().BeFalse();

        var (memberCreatesPublished, _) = await SendAsync("/spark/po/create", NewProduct("launch", acme.Id!, isPublished: true), max.Id);
        memberCreatesPublished.Should().Be(HttpStatusCode.BadRequest, "creating a row published is publishing it");

        var (adminPublish, _) = await SendAsync("/spark/po/update", await EditAsync(draft.Id!, ("IsPublished", true)), ada.Id);
        adminPublish.Should().Be(HttpStatusCode.OK);
        (await LoadAsync<OgProduct>(draft.Id!))!.IsPublished.Should().BeTrue();
        (await QueryIdsAsync(Wire.Query(ProductsQueryId), null!)).Should().Equal(draft.Id!);

        var (memberUnpublish, _) = await SendAsync("/spark/po/update", await EditAsync(draft.Id!, ("IsPublished", false)), max.Id);
        memberUnpublish.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- custom roles ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_custom_role_opens_the_actions_that_name_it_beside_the_built_in_role()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var ada = await SeedUserAsync("ada");
        var acme = await SeedOrganizationAsync("Acme", ann, (max, OrganizationRole.Member), (ada, OrganizationRole.Admin));
        var first = await SeedProductAsync("first", acme.Id);
        var second = await SeedProductAsync("second", acme.Id);

        // OgProduct: Delete for Owners, or holders of "finance". An Admin without it may not.
        var (adminDelete, _) = await SendAsync("/spark/po/delete", Wire.Typed(ProductTypeId, id: first.Id, etag: await StoredEtag.OfAsync(Store, first.Id!)), ada.Id);
        adminDelete.Should().Be(HttpStatusCode.NotFound, "Delete names Owner and finance only");
        var (memberDelete, _) = await SendAsync("/spark/po/delete", Wire.Typed(ProductTypeId, id: first.Id, etag: await StoredEtag.OfAsync(Store, first.Id!)), max.Id);
        memberDelete.Should().Be(HttpStatusCode.NotFound);

        await Organizations().SetCustomRolesAsync(acme.Id!, max.Id!, ["FINANCE"], ann.Id!);
        (await Organizations().GetCustomRolesAsync(acme.Id!, max.Id!)).Should().Equal("finance");

        var (financeDelete, _) = await SendAsync("/spark/po/delete", Wire.Typed(ProductTypeId, id: first.Id, etag: await StoredEtag.OfAsync(Store, first.Id!)), max.Id);
        financeDelete.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        (await LoadAsync<OgProduct>(first.Id!)).Should().BeNull();

        var (ownerDelete, _) = await SendAsync("/spark/po/delete", Wire.Typed(ProductTypeId, id: second.Id, etag: await StoredEtag.OfAsync(Store, second.Id!)), ann.Id);
        new[] { HttpStatusCode.OK, HttpStatusCode.NoContent }.Should().Contain(ownerDelete, "an Owner passes every action");
    }

    [Fact]
    public async Task Admins_give_declared_custom_roles_on_the_membership_form_and_an_undeclared_one_is_refused()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var ada = await SeedUserAsync("ada");
        var acme = await SeedOrganizationAsync("Acme", ann, (max, OrganizationRole.Member), (ada, OrganizationRole.Admin));
        var membershipId = OrganizationMembership.IdFor(max.Id!, acme.Id!);

        var (bogus, _) = await SendAsync("/spark/po/update", await EditMembershipAsync(membershipId, "scanner"), ada.Id);
        bogus.Should().Be(HttpStatusCode.BadRequest, "scanner is not declared in this host");
        var (byMember, _) = await SendAsync("/spark/po/update", await EditMembershipAsync(OrganizationMembership.IdFor(ada.Id!, acme.Id!), "finance"), max.Id);
        byMember.Should().Be(HttpStatusCode.NotFound, "memberships are edited by Admins");

        var (given, _) = await SendAsync("/spark/po/update", await EditMembershipAsync(membershipId, "finance"), ada.Id);
        given.Should().Be(HttpStatusCode.OK);
        (await LoadAsync<OrganizationMembership>(membershipId))!.CustomRoles.Should().Equal("finance");

        var undeclared = () => Organizations().SetCustomRolesAsync(acme.Id!, max.Id!, ["scanner"], ann.Id!);
        await undeclared.Should().ThrowAsync<SparkValidationException>();
        // A Member gives no roles.
        var byMemberService = () => Organizations().SetCustomRolesAsync(acme.Id!, ada.Id!, ["finance"], max.Id!);
        await byMemberService.Should().ThrowAsync<SparkRowLevelAccessDeniedException>();
    }

    [Fact]
    public void A_custom_role_name_is_validated_when_declared()
    {
        OrganizationCustomRoles.IsValidName("scanner").Should().BeTrue();
        OrganizationCustomRoles.IsValidName("door-staff_2").Should().BeTrue();
        OrganizationCustomRoles.IsValidName("Admin").Should().BeFalse("a built-in role's name");
        OrganizationCustomRoles.IsValidName("2fa").Should().BeFalse();
        OrganizationCustomRoles.IsValidName("has space").Should().BeFalse();
        OrganizationCustomRoles.IsValidName("").Should().BeFalse();

        var requirement = RequiresOrganizationRoleAttribute.For(typeof(OgProduct), "Delete");
        requirement.Role.Should().Be(OrganizationRole.Owner);
        requirement.CustomRoles.Should().Equal("finance");
        requirement.IsMetBy(OrganizationRole.Admin, []).Should().BeFalse();
        requirement.IsMetBy(OrganizationRole.Viewer, ["Finance"]).Should().BeTrue();
        var publish = RequiresOrganizationRoleAttribute.For(typeof(OgProduct), "Publish");
        publish.Role.Should().Be(OrganizationRole.Admin);
        publish.CustomRoles.Should().BeEmpty();
        RequiresOrganizationRoleAttribute.For(typeof(OgNote), "Publish").Role.Should().Be(OrganizationRole.Member, "Publish follows Edit when undeclared");
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static object Load(string id) => Wire.Typed(ProductTypeId, id: id);

    private async Task<OgProduct> SeedProductAsync(string title, string? organizationId, bool isPublished = false, DateTime? expiresAtUtc = null)
    {
        var product = new OgProduct { Title = title, OrganizationId = organizationId, IsPublished = isPublished, ExpiresAtUtc = expiresAtUtc };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(product);
        await session.SaveChangesAsync();
        return product;
    }

    private async Task<object> EditAsync(string id, params (string Name, object Value)[] changes) => Wire.Typed(ProductTypeId, new
    {
        persistentObject = new
        {
            id,
            etag = await StoredEtag.OfAsync(Store, id),
            name = "OgProduct",
            objectTypeId = ProductTypeId.ToString(),
            attributes = changes.Select(c => new { name = c.Name, value = c.Value, isValueChanged = true }).ToArray(),
        },
    }, id);

    private async Task<object> EditMembershipAsync(string membershipId, params string[] customRoles) => Wire.Typed(MembershipTypeId, new
    {
        persistentObject = new
        {
            id = membershipId,
            etag = await StoredEtag.OfAsync(Store, membershipId),
            name = "OrganizationMembership",
            objectTypeId = MembershipTypeId,
            attributes = new[] { new { name = "CustomRoles", value = customRoles, isValueChanged = true } },
        },
    }, membershipId);

    private static object NewProduct(string title, string organizationId, bool isPublished) => Wire.Typed(ProductTypeId, new
    {
        persistentObject = new
        {
            name = "OgProduct",
            objectTypeId = ProductTypeId.ToString(),
            attributes = new object[]
            {
                new { name = "Title", value = title, isValueChanged = true },
                new { name = "OrganizationId", value = organizationId, isValueChanged = true },
                new { name = "IsPublished", value = isPublished, isValueChanged = true },
            },
        },
    });

    private static EntityTypeFile ProductModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ProductTypeId,
            Name = "OgProduct",
            ClrType = typeof(OgProduct).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = "OrganizationId", DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = "IsPublished", DataType = "boolean" },
            ],
        },
        Queries = [new SparkQuery { Id = ProductsQueryId, Name = "OgProducts", Source = "Custom.OgProducts", EntityType = "OgProduct" }],
    };
}

/// <summary>A publishable organization-owned row: Admins publish, Owners or holders of <c>finance</c> delete; it also expires.</summary>
[RequiresOrganizationRole("Publish", OrganizationRole.Admin)]
[RequiresOrganizationRole("Delete", OrganizationRole.Owner)]
[RequiresOrganizationRole("Delete", "finance")]
public class OgProduct : IOrganizationPublishable, IExpiring
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public bool IsPublished { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

public class OgProductActions(IEntityMapper mapper, IAsyncDocumentSession session) : DefaultPersistentObjectActions<OgProduct>(mapper)
{
    public IQueryable<OgProduct> OgProducts() => session.Query<OgProduct>();
}
