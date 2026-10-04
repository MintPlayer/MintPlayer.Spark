using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// #460 contributions M2c-1 — attribute-level rights, <c>{verb}/{Type}/{Attr}</c> (PRD §5 Q11–Q15):
/// the syntax, its validation, and the effective (type, attribute, verb) table M2c-2 enforces.
/// </summary>
public class AttributeRightsTests
{
    private static readonly Guid ContributorsId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EditorsId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Right Grant(string resource, Guid? group = null, bool important = false)
        => new() { Id = Guid.NewGuid(), GroupId = group ?? ContributorsId, Resource = resource, IsImportant = important };

    private static Right Deny(string resource, Guid? group = null, bool important = false)
        => new() { Id = Guid.NewGuid(), GroupId = group ?? ContributorsId, Resource = resource, IsDenied = true, IsImportant = important };

    private static SecurityConfiguration Config(params Right[] rights) => new()
    {
        Groups =
        {
            [ContributorsId.ToString()] = "Contributors",
            [EditorsId.ToString()] = "Editors",
        },
        Rights = [.. rights],
    };

    private static EffectiveAttributeRights Effective(string verb, SecurityConfiguration config, params Guid[] groups)
        => RightsDecision.For(config, new HashSet<Guid>(groups.Length == 0 ? [ContributorsId] : groups)).ForAttributes(verb, "Song");

    // ---------- syntax ----------

    [Theory]
    [InlineData("Query")]
    [InlineData("Read")]
    [InlineData("Edit")]
    [InlineData("New")]
    [InlineData("QueryRead")]
    [InlineData("QueryReadEdit")]
    [InlineData("QueryReadEditNew")]
    [InlineData("ReadEdit")]
    [InlineData("ReadEditNew")]
    [InlineData("EditNew")]
    [InlineData("queryread")]
    public void A_verb_made_of_Query_Read_Edit_New_has_an_attribute_form(string action)
        => SparkAttributeRights.IsAttributeAction(action).Should().BeTrue();

    [Theory]
    [InlineData("Delete")]
    [InlineData("EditNewDelete")]
    [InlineData("NewDelete")]
    [InlineData("ReadEditNewDelete")]
    [InlineData("QueryReadEditNewDelete")]
    [InlineData("Replicate")]
    [InlineData("Approve")]
    [InlineData("")]
    public void Delete_combinations_package_verbs_and_custom_actions_have_no_attribute_form(string action)
        => SparkAttributeRights.IsAttributeAction(action).Should().BeFalse();

    [Fact]
    public void TrySplit_keeps_the_written_casing_and_ignores_type_level_resources()
    {
        SparkAttributeRights.TrySplit("QueryRead/Employee/Salary", out var action, out var type, out var attribute).Should().BeTrue();
        (action, type, attribute).Should().Be(("QueryRead", "Employee", "Salary"));

        SparkAttributeRights.TrySplit("Edit/Song", out _, out _, out _).Should().BeFalse();
    }

    // ---------- validation ----------

    private static IModelLoader Model()
    {
        var model = Substitute.For<IModelLoader>();
        var song = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Song",
            Alias = "songs",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Lyrics" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Genre" },
            ],
        };
        model.GetEntityTypeByName(Arg.Any<string>())
            .Returns(ci => string.Equals(ci.Arg<string>(), "Song", StringComparison.OrdinalIgnoreCase) ? song : null);
        return model;
    }

    [Theory]
    [InlineData("Edit/Song/Lyrics")]
    [InlineData("edit/song/LYRICS")]
    [InlineData("QueryReadEditNew/Song/Genre")]
    [InlineData("EditNew/Song/Title")]
    public void A_valid_attribute_right_is_accepted(string resource)
    {
        var act = () => SecurityConfigurationValidator.Validate(Config(Grant(resource)), Model());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Delete/Song/Lyrics", "no attribute-level form")]
    [InlineData("EditNewDelete/Song/Lyrics", "no attribute-level form")]
    [InlineData("QueryReadEditNewDelete/Song/Lyrics", "no attribute-level form")]
    [InlineData("Approve/Song/Lyrics", "no attribute-level form")]
    [InlineData("Replicate/Song/Lyrics", "no attribute-level form")]
    [InlineData("Edit/Song/Lyrics/Text", "never with a longer path")]
    [InlineData("Edit//Lyrics", "never with a longer path")]
    [InlineData("Edit/Sogn/Lyrics", "no persistent object named 'Sogn'")]
    [InlineData("Edit/songs/Lyrics", "no persistent object named 'songs'")]
    [InlineData("Read/LookupReferences/Name", "no persistent object named 'LookupReferences'")]
    [InlineData("Edit/Song/Lyricz", "declares no attribute 'Lyricz'")]
    public void An_invalid_attribute_right_refuses_the_file(string resource, string expected)
    {
        var act = () => SecurityConfigurationValidator.Validate(Config(Grant(resource)), Model());

        act.Should().Throw<SparkSecurityConfigurationException>().Which.Message.Should().Contain(expected);
    }

    [Fact]
    public void Without_a_model_the_syntax_is_still_checked()
    {
        var bad = () => SecurityConfigurationValidator.Validate(Config(Grant("Delete/Song/Lyrics")));
        var unknownType = () => SecurityConfigurationValidator.Validate(Config(Grant("Edit/Nothing/Lyrics")));

        bad.Should().Throw<SparkSecurityConfigurationException>();
        unknownType.Should().NotThrow("the model lookup is the loader's, which always passes the model");
    }

    // ---------- evaluation ----------

    /// <summary>
    /// The evaluation matrix of Q13 for one group: the type right is required, an attribute right
    /// composes over it tier by tier, and an attribute nobody mentions inherits the type decision.
    /// </summary>
    [Theory]
    // No type right: nothing is unlocked, whatever the attribute says.
    [InlineData(null, "grant", false, false)]
    [InlineData(null, "important-grant", false, false)]
    // Type granted.
    [InlineData("grant", null, true, true)]
    [InlineData("grant", "grant", true, true)]
    [InlineData("grant", "deny", true, false)]
    [InlineData("grant", "important-deny", true, false)]
    [InlineData("grant", "important-grant", true, true)]
    // Type granted importantly: outranks an ordinary attribute denial, not an important one.
    [InlineData("important-grant", "deny", true, true)]
    [InlineData("important-grant", "important-deny", true, false)]
    // Type denied (an ordinary denial beats the ordinary grant on the type too).
    [InlineData("deny", "important-grant", false, false)]
    public void The_type_right_is_required_and_attribute_rights_compose_over_it(
        string? typeRight, string? attributeRight, bool typeAllowed, bool lyricsAllowed)
    {
        var rights = new List<Right>();
        if (typeRight == "deny") rights.Add(Grant("Edit/Song", EditorsId));
        rights.AddRange(Rights(typeRight, "Edit/Song"));
        rights.AddRange(Rights(attributeRight, "Edit/Song/Lyrics"));

        var effective = Effective("Edit", Config([.. rights]), ContributorsId, EditorsId);

        effective.TypeAllowed.Should().Be(typeAllowed);
        effective.IsAllowed("Lyrics").Should().Be(lyricsAllowed);
        effective.IsAllowed("lyrics").Should().Be(lyricsAllowed, "attribute names are case-insensitive");
        effective.IsAllowed("Title").Should().Be(typeAllowed, "an unmentioned attribute inherits the type decision");
    }

    private static IEnumerable<Right> Rights(string? kind, string resource) => kind switch
    {
        null => [],
        "grant" => [Grant(resource)],
        "important-grant" => [Grant(resource, important: true)],
        "deny" => [Deny(resource)],
        "important-deny" => [Deny(resource, important: true)],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The tiers run across every group before the next tier: one group's attribute denial restricts
    /// another group's type grant, and an important attribute grant in one group outranks an ordinary
    /// attribute denial in another.
    /// </summary>
    [Fact]
    public void Tiers_are_evaluated_across_all_groups()
    {
        var denied = Effective("Edit", Config(Grant("Edit/Song", EditorsId), Deny("Edit/Song/Lyrics")), ContributorsId, EditorsId);
        denied.IsAllowed("Lyrics").Should().BeFalse();

        var rescued = Effective("Edit", Config(
            Grant("Edit/Song", EditorsId),
            Deny("Edit/Song/Lyrics", EditorsId),
            Grant("Edit/Song/Lyrics", important: true)), ContributorsId, EditorsId);
        rescued.IsAllowed("Lyrics").Should().BeTrue();

        // A group the caller is not in contributes nothing.
        var outsider = Effective("Edit", Config(Grant("Edit/Song"), Deny("Edit/Song/Lyrics", EditorsId)), ContributorsId);
        outsider.IsAllowed("Lyrics").Should().BeTrue();
        outsider.Attributes.Should().BeEmpty();
    }

    /// <summary>The contributor scenario of Q13: <c>Edit/Song</c> plus denies on the other attributes.</summary>
    [Fact]
    public void A_contributor_edits_only_what_is_not_denied()
    {
        var config = Config(Grant("QueryReadEdit/Song"), Deny("Edit/Song/Title"), Deny("Edit/Song/Genre"));

        var edit = Effective("Edit", config);
        edit.IsAllowed("Lyrics").Should().BeTrue();
        edit.IsAllowed("Title").Should().BeFalse();
        edit.DeniedAttributes.Should().BeEquivalentTo(["TITLE", "GENRE"]);

        Effective("Read", config).IsAllowed("Title").Should().BeTrue("a denial of Edit says nothing about Read");
    }

    [Fact]
    public void A_combined_verb_expands_as_prefixes_on_attribute_rights()
    {
        var config = Config(Grant("QueryReadEditNew/Song"), Deny("QueryRead/Song/Lyrics"));

        Effective("Query", config).IsAllowed("Lyrics").Should().BeFalse();
        Effective("Read", config).IsAllowed("Lyrics").Should().BeFalse();
        Effective("Edit", config).IsAllowed("Lyrics").Should().BeTrue();
        Effective("New", config).IsAllowed("Lyrics").Should().BeTrue();
    }

    /// <summary>
    /// Read ⇒ Query mirrors the type-level rule: a grant of Read implies Query, a denial of Read does
    /// not deny Query ("list, but no click-through" stays expressible).
    /// </summary>
    [Fact]
    public void Read_implies_Query_for_grants_only_as_at_type_level()
    {
        var denyRead = Config(Grant("QueryRead/Song"), Deny("Read/Song/Lyrics"));
        Effective("Read", denyRead).IsAllowed("Lyrics").Should().BeFalse();
        Effective("Query", denyRead).IsAllowed("Lyrics").Should().BeTrue();

        var importantRead = Config(Grant("QueryRead/Song"), Deny("Query/Song/Lyrics"), Grant("Read/Song/Lyrics", important: true));
        Effective("Query", importantRead).IsAllowed("Lyrics").Should().BeTrue("the important Read grant implies an important Query grant");
    }

    [Fact]
    public void New_and_Edit_are_separate_verbs()
    {
        var config = Config(Grant("EditNew/Song"), Deny("New/Song/Genre"));

        Effective("New", config).IsAllowed("Genre").Should().BeFalse();
        Effective("Edit", config).IsAllowed("Genre").Should().BeTrue();
    }

    [Fact]
    public void Attribute_rights_never_change_the_type_level_decision()
    {
        var decision = RightsDecision.For(Config(Grant("Edit/Song/Lyrics")), new HashSet<Guid> { ContributorsId });

        decision.Allows("Edit/Song").Should().BeFalse("an attribute grant unlocks nothing");

        var denied = RightsDecision.For(Config(Grant("Edit/Song"), Deny("Edit/Song/Lyrics")), new HashSet<Guid> { ContributorsId });
        denied.Allows("Edit/Song").Should().BeTrue("an attribute denial does not deny the type");
    }

    // ---------- IAttributeRights ----------

    private sealed class CountingAccessControl(SecurityConfiguration config) : IAccessControl
    {
        public int AttributeCalls { get; private set; }

        private RightsDecision Decision => RightsDecision.For(config, new HashSet<Guid> { ContributorsId });

        public Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
            => Task.FromResult(Decision.Allows(resource));

        public Task<EffectiveAttributeRights> GetAttributeRightsAsync(string verb, string entityTypeName, CancellationToken cancellationToken = default)
        {
            AttributeCalls++;
            return Task.FromResult(Decision.ForAttributes(verb, entityTypeName));
        }
    }

    private static IHttpContextAccessor Accessor(bool system)
    {
        var identity = new ClaimsIdentity(
            system ? [new Claim(SparkSystemContext.ClaimType, "module")] : [], authenticationType: "Test");
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = new ClaimsPrincipal(identity) });
        return accessor;
    }

    [Fact]
    public async Task The_table_is_evaluated_once_per_type_and_verb_per_request()
    {
        var access = new CountingAccessControl(Config(Grant("QueryReadEdit/Song"), Deny("Edit/Song/Lyrics")));
        var rights = new AttributeRights(access, Accessor(system: false));

        var first = await rights.GetEffectiveAsync("Song", "Edit");
        var again = await rights.GetEffectiveAsync("song", "edit");
        await rights.GetEffectiveAsync("Song", "Read");

        again.Should().BeSameAs(first);
        access.AttributeCalls.Should().Be(2, "one evaluation for (Song, Edit) and one for (Song, Read)");
        first.IsAllowed("Lyrics").Should().BeFalse();
    }

    [Fact]
    public async Task The_definition_overload_gives_attributes_the_model_spelling()
    {
        var access = new CountingAccessControl(Config(Grant("Edit/Song"), Deny("Edit/song/lyrics")));
        var rights = new AttributeRights(access, Accessor(system: false));

        var effective = await rights.GetEffectiveAsync(Model().GetEntityTypeByName("Song")!, "Edit");

        effective.DeniedAttributes.Should().BeEquivalentTo(["Lyrics"]);
    }

    [Fact]
    public async Task System_context_is_unrestricted_and_asks_nothing()
    {
        var access = new CountingAccessControl(Config(Deny("Edit/Song", important: true)));
        var rights = new AttributeRights(access, Accessor(system: true));

        var effective = await rights.GetEffectiveAsync("Song", "Edit");

        effective.IsUnrestricted.Should().BeTrue();
        effective.IsAllowed("Lyrics").Should().BeTrue();
        access.AttributeCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("Delete")]
    [InlineData("QueryRead")]
    [InlineData("Approve")]
    public async Task A_verb_without_an_attribute_form_is_refused(string verb)
    {
        var rights = new AttributeRights(new CountingAccessControl(Config()), Accessor(system: false));

        var act = () => rights.GetEffectiveAsync("Song", verb);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// An evaluator that knows no attribute rights (a test double granting resource strings) gets the
    /// interface's default: the type decision, inherited by every attribute.
    /// </summary>
    [Fact]
    public async Task An_access_control_without_attribute_rights_inherits_the_type_decision()
    {
        var access = Substitute.For<IAccessControl>();
        access.IsAllowedAsync("Edit/Song", Arg.Any<CancellationToken>()).Returns(true);

        // The default interface method, not a substitute of it.
        var effective = await DefaultAttributeRights(access, "Edit", "Song");

        effective.TypeAllowed.Should().BeTrue();
        effective.Attributes.Should().BeEmpty();
        effective.IsAllowed("Lyrics").Should().BeTrue();
    }

    private static Task<EffectiveAttributeRights> DefaultAttributeRights(IAccessControl inner, string verb, string type)
        => ((IAccessControl)new OnlyTypeLevel(inner)).GetAttributeRightsAsync(verb, type);

    private sealed class OnlyTypeLevel(IAccessControl inner) : IAccessControl
    {
        public Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
            => inner.IsAllowedAsync(resource, cancellationToken);
    }
}
