using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// <see cref="SparkOptions.ModelAttribute"/> and <c>spark.ModelAttributes(...)</c>
/// (<c>docs/default_index_PRD.md</c>, D9; SP5). The synchronize command reads the hook off the
/// <see cref="SparkOptions"/> singleton AddSpark registers, so the builder extension must write to that
/// very instance.
/// </summary>
public class ModelAttributeOptionTests
{
    private sealed class Audited
    {
        public string? Name { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? CreatedAtUtc { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTimeOffset? ModifiedAt { get; set; }
        public string? CreatedByline { get; set; }
    }

    [Theory]
    [InlineData(nameof(Audited.CreatedBy), ModelAttributeSeed.Hidden)]
    [InlineData(nameof(Audited.CreatedAtUtc), ModelAttributeSeed.Hidden)]
    [InlineData(nameof(Audited.CreatedAt), ModelAttributeSeed.Hidden)]
    [InlineData(nameof(Audited.ModifiedBy), ModelAttributeSeed.Hidden)]
    [InlineData(nameof(Audited.ModifiedAt), ModelAttributeSeed.Hidden)]
    [InlineData(nameof(Audited.Name), ModelAttributeSeed.Default)]
    [InlineData(nameof(Audited.CreatedByline), ModelAttributeSeed.Default)]
    public void The_default_hook_hides_audit_members_by_name(string property, ModelAttributeSeed expected)
    {
        var hook = new SparkOptions().ModelAttribute;

        hook(typeof(Audited), typeof(Audited).GetProperty(property)!).Should().Be(expected);
    }

    [Fact]
    public void ModelAttributes_replaces_the_hook_on_the_registered_options_instance()
    {
        var services = new ServiceCollection();
        services.AddSpark(new ConfigurationBuilder().Build(), spark => spark.ModelAttributes((_, _) => ModelAttributeSeed.Exclude));

        var registered = services.Last(d => d.ServiceType == typeof(SparkOptions)).ImplementationInstance as SparkOptions;

        registered.Should().NotBeNull();
        registered!.ModelAttribute(typeof(Audited), typeof(Audited).GetProperty(nameof(Audited.Name))!)
            .Should().Be(ModelAttributeSeed.Exclude);
    }
}
