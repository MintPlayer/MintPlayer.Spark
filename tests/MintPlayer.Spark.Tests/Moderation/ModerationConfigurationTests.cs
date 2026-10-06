using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.Moderation.Indexes;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.Moderation;

/// <summary>
/// #460 D12 / D14: moderation.json is the lowest-precedence IConfiguration source, every threshold is
/// overridable, and validation runs on the layered result against security.json.
/// </summary>
public class ModerationConfigurationTests : SparkTestDriver
{
    private static SparkModerationOptions Bind(IConfiguration configuration)
    {
        var options = new SparkModerationOptions();
        configuration.GetSection(SparkModerationConfigurationExtensions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void Moderation_json_is_overridden_by_appsettings_and_by_environment_variables()
    {
        var folder = Directory.CreateTempSubdirectory("spark-moderation-config-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "App_Data"));
            File.WriteAllText(Path.Combine(folder, "App_Data", "moderation.json"),
                """{ "Fraud": { "MaxVotesCastPerDay": 12, "SerialVotesIn24Hours": 9 }, "Reputation": { "UpvoteReceived": 5 } }""");
            const string prefix = "SPARK_MODTEST_";
            Environment.SetEnvironmentVariable(prefix + "Spark__Moderation__Fraud__MaxVotesCastPerDay", "7");
            try
            {
                // The app's sources first, like a WebApplicationBuilder; AddModeration inserts the file at 0.
                IConfigurationBuilder Sources(bool withEnvironment)
                {
                    var builder = new ConfigurationBuilder().SetBasePath(folder)
                        .AddInMemoryCollection(new Dictionary<string, string?> { ["Spark:Moderation:Fraud:MaxVotesCastPerDay"] = "20" });
                    if (withEnvironment)
                        builder.AddEnvironmentVariables(prefix);
                    return builder.AddSparkModerationFile();
                }

                var layered = Bind(Sources(withEnvironment: true).Build());
                var withoutEnvironment = Bind(Sources(withEnvironment: false).Build());
                var fileOnly = Bind(new ConfigurationBuilder().SetBasePath(folder).AddSparkModerationFile().Build());

                layered.Fraud.MaxVotesCastPerDay.Should().Be(7, "an environment variable beats appsettings and the file");
                withoutEnvironment.Fraud.MaxVotesCastPerDay.Should().Be(20, "appsettings beats the file");
                fileOnly.Fraud.MaxVotesCastPerDay.Should().Be(12);
                layered.Fraud.SerialVotesIn24Hours.Should().Be(9, "what nobody overrides comes from the file");
                layered.PointsFor(ReputationEventKinds.UpvoteReceived).Should().Be(5);
                layered.PointsFor(ReputationEventKinds.DownvoteReceived).Should().Be(-2, "a missing event keeps its default");
                layered.Fraud.MaxCreditedPairVotesPerDay.Should().Be(3, "a missing threshold keeps its default");
            }
            finally
            {
                Environment.SetEnvironmentVariable(prefix + "Spark__Moderation__Fraud__MaxVotesCastPerDay", null);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_library_defaults_sit_below_the_application_file_and_below_appsettings()
    {
        // Grill Q6: Moderation ships its reputation table and privileges as a layer; the application's
        // moderation.json composes on top per key ("Review": null removes a privilege), and the result
        // is one IConfiguration source below appsettings and environment variables.
        var folder = Directory.CreateTempSubdirectory("spark-moderation-layers-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "App_Data"));
            File.WriteAllText(Path.Combine(folder, "App_Data", "moderation.json"),
                """{ "Privileges": { "Review": null, "Flag": { "Rep": 50 } } }""");

            var fileOnly = Bind(new ConfigurationBuilder().SetBasePath(folder).AddSparkModerationFile().Build());
            var withAppSettings = Bind(new ConfigurationBuilder().SetBasePath(folder)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Spark:Moderation:Privileges:Flag:Rep"] = "60" })
                .AddSparkModerationFile().Build());

            fileOnly.Privileges.Keys.Should().BeEquivalentTo(["Upvote", "Flag", "Downvote"]); // The application removed Review.
            fileOnly.Privileges["Upvote"].Group.Should().Be("moderation:voters", "what the application does not state comes from the library");
            fileOnly.Privileges["Upvote"].Rep.Should().Be(10);
            fileOnly.Privileges["Flag"].Rep.Should().Be(50, "the application's file wins over the library");
            fileOnly.Privileges["Flag"].Group.Should().Be("moderation:flaggers", "it merges per key, not per privilege");
            withAppSettings.Privileges["Flag"].Rep.Should().Be(60, "appsettings wins over the composed file");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Without_an_application_file_the_library_defaults_apply()
    {
        var folder = Directory.CreateTempSubdirectory("spark-moderation-defaults-").FullName;
        try
        {
            var options = Bind(new ConfigurationBuilder().SetBasePath(folder).AddSparkModerationFile().Build());

            options.Privileges.Keys.Should().BeEquivalentTo(["Upvote", "Flag", "Downvote", "Review"]);
            options.Privileges["Review"].Group.Should().Be("moderation:reviewers");
            options.Privileges["Review"].Rep.Should().Be(500);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_privilege_slot_resolves_through_the_security_bindings()
    {
        var security = MoSecurity.Configuration();
        security.Bindings = new() { ["moderation:voters"] = ["MoVoters"], ["moderation:flaggers"] = [MoSecurity.Flaggers.ToString(), MoSecurity.Voters.ToString()] };
        var options = new SparkModerationOptions();
        options.Privileges["Upvote"] = new ModerationPrivilegeOptions { Group = "moderation:voters", Grants = ["Vote"] };
        options.Privileges["Flag"] = new ModerationPrivilegeOptions { Group = "moderation:flaggers", Grants = ["Flag"] };
        options.Privileges["Review"] = new ModerationPrivilegeOptions { Group = "moderation:reviewers", Grants = ["Review"] };
        options.Privileges["Token"] = new ModerationPrivilegeOptions { Group = "@authenticated" };

        ModerationStartupCheck.ResolveGroups(options, security);
        var problems = ModerationStartupCheck.Validate(options, security);

        options.Privileges["Upvote"].GroupId.Should().Be(MoSecurity.Voters, "bound by the group's name");
        problems.Should().Contain(p => p.Contains("'Flag'") && p.Contains("binds to 2 groups"));
        problems.Should().Contain(p => p.Contains("'Review'") && p.Contains("does not bind"), "an unbound slot refuses startup");
        problems.Should().Contain(p => p.Contains("'Token'") && p.Contains("a token or a group id is refused"));
        problems.Should().NotContain(p => p.Contains("'Upvote'"));
    }

    [Fact]
    public void A_valid_configuration_has_no_problems()
    {
        var options = new SparkModerationOptions();
        MoSecurity.OpenPrivileges(options);

        ModerationStartupCheck.Validate(options, MoSecurity.Configuration()).Should().BeEmpty();
    }

    [Fact]
    public void Unknown_reputation_events_are_refused()
    {
        var options = new SparkModerationOptions { Reputation = { ["UpvoteRecieved"] = 10 } };

        ModerationStartupCheck.Validate(options, MoSecurity.Configuration())
            .Should().ContainSingle().Which.Should().Contain("'UpvoteRecieved' is unknown");
    }

    [Fact]
    public void A_destructive_action_cannot_be_made_earnable()
    {
        var options = new SparkModerationOptions { Earnable = ["Purge", "Delete"] };

        ModerationStartupCheck.Validate(options, MoSecurity.Configuration())
            .Should().ContainSingle().Which.Should().Contain("'Purge', which is never earnable");
    }

    [Fact]
    public void A_privilege_group_must_exist_not_be_well_known_and_hold_only_earnable_rights()
    {
        var security = MoSecurity.Configuration(("Delete/MoPost", MoSecurity.Voters), ("Lock/MoPost", MoSecurity.Flaggers));
        var options = new SparkModerationOptions();
        options.Privileges["Upvote"] = new ModerationPrivilegeOptions { GroupId = MoSecurity.Voters };
        options.Privileges["Flag"] = new ModerationPrivilegeOptions { GroupId = MoSecurity.Flaggers };
        options.Privileges["Ghost"] = new ModerationPrivilegeOptions { GroupId = Guid.Parse("46129999-1111-4000-8000-000000000001") };
        options.Privileges["Everyone"] = new ModerationPrivilegeOptions { GroupId = SparkTestSecurity.AuthenticatedGroupId };

        var problems = ModerationStartupCheck.Validate(options, security);

        problems.Should().Contain(p => p.Contains("'Upvote'") && p.Contains("'Delete' is not earnable"));
        problems.Should().Contain(p => p.Contains("'Flag'") && p.Contains("'Lock' is never earnable"));
        problems.Should().Contain(p => p.Contains("'Ghost'") && p.Contains("does not declare"));
        problems.Should().Contain(p => p.Contains("'Ghost'") && p.Contains("has no grant"));
        problems.Should().Contain(p => p.Contains("'Everyone'") && p.Contains("well-known"));

        // The escape hatch admits a non-destructive action; it never admits a destructive one.
        options.Earnable.Add("Delete");
        ModerationStartupCheck.Validate(options, security).Should().NotContain(p => p.Contains("'Delete' is not earnable"));
    }

    [Fact]
    public async Task An_invalid_layered_configuration_refuses_startup()
    {
        var act = async () => await MoHost.StartAsync(Store, o => o.Reputation["NotAnEvent"] = 1);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("'NotAnEvent' is unknown");
    }

    [Fact]
    public async Task The_moderation_indexes_are_deployed_by_AddModeration()
    {
        await using var host = await MoHost.StartAsync(Store);

        var names = await Store.Maintenance.SendAsync(new GetIndexNamesOperation(0, 256));
        foreach (var expected in ModerationIndexNames.All)
            names.Should().Contain(expected, "deploy failures only log to the console, so this is the check that notices");
        foreach (var name in ModerationIndexNames.All)
            (await Store.Maintenance.SendAsync(new GetIndexErrorsOperation([name]))).Single().Errors.Should().BeEmpty();
    }

    [Fact]
    public void Init_prints_the_grants_per_moderatable_type_and_leaves_out_never_earnable_ones()
    {
        var options = new SparkModerationOptions();
        MoSecurity.OpenPrivileges(options);
        options.Privileges["Upvote"].Grants.Add("Purge");

        var report = SparkModerationInitExtensions.Render(options, ["MoPost", "MoAnswer"], new HashSet<string>());

        report.Should().Contain("\"resource\": \"Vote/MoPost\"").And.Contain("\"resource\": \"Vote/MoAnswer\"");
        report.Should().Contain("\"resource\": \"Review/Moderation\"");
        report.Should().Contain($"\"groupId\": \"{MoSecurity.Voters}\"");
        report.Should().Contain("'Purge', which is never earnable; it is left out");
        report.Should().Contain("\"resource\": \"Lock/MoPost\"").And.Contain("\"groupId\": \"moderation:moderators\"");
    }

    /// <summary>
    /// Composition M9: the library ships the Moderation pseudo-type's grants to its own slots, so the
    /// report leaves them out and says so. Read from the library's real embedded layer.
    /// </summary>
    [Fact]
    public void Init_leaves_out_the_rights_the_library_ships()
    {
        var shipped = SparkModerationInitExtensions.ShippedRights();
        shipped.Should().BeEquivalentTo(
        [
            "Review/Moderation|moderation:reviewers",
            "Review/Moderation|moderation:moderators",
            "Suspend/Moderation|moderation:moderators",
            "Audit/Moderation|moderation:moderators",
        ]);

        var options = new SparkModerationOptions();
        var library = new ConfigurationBuilder().AddSparkModerationFile().Build();
        library.GetSection(SparkModerationConfigurationExtensions.SectionName).Bind(options);
        var report = SparkModerationInitExtensions.Render(options, ["MoPost"], shipped);

        report.Should().NotContain("\"resource\": \"Review/Moderation\"")
            .And.NotContain("\"resource\": \"Suspend/Moderation\"")
            .And.NotContain("\"resource\": \"Audit/Moderation\"");
        report.Should().Contain("Shipped by the library").And.Contain("Suspend/Moderation to moderation:moderators");
        report.Should().Contain("\"resource\": \"Lock/MoPost\"").And.Contain("\"resource\": \"Vote/MoPost\"");
    }
}
