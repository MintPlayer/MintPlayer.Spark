using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage.InMemory;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// The route binder (#536 D23 rule 3, D7): an <see cref="Id{T}"/> action parameter is canonicalised through the app
/// model, so a hand-typed <c>ABC</c> in a URL finds the same entity as <c>abc</c>, and text that is not an id of the
/// entity is a model error (400), never an id of another collection.
/// </summary>
public class SparkIdModelBinderTests
{
    public class BoundPerson
    {
        public Id<BoundPerson> Id { get; set; }

        internal string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = Id<BoundPerson>.FromTrusted(value); }
    }

    private readonly IServiceProvider services;
    private readonly ISparkIds ids;

    public SparkIdModelBinderTests()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<ISparkStore>(new InMemorySparkStore(new InMemoryDatabase()));
        collection.AddOptions();
        collection.AddSingleton<ISparkIds, SparkIds>();
        services = collection.BuildServiceProvider();
        ids = services.GetRequiredService<ISparkIds>();
    }

    private async Task<ModelBindingContext> BindAsync(Type modelType, string? routeValue)
    {
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(modelType);
        var binder = new SparkIdModelBinderProvider().GetBinder(new Context(metadata))
            ?? throw new InvalidOperationException($"No binder for {modelType}.");
        var routeValues = new RouteValueDictionary();
        if (routeValue is not null) routeValues["id"] = routeValue;
        var context = new DefaultModelBindingContext
        {
            ActionContext = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } },
            ModelMetadata = metadata,
            ModelName = "id",
            ModelState = new ModelStateDictionary(),
            ValueProvider = new RouteValueProvider(BindingSource.Path, routeValues),
        };
        await binder.BindModelAsync(context);
        return context;
    }

    [Fact]
    public async Task The_whole_id_in_any_case_binds_to_the_canonical_id()
    {
        var canonical = ids.Parse<BoundPerson>("42");

        var context = await BindAsync(typeof(Id<BoundPerson>), canonical.Value.ToUpperInvariant());

        context.Result.IsModelSet.Should().BeTrue();
        context.Result.Model.Should().Be(canonical);
        context.ModelState.ErrorCount.Should().Be(0);
    }

    [Fact]
    public async Task The_key_alone_binds_too()
    {
        var context = await BindAsync(typeof(Id<BoundPerson>), "042");

        context.Result.Model.Should().Be(ids.Parse<BoundPerson>("42"));
    }

    [Fact]
    public async Task Another_collections_id_is_a_model_error()
    {
        var context = await BindAsync(typeof(Id<BoundPerson>), "companies/1");

        context.Result.IsModelSet.Should().BeFalse();
        context.ModelState.IsValid.Should().BeFalse();
        context.ModelState["id"]!.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task A_missing_value_leaves_the_parameter_unbound_without_an_error()
    {
        var context = await BindAsync(typeof(Id<BoundPerson>), null);

        context.Result.IsModelSet.Should().BeFalse();
        context.ModelState.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_nullable_id_binds_the_same_way()
    {
        var context = await BindAsync(typeof(Id<BoundPerson>?), "42");

        context.Result.Model.Should().Be(ids.Parse<BoundPerson>("42"));
    }

    [Fact]
    public void Only_ids_get_the_binder()
        => new SparkIdModelBinderProvider().GetBinder(new Context(new EmptyModelMetadataProvider().GetMetadataForType(typeof(string))))
            .Should().BeNull();

    private sealed class Context(ModelMetadata metadata) : ModelBinderProviderContext
    {
        public override BindingInfo BindingInfo { get; } = new();
        public override ModelMetadata Metadata { get; } = metadata;
        public override IModelMetadataProvider MetadataProvider { get; } = new EmptyModelMetadataProvider();
        public override IModelBinder CreateBinder(ModelMetadata metadata) => throw new NotSupportedException();
    }
}
