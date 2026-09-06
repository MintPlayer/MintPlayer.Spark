using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// The startup gate for types reachable by a well-known group with no row policy behind them.
/// <para>
/// The distinction it draws is the whole point, and it is narrow: it does <b>not</b> refuse public
/// data. An application may publish an entire collection — <c>ReportSecurityPosture</c> records that
/// as a decision an application is entitled to make — it just may not leave the decision unmade.
/// So a type with a row rule passes, a type that declares itself public passes, and only the silent
/// case is refused.
/// </para>
/// </summary>
public class RowPolicyDeclarationTests
{
    private static readonly Guid AnonymousGroup = Guid.Parse("00000000-0000-0000-0000-000000000000");
    private static readonly Guid AuthenticatedGroup = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OrdinaryGroup = Guid.Parse("cafe0000-0000-0000-0000-000000000009");

    private sealed class Declared : ISparkOwnsRowSecurity
    {
        public string RowSecurityRationale => "Public reference data; every row is safe to publish.";
    }

    private sealed class Blank : ISparkOwnsRowSecurity
    {
        public string RowSecurityRationale => "";
    }

    private sealed class Undeclared;

    private static EntityTypeDefinition Type(string name, string? clrType = "Some.Clr.Type") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ClrType = clrType,
    };

    private static SecurityConfiguration Config(Guid group, string resource, bool denied = false) => new()
    {
        WellKnown = new()
        {
            [SparkWellKnownGroups.Anonymous] = AnonymousGroup.ToString(),
            [SparkWellKnownGroups.Authenticated] = AuthenticatedGroup.ToString(),
        },
        Rights = [new Right { Id = Guid.NewGuid(), Resource = resource, GroupId = group, IsDenied = denied }],
    };

    private static IReadOnlyList<string> Validate(
        SecurityConfiguration config, bool hasRowRule = false, object? actions = null)
        => RowPolicyDeclarationValidator.Validate(
            config, [Type("Widget")], _ => hasRowRule, _ => actions ?? new Undeclared());

    [Fact]
    public void An_anonymous_grant_with_no_row_rule_and_no_declaration_is_refused()
    {
        var problems = Validate(Config(AnonymousGroup, "QueryRead/Widget"));

        problems.Should().ContainSingle().Which.Should()
            .Contain("Widget").And.Contain("anonymous").And.Contain("ISparkOwnsRowSecurity");
    }

    /// <summary>
    /// <c>authenticated</c> is every account that ever signs up, not a vetted group — so it needs the
    /// same treatment as anonymous, which is easy to assume it does not.
    /// </summary>
    [Fact]
    public void An_authenticated_grant_is_judged_the_same_way()
    {
        Validate(Config(AuthenticatedGroup, "QueryRead/Widget")).Should().ContainSingle();
    }

    [Fact]
    public void A_type_with_a_row_rule_passes()
    {
        Validate(Config(AnonymousGroup, "QueryRead/Widget"), hasRowRule: true).Should().BeEmpty();
    }

    /// <summary>Publishing everything is allowed; it just has to be written down.</summary>
    [Fact]
    public void A_type_that_declares_itself_public_passes()
    {
        Validate(Config(AnonymousGroup, "QueryRead/Widget"), actions: new Declared()).Should().BeEmpty();
    }

    /// <summary>
    /// An empty rationale is not a declaration. The interface exists to force a sentence someone had
    /// to write; accepting <c>""</c> would reduce it to a marker anyone can paste on.
    /// </summary>
    [Fact]
    public void An_empty_rationale_does_not_count_as_a_declaration()
    {
        Validate(Config(AnonymousGroup, "QueryRead/Widget"), actions: new Blank()).Should().ContainSingle();
    }

    /// <summary>
    /// An ordinary group is not this gate's business: someone had to be put in it deliberately, so
    /// the type-level grant is itself the constraint.
    /// </summary>
    [Fact]
    public void A_grant_to_an_ordinary_group_is_left_alone()
    {
        Validate(Config(OrdinaryGroup, "QueryRead/Widget")).Should().BeEmpty();
    }

    [Fact]
    public void A_denial_exposes_nothing_and_is_ignored()
    {
        Validate(Config(AnonymousGroup, "QueryRead/Widget", denied: true)).Should().BeEmpty();
    }

    /// <summary>
    /// Write and custom actions are a different question with different answers, and the write paths
    /// have their own gates. Only actions that hand rows to a caller are judged here.
    /// </summary>
    [Theory]
    [InlineData("Edit/Widget")]
    [InlineData("New/Widget")]
    [InlineData("Delete/Widget")]
    [InlineData("Archive/Widget")]
    public void A_grant_that_returns_no_rows_is_not_judged(string resource)
    {
        Validate(Config(AnonymousGroup, resource)).Should().BeEmpty();
    }

    /// <summary>Combined names expand, so the row-returning half inside one still counts.</summary>
    [Fact]
    public void A_combined_action_containing_Query_is_judged()
    {
        Validate(Config(AnonymousGroup, "QueryReadEditNewDelete/Widget")).Should().ContainSingle();
    }

    /// <summary>
    /// A composed type has no document a row filter could act on, and carries its own declaration
    /// requirement at execution time. Judging it here would demand the same statement twice.
    /// </summary>
    [Fact]
    public void A_composed_type_is_left_to_its_own_declaration()
    {
        var problems = RowPolicyDeclarationValidator.Validate(
            Config(AnonymousGroup, "Query/Widget"),
            [Type("Widget", clrType: null)],
            _ => false,
            _ => new Undeclared());

        problems.Should().BeEmpty();
    }

    /// <summary>
    /// A file with no <c>wellKnown</c> block has no well-known roles to judge against, and every
    /// group in it is an ordinary one.
    /// </summary>
    [Fact]
    public void A_configuration_with_no_wellKnown_block_is_not_judged()
    {
        var config = new SecurityConfiguration
        {
            Rights = [new Right { Id = Guid.NewGuid(), Resource = "QueryRead/Widget", GroupId = AnonymousGroup }],
        };

        Validate(config).Should().BeEmpty();
    }

    /// <summary>One message per type, however many grants exposed it — the fix is the same one.</summary>
    [Fact]
    public void A_type_exposed_by_two_grants_is_reported_once()
    {
        var config = Config(AnonymousGroup, "QueryRead/Widget");
        config.Rights.Add(new Right
        {
            Id = Guid.NewGuid(),
            Resource = "Query/Widget",
            GroupId = AuthenticatedGroup,
        });

        Validate(config).Should().ContainSingle();
    }
}
