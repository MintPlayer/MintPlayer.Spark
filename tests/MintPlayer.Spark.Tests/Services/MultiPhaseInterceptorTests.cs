using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// One interceptor class may implement several phase interfaces (<c>ModerationInterceptor</c>,
/// <c>ContributionsInterceptor</c>, <c>SoftDeleteInterceptor</c> all do). <c>AddInterceptor&lt;T&gt;()</c>
/// registers it once and forwards it per phase, so each phase calls it exactly once per write, on the
/// one instance of the request.
/// <para>
/// The pipeline only calls the phases a class <b>declares</b>. A method named like a phase on a class
/// that does not implement that phase's interface compiles, looks right, and never runs — which is how
/// <c>ModerationInterceptor.OnAfterLoadAsync</c> stopped disabling Edit and Delete on a locked post after
/// #482 (M7) until <c>IAfterLoad</c> was declared. The second test refuses that shape everywhere.
/// </para>
/// </summary>
public class MultiPhaseInterceptorTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("46a1c7e0-4600-4600-4600-46a1c7e04601");

    [Fact]
    public async Task One_class_implementing_several_phases_is_called_once_per_phase_on_one_instance()
    {
        var log = new InterceptionLog();
        await using var factory = new SparkEndpointFactory<InterceptedContext>(
            Store,
            [InterceptedNoteModel.For(NoteTypeId)],
            configureServices: services => services.AddSingleton(log),
            configureSpark: spark => spark.AddInterceptor<AllPhasesInterceptor>());

        string id;
        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            var po = new PersistentObject { ObjectTypeId = NoteTypeId, Name = "InterceptedNote" };
            po.AddAttribute(new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = "multi", IsValueChanged = true });
            id = (await db.SavePersistentObjectAsync(po)).Id!;
        }
        Calls(log, "save").Should().Equal("BeforeSave", "AfterSave");
        Instances(log, "save").Should().ContainSingle("both save phases ran on the one instance of the request");

        using (var scope = factory.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IDatabaseAccess>().GetPersistentObjectAsync(NoteTypeId, id)).Should().NotBeNull();
        Calls(log, "load").Should().Equal("AfterLoad");

        using (var scope = factory.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDatabaseAccess>().DeletePersistentObjectAsync(NoteTypeId, id);
        Calls(log, "delete").Should().Equal("BeforeDelete", "AfterDelete");
        Instances(log, "delete").Should().ContainSingle();
    }

    [Fact]
    public void No_interceptor_declares_a_phase_method_without_the_phase_interface()
    {
        // Every assembly that ships interceptors and is referenced here, plus this one (fixtures).
        string[] names =
        [
            "MintPlayer.Spark", "MintPlayer.Spark.SoftDelete", "MintPlayer.Spark.History", "MintPlayer.Spark.Moderation",
            "MintPlayer.Spark.Contributions", "MintPlayer.Spark.Replication", "MintPlayer.Spark.IdentityProvider",
            "MintPlayer.Spark.Messaging", "MintPlayer.Spark.Authorization",
        ];
        var assemblies = names.Select(Assembly.Load).Append(typeof(MultiPhaseInterceptorTests).Assembly);

        var undeclared = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => Phases
                .Where(phase => DeclaresPhaseMethod(t, phase) && !phase.Interface.IsAssignableFrom(t))
                .Select(phase => $"{t.FullName}.{phase.Method} without {phase.Interface.Name}"))
            .ToList();

        undeclared.Should().BeEmpty("the pipeline never calls a phase the class does not declare");
    }

    private static readonly (string Method, Type Context, Type Interface)[] Phases =
    [
        (nameof(IBeforeSave.OnBeforeSaveAsync), typeof(SaveContext), typeof(IBeforeSave)),
        (nameof(IAfterSave.OnAfterSaveAsync), typeof(SaveContext), typeof(IAfterSave)),
        (nameof(IBeforeDelete.OnBeforeDeleteAsync), typeof(DeleteContext), typeof(IBeforeDelete)),
        (nameof(IAfterDelete.OnAfterDeleteAsync), typeof(DeleteContext), typeof(IAfterDelete)),
        (nameof(IDeleteReplacement.ReplaceAsync), typeof(DeleteContext), typeof(IDeleteReplacement)),
        (nameof(IAfterLoad.OnAfterLoadAsync), typeof(LoadContext), typeof(IAfterLoad)),
        (nameof(IAfterMaterialize.OnAfterMaterializeAsync), typeof(MaterializeContext), typeof(IAfterMaterialize)),
        (nameof(INaturalIdCollision.OnNaturalIdCollisionAsync), typeof(NaturalIdCollisionContext), typeof(INaturalIdCollision)),
    ];

    private static bool DeclaresPhaseMethod(Type type, (string Method, Type Context, Type Interface) phase)
        => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == phase.Method && m.GetParameters() is { Length: > 0 } p && p[^1].ParameterType == phase.Context);

    private static string[] Calls(InterceptionLog log, string operation)
        => [.. log.Entries.Where(e => e.StartsWith(operation + ":")).Select(e => e.Split(':')[1])];

    private static string[] Instances(InterceptionLog log, string operation)
        => [.. log.Entries.Where(e => e.StartsWith(operation + ":")).Select(e => e.Split(':')[2]).Distinct()];
}

/// <summary>Every write and read phase on one class, the shape <c>ModerationInterceptor</c> has.</summary>
public sealed class AllPhasesInterceptor(InterceptionLog log) : IBeforeSave, IAfterSave, IBeforeDelete, IAfterDelete, IAfterLoad
{
    private readonly string instance = Guid.NewGuid().ToString("N");

    public bool AppliesTo(Type entityType) => entityType == typeof(InterceptedNote);

    public ValueTask OnBeforeSaveAsync(SaveContext context) => Record("save", "BeforeSave");
    public ValueTask OnAfterSaveAsync(SaveContext context) => Record("save", "AfterSave");
    public ValueTask OnBeforeDeleteAsync(DeleteContext context) => Record("delete", "BeforeDelete");
    public ValueTask OnAfterDeleteAsync(DeleteContext context) => Record("delete", "AfterDelete");
    public ValueTask OnAfterLoadAsync(LoadContext context) => Record("load", "AfterLoad");

    private ValueTask Record(string operation, string phase)
    {
        log.Entries.Add($"{operation}:{phase}:{instance}");
        return ValueTask.CompletedTask;
    }
}
