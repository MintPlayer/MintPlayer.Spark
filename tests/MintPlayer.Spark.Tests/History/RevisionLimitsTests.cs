using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.History;

namespace MintPlayer.Spark.Tests.History;

/// <summary>
/// #460 M16: revision limits from <c>Spark:History</c> — binding, configuration over code, the
/// per-property precedence (type options → model block → default), and the licence check that runs
/// before anything is sent. The check is tested as logic: no Community server is needed.
/// </summary>
public class RevisionLimitsTests
{
    private static SparkHistoryOptions Resolve(Dictionary<string, string?> settings, Action<SparkHistoryOptions>? code = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        new MintPlayer.Spark.SparkBuilder(services, configuration).AddHistory(code);
        return services.BuildServiceProvider().GetRequiredService<IOptions<SparkHistoryOptions>>().Value;
    }

    private static EntityTypeDefinition Type(string name, EntityRevisionsDefinition? revisions) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ClrType = $"Demo.{name}",
        Revisions = revisions,
    };

    [Fact]
    public void The_defaults_are_30_days_no_count_limit_and_no_purge()
    {
        var options = Resolve([]);

        options.ConfigureRevisions.Should().BeTrue();
        options.Revisions.MinimumRevisionAgeToKeep.Should().Be(TimeSpan.FromDays(30));
        options.Revisions.MinimumRevisionsToKeep.HasValue.Should().BeFalse();
        options.Revisions.PurgeOnDelete.HasValue.Should().BeFalse();
        options.Types.Should().BeEmpty();
    }

    [Fact]
    public void Configuration_binds_the_default_and_per_type_limits_and_beats_code()
    {
        var options = Resolve(new()
        {
            ["Spark:History:Revisions:MinimumRevisionAgeToKeep"] = "10.00:00:00",
            ["Spark:History:Types:Question:MinimumRevisionsToKeep"] = "2",
            ["Spark:History:Types:Answer:Enabled"] = "false",
        }, code: o =>
        {
            o.Revisions.MinimumRevisionAgeToKeep = TimeSpan.FromDays(40);
            o.Revisions.PurgeOnDelete = true;
            o.Types["question"] = new SparkRevisionTypeOptions { MinimumRevisionsToKeep = 50, PurgeOnDelete = false };
        });

        options.Revisions.MinimumRevisionAgeToKeep.Should().Be(TimeSpan.FromDays(10), "configuration beats code");
        options.Revisions.PurgeOnDelete.Should().Be(true, "an unconfigured property keeps the code value");
        options.Types["Question"].MinimumRevisionsToKeep.Should().Be(2, "configuration beats code, and type names are case-insensitive");
        options.Types["Question"].PurgeOnDelete.Should().Be(false);
        options.Types["Answer"].Enabled.Should().Be(false);
    }

    [Fact]
    public void Each_setting_comes_from_the_type_options_then_the_model_then_the_default()
    {
        var options = new SparkHistoryOptions();
        options.Revisions.MinimumRevisionsToKeep = 5;
        options.Types["Question"] = new SparkRevisionTypeOptions { MinimumRevisionAgeToKeep = TimeSpan.FromDays(7) };
        var model = new EntityRevisionsDefinition { Enabled = true, MinimumRevisionsToKeep = 3, MinimumRevisionAgeToKeep = TimeSpan.FromDays(20) };

        var question = RevisionsConfigurator.Resolve(Type("Question", model), options)!;
        question.Enabled.Should().BeTrue();
        question.MinimumRevisionAgeToKeep.Should().Be(TimeSpan.FromDays(7));
        question.MinimumRevisionAgeToKeepSource.Should().Be("Spark:History:Types:Question");
        question.MinimumRevisionsToKeep.Should().Be(3, "the model beats the default");
        question.MinimumRevisionsToKeepSource.Should().Be("the model's \"revisions\" block");

        var plain = RevisionsConfigurator.Resolve(Type("Tag", new EntityRevisionsDefinition { Enabled = true }), options)!;
        plain.MinimumRevisionsToKeep.Should().Be(5);
        plain.MinimumRevisionAgeToKeep.Should().Be(TimeSpan.FromDays(30), "the built-in default");
        plain.PurgeOnDelete.Should().BeFalse();

        RevisionsConfigurator.Resolve(Type("Untouched", null), options).Should().BeNull("no model block and no Enabled: the collection is left alone");
    }

    [Fact]
    public void Type_options_can_enable_or_disable_revisions_and_zero_means_no_limit()
    {
        var options = new SparkHistoryOptions();
        options.Revisions.MinimumRevisionAgeToKeep = TimeSpan.Zero;
        options.Types["Audit"] = new SparkRevisionTypeOptions { Enabled = true, MinimumRevisionsToKeep = 0 };
        options.Types["Draft"] = new SparkRevisionTypeOptions { Enabled = false };

        var audit = RevisionsConfigurator.Resolve(Type("Audit", null), options)!;
        audit.Enabled.Should().BeTrue();
        audit.MinimumRevisionsToKeep.HasValue.Should().BeFalse("0 = no count limit");
        audit.MinimumRevisionAgeToKeep.HasValue.Should().BeFalse("00:00:00 = no age limit");

        RevisionsConfigurator.Resolve(Type("Draft", new EntityRevisionsDefinition { Enabled = true, PurgeOnDelete = true }), options)!
            .Enabled.Should().BeFalse("the type options beat the model's enabled");
    }

    [Fact]
    public void The_Community_check_names_every_type_setting_and_source_and_the_escape_hatch()
    {
        var options = new SparkHistoryOptions();
        options.Types["Question"] = new SparkRevisionTypeOptions { MinimumRevisionsToKeep = 10 };
        options.Revisions.MinimumRevisionAgeToKeep = TimeSpan.FromDays(90);
        RevisionsConfigurator.WantedRevisions Wanted(string type, EntityRevisionsDefinition model)
            => new($"{type}s", type, RevisionsConfigurator.Resolve(Type(type, model), options)!);

        var wanted = new[]
        {
            Wanted("Question", new EntityRevisionsDefinition { Enabled = true }),
            Wanted("Answer", new EntityRevisionsDefinition { Enabled = true, MinimumRevisionsToKeep = 2, MinimumRevisionAgeToKeep = TimeSpan.FromDays(45) }),
            Wanted("Off", new EntityRevisionsDefinition { Enabled = false, MinimumRevisionsToKeep = 100 }),
        };

        var problems = RevisionsConfigurator.LicenceProblems(wanted, RevisionsConfigurator.Community);

        problems.Should().Equal([
            "Question (Questions): MinimumRevisionsToKeep = 10 (from Spark:History:Types:Question)",
            "Question (Questions): MinimumRevisionAgeToKeep = 90 days (from Spark:History:Revisions)",
        ], "Answer is exactly at the caps (2, 45 days) and Off is disabled");

        var message = RevisionsConfigurator.LicenceRefusal(RevisionsConfigurator.Community, problems).Message;
        message.Should().Contain("Community licence allows at most 2 revisions and 45 days");
        message.Should().Contain("MinimumRevisionsToKeep = 10 (from Spark:History:Types:Question)");
        message.Should().Contain("Spark:History:ConfigureRevisions=false");
    }

    [Fact]
    public void No_limit_is_within_the_Community_caps()
    {
        var options = new SparkHistoryOptions();
        options.Revisions.MinimumRevisionAgeToKeep = null;
        var unlimited = new RevisionsConfigurator.WantedRevisions("Notes", "Note",
            RevisionsConfigurator.Resolve(Type("Note", new EntityRevisionsDefinition { Enabled = true }), options)!);

        RevisionsConfigurator.LicenceProblems([unlimited], RevisionsConfigurator.Community)
            .Should().BeEmpty("H1 measured Community accepting a collection without limits");
    }
}
