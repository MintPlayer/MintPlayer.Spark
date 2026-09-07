using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Pins the public surface of <see cref="SparkExtensions"/> beyond <c>AddSpark/UseSpark</c>
/// itself: the configuration-aware <c>AddSpark</c> overload, the actions registration helper,
/// the <c>UseSpark(opts =&gt; ...)</c> options shape, and the model-synchronization helpers.
/// These are thin wrappers but each one is a discrete public API surface — a regression
/// breaks Demo apps that compose them in unique combinations.
/// </summary>
public class SparkExtensionsTests
{
    // --- AddSpark(IConfiguration) overload ------------------------------

    [Fact]
    public void AddSpark_with_configuration_binds_Spark_section_to_builder_options_before_configure_runs()
    {
        // The overload binds configuration.GetSection("Spark") to builder.Options *before*
        // invoking configure(builder). Pin that ordering — modules registered via configure
        // are entitled to read RavenDb settings off builder.Options.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spark:RavenDb:Database"] = "Bound",
                ["Spark:RavenDb:MaxConnectionRetries"] = "0",
                ["Spark:RavenDb:EnsureDatabaseCreated"] = "false",
            })
            .Build();

        SparkOptions? observedOptions = null;
        services.AddSpark(configuration, builder =>
        {
            observedOptions = ((SparkBuilder)builder).Options;
        });

        observedOptions.Should().NotBeNull();
        observedOptions!.RavenDb.Database.Should().Be("Bound");
        observedOptions.RavenDb.MaxConnectionRetries.Should().Be(0);
    }

    [Fact]
    public void AddSpark_with_configuration_invokes_the_configure_callback_with_a_builder()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var captured = false;

        services.AddSpark(configuration, builder =>
        {
            captured = true;
            builder.Should().NotBeNull();
            builder.Configuration.Should().BeSameAs(configuration);
        });

        captured.Should().BeTrue();
    }

    // --- AddSparkActions<TActions, TEntity> -----------------------------

    [Fact]
    public void AddSparkActions_registers_actions_class_under_the_typed_interface_and_concrete()
    {
        var services = new ServiceCollection();

        services.AddSparkActions<TestPersonActions, Person>();

        services.Should().Contain(d =>
            d.ServiceType == typeof(IPersistentObjectActions<Person>) &&
            d.ImplementationType == typeof(TestPersonActions) &&
            d.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(d =>
            d.ServiceType == typeof(TestPersonActions) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddSparkActions_returns_the_service_collection_for_chaining()
    {
        var services = new ServiceCollection();

        var returned = services.AddSparkActions<TestPersonActions, Person>();

        returned.Should().BeSameAs(services);
    }

    // --- SynchronizeSparkModelsIfRequested (builder phase) --------------

    [Fact]
    public void SynchronizeSparkModelsIfRequested_returns_false_when_the_flag_is_absent()
    {
        // False means "no command was handled, carry on and start the app" — the path every
        // boot takes. It must not touch the model directory.
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();
        builder.Services.AddScoped<SparkContext, EmptyTestSparkContext>();

        var handled = builder.SynchronizeSparkModelsIfRequested(["--unrelated", "--verbose"]);

        handled.Should().BeFalse();
        Directory.Exists(Path.Combine(scratch.Path, "App_Data", "Model")).Should().BeFalse(
            "no command was requested, so nothing should have been written");
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_with_empty_args_returns_false()
    {
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();

        builder.SynchronizeSparkModelsIfRequested([]).Should().BeFalse();
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_writes_the_model_without_any_database()
    {
        // The point of the builder-phase move: no IDocumentStore is ever resolved, so this runs
        // in CI where no RavenDB exists. If a connection creeps back in, this test hangs or throws
        // rather than passing quietly.
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();
        builder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();

        var handled = builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);

        handled.Should().BeTrue("the command was handled, so the host must return instead of starting");
        Environment.ExitCode.Should().Be(0);
        File.Exists(Path.Combine(scratch.Path, "App_Data", "Model", "SyncProbe.json")).Should().BeTrue();
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_reports_a_missing_context_registration_and_fails_the_run()
    {
        // A merge queue must not see exit 0 from a run that did nothing — that is a green gate
        // that never ran, which is the failure mode this whole change exists to remove.
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();

        var handled = builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);

        handled.Should().BeTrue();
        Environment.ExitCode.Should().Be(2);
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_generic_overload_does_not_need_a_registration()
    {
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();

        var handled = builder.SynchronizeSparkModelsIfRequested<OneEntityTestSparkContext>(
            ["--spark-synchronize-model"]);

        handled.Should().BeTrue();
        File.Exists(Path.Combine(scratch.Path, "App_Data", "Model", "SyncProbe.json")).Should().BeTrue();
    }

    [Fact]
    public void Verify_mode_reports_a_synchronized_model_as_in_sync_and_writes_nothing()
    {
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();
        builder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();
        builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);

        var modelDir = Path.Combine(scratch.Path, "App_Data", "Model");
        var before = Directory.GetFiles(modelDir).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToArray();

        var verifyBuilder = scratch.CreateBuilder();
        verifyBuilder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();
        var handled = verifyBuilder.SynchronizeSparkModelsIfRequested(["--spark-verify-model"]);

        handled.Should().BeTrue();
        Environment.ExitCode.Should().Be(0);
        Directory.GetFiles(modelDir).Select(f => (f, File.GetLastWriteTimeUtc(f))).Should().BeEquivalentTo(before,
            because: "verify must leave the workspace exactly as the pull request left it");
    }

    [Fact]
    public void Verify_mode_exits_3_when_the_model_has_drifted()
    {
        // The merge-queue gate: entities changed, model not regenerated.
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();
        builder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();
        builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);

        File.WriteAllText(
            Path.Combine(scratch.Path, "App_Data", "Model", "Planted.json"),
            "{ \"persistentObject\": { \"name\": \"Planted\", \"clrType\": \"X.Planted\" }, \"queries\": [] }");

        var verifyBuilder = scratch.CreateBuilder();
        verifyBuilder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();
        verifyBuilder.SynchronizeSparkModelsIfRequested(["--spark-verify-model"]);

        Environment.ExitCode.Should().Be(3);
    }

    [Fact]
    public void Verify_mode_exits_3_when_the_hash_file_is_missing()
    {
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();
        builder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();
        builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);
        File.Delete(Path.Combine(scratch.Path, "App_Data", "modelHashes.json"));

        var verifyBuilder = scratch.CreateBuilder();
        verifyBuilder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();
        verifyBuilder.SynchronizeSparkModelsIfRequested(["--spark-verify-model"]);

        Environment.ExitCode.Should().Be(3);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void A_production_start_without_the_flag_never_writes_a_model(string environmentName)
    {
        // The question this answers: can a deployed application rewrite its own model? Only the
        // explicit CLI flag reaches the synchronizer, so an ordinary start does nothing at all.
        using var scratch = new ScratchContentRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = scratch.Path,
            EnvironmentName = environmentName,
        });
        builder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();

        // The arguments a deployed app actually starts with.
        var handled = builder.SynchronizeSparkModelsIfRequested(["--urls", "http://0.0.0.0:8080"]);

        handled.Should().BeFalse("no Spark command was requested, so the host must start normally");
        Directory.Exists(Path.Combine(scratch.Path, "App_Data", "Model")).Should().BeFalse(
            "an ordinary production start must not write model files");
        Environment.ExitCode.Should().Be(0);
    }

    // --- IModelSynchronizer is a development-only service ----------------

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void AddSpark_does_not_register_the_model_synchronizer_outside_Development(string environmentName)
    {
        // The security property, made structural: outside Development there is nothing in the
        // container to resolve, so app code cannot drive a model rewrite by reaching past the
        // build-time command. Previously [Register] put it in every app in every environment.
        using var scratch = new ScratchContentRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = scratch.Path,
            EnvironmentName = environmentName,
        });

        builder.Services.AddSpark(spark => spark.UseContext<EmptyTestSparkContext>());

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetService<IModelSynchronizer>().Should().BeNull(
            $"the synchronizer is a build-time tool and must not be resolvable in {environmentName}");
    }

    [Fact]
    public void AddSpark_registers_the_model_synchronizer_in_Development()
    {
        using var scratch = new ScratchContentRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = scratch.Path,
            EnvironmentName = Environments.Development,
        });

        builder.Services.AddSpark(spark => spark.UseContext<EmptyTestSparkContext>());

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetService<IModelSynchronizer>().Should().NotBeNull();
    }

    // --- helpers --------------------------------------------------------

    /// <summary>
    /// A throwaway content root plus a <see cref="WebApplicationBuilder"/> rooted at it, so
    /// synchronization writes into the temp directory rather than the test host's own folder.
    /// Also restores <see cref="Environment.ExitCode"/>, which these tests deliberately set.
    /// </summary>
    // ---------------------------------------------------------------- issue #374
    //
    // A program unit names its target twice -- an id and an alias -- and only the id used to be
    // checked, while the client routes by the ALIAS. A mismatch 404s at runtime, and that 404 is
    // deliberately byte-identical to the one an unauthorized caller gets, so the symptom carries no
    // information at all. These pin the check that turns it into a build failure.

    /// <summary>
    /// Writes a `programUnits.json` with a single query unit at <paramref name="unitAlias"/>, and
    /// returns the id of the query it points at.
    /// </summary>
    /// <remarks>
    /// <c>TranslatedString</c> serializes FLAT -- <c>{ "en": "..." }</c>, not
    /// <c>{ "translations": { "en": "..." } }</c>. Getting that wrong makes the whole file
    /// unparseable, and the check under test deliberately swallows a JsonException (FR5, because
    /// ProgramUnitsLoader owns that error) -- so a malformed fixture presents as the check simply
    /// not firing. That cost a debugging round: read a real App_Data/programUnits.json before
    /// inventing one.
    /// </remarks>
    private static void PlantQueryUnit(string contentRoot, string? unitAlias, Guid queryId)
    {
        var aliasLine = unitAlias is null ? string.Empty : $"\"alias\": \"{unitAlias}\",";
        File.WriteAllText(
            Path.Combine(contentRoot, "App_Data", "programUnits.json"),
            $$"""
            {
              "programUnitGroups": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": { "en": "Group" },
                  "order": 1,
                  "programUnits": [
                    {
                      "id": "22222222-2222-2222-2222-222222222222",
                      "name": { "en": "The unit" },
                      "type": "query",
                      "queryId": "{{queryId}}",
                      {{aliasLine}}
                      "order": 1
                    }
                  ]
                }
              ]
            }
            """);
    }

    /// <summary>The id and alias of the one query a synchronized `OneEntityTestSparkContext` produces.</summary>
    private static (Guid Id, string Alias) TheOnlyQuery(string contentRoot)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(contentRoot, "App_Data", "Model"), "*.json"))
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<MintPlayer.Spark.Abstractions.EntityTypeFile>(
                File.ReadAllText(file),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed?.Queries is { Length: > 0 } queries)
            {
                var query = queries[0];
                return (query.Id, query.Alias ?? MintPlayer.Spark.Abstractions.SparkQueryAliases.Derive(query.Name));
            }
        }

        throw new InvalidOperationException("The synchronized model produced no query to point a unit at.");
    }

    /// <remarks>
    /// Call this AFTER planting any App_Data config. The model hash covers
    /// <c>programUnits.json</c>, so a file written afterwards drifts the hash and the verify run
    /// exits 3 for a reason that has nothing to do with aliases -- which is exactly how these tests
    /// first failed.
    /// </remarks>
    private static string Synchronized(ScratchContentRoot scratch)
    {
        var builder = scratch.CreateBuilder();
        builder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();
        builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);
        return scratch.Path;
    }

    /// <remarks>
    /// Returns the reported text as well as the code, because the code alone is a weak assertion:
    /// several checks and the hash comparison all exit 3, so "it failed" does not establish that it
    /// failed for the reason under test.
    /// </remarks>
    private static (int ExitCode, string Reported) Verify(ScratchContentRoot scratch)
    {
        Environment.ExitCode = 0;
        var captured = new StringWriter();
        var previous = Console.Error;
        Console.SetError(captured);
        try
        {
            var verifyBuilder = scratch.CreateBuilder();
            verifyBuilder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();
            verifyBuilder.SynchronizeSparkModelsIfRequested(["--spark-verify-model"]);
        }
        finally
        {
            Console.SetError(previous);
        }

        return (Environment.ExitCode, captured.ToString());
    }

    private static int VerifyExitCode(ScratchContentRoot scratch) => Verify(scratch).ExitCode;

    [Fact]
    public void A_program_unit_whose_alias_matches_its_target_verifies()
    {
        using var scratch = new ScratchContentRoot();
        var query = TheOnlyQuery(Synchronized(scratch));
        PlantQueryUnit(scratch.Path, query.Alias, query.Id);
        Synchronized(scratch);

        VerifyExitCode(scratch).Should().Be(0,
            "the unit routes to the alias its own query resolves to");
    }

    /// <summary>
    /// The reported bug, reduced: the alias is the hyphenated form someone would naturally choose
    /// for a URL, while the query derives its own from the name.
    /// </summary>
    [Fact]
    public void A_program_unit_whose_alias_resolves_to_nothing_exits_3()
    {
        using var scratch = new ScratchContentRoot();
        var query = TheOnlyQuery(Synchronized(scratch));
        PlantQueryUnit(scratch.Path, query.Alias + "-typo", query.Id);
        Synchronized(scratch);

        var (exitCode, reported) = Verify(scratch);

        exitCode.Should().Be(3,
            "the client fetches /spark/queries/{alias}, so this unit would 404 at runtime");
        reported.Should().Contain("routes to alias",
            "the failure must be THIS check rather than an unrelated one that also exits 3");
        reported.Should().Contain(query.Alias,
            "the message has to name the alias the target actually resolves to, or the reader " +
            "cannot tell which of the two identifiers to change");
    }

    /// <summary>
    /// No alias is a supported shape, not an omission — the client then routes by id. A check that
    /// warned here would fire on every unit that does the simple thing.
    /// </summary>
    [Fact]
    public void A_program_unit_with_no_alias_verifies()
    {
        using var scratch = new ScratchContentRoot();
        var query = TheOnlyQuery(Synchronized(scratch));
        PlantQueryUnit(scratch.Path, null, query.Id);
        Synchronized(scratch);

        VerifyExitCode(scratch).Should().Be(0);
    }

    /// <summary>
    /// A `url` unit has no server-side target, so its alias resolves to nothing by definition.
    /// </summary>
    [Fact]
    public void A_url_program_unit_is_not_alias_checked()
    {
        using var scratch = new ScratchContentRoot();
        Synchronized(scratch);
        File.WriteAllText(
            Path.Combine(scratch.Path, "App_Data", "programUnits.json"),
            """
            {
              "programUnitGroups": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": { "en": "Group" },
                  "order": 1,
                  "programUnits": [
                    {
                      "id": "33333333-3333-3333-3333-333333333333",
                      "name": { "en": "Docs" },
                      "type": "url",
                      "url": "https://example.invalid",
                      "alias": "resolves-to-nothing",
                      "order": 1
                    }
                  ]
                }
              ]
            }
            """);
        Synchronized(scratch);

        VerifyExitCode(scratch).Should().Be(0);
    }

    /// <summary>
    /// An app may ship no menu at all, so an absent file cannot be a failure.
    /// </summary>
    [Fact]
    public void A_missing_programUnits_file_verifies()
    {
        using var scratch = new ScratchContentRoot();
        Synchronized(scratch);

        VerifyExitCode(scratch).Should().Be(0);
    }

    /// <summary>
    /// Malformed JSON belongs to `ProgramUnitsLoader`, which reports it with its own message.
    /// Reporting it twice, differently, tells the reader less rather than more.
    /// </summary>
    [Fact]
    public void A_malformed_programUnits_file_is_not_this_checks_error()
    {
        using var scratch = new ScratchContentRoot();
        Synchronized(scratch);
        File.WriteAllText(Path.Combine(scratch.Path, "App_Data", "programUnits.json"), "{ not json");
        Synchronized(scratch);

        VerifyExitCode(scratch).Should().Be(0);
    }

    private sealed class ScratchContentRoot : IDisposable
    {
        private readonly int _previousExitCode = Environment.ExitCode;

        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "spark-sync-tests-" + Guid.NewGuid().ToString("N"));

        public ScratchContentRoot() => Directory.CreateDirectory(Path);

        public WebApplicationBuilder CreateBuilder() =>
            WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path });

        public void Dispose()
        {
            Environment.ExitCode = _previousExitCode;
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    public sealed class Person
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class TestPersonActions : DefaultPersistentObjectActions<Person>
    {
        public TestPersonActions(IEntityMapper mapper) : base(mapper) { }
    }

    public sealed class EmptyTestSparkContext : SparkContext { }

    public sealed class SyncProbe
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>
    /// Declares one queryable root so synchronization has something to write. The getter is never
    /// invoked — only its property type is read — which is why a null <c>Session</c> is safe here.
    /// </summary>
    public sealed class OneEntityTestSparkContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<SyncProbe> SyncProbes => Session.Query<SyncProbe>();
    }
}
