using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Staging on <see cref="SparkModuleRegistry"/>. Before stages existed, every registration ran at one
/// point — the end of <c>UseSpark()</c>, behind authentication — and a registrant that needed to run
/// earlier had no way to say so.
/// <para>
/// The properties worth pinning are not "the enum has two values" but the two silent failures the
/// design exists to prevent: middleware registered too late vanishing without a word, and a caller
/// applying one stage while quietly dropping the other.
/// </para>
/// </summary>
public class SparkMiddlewareStageTests
{
    [Fact]
    public void AfterSpark_is_the_default_stage_so_existing_registrants_do_not_move()
    {
        // The five module registrants that predate stages all call the single-argument overload and
        // are all correct behind authentication. If the default ever flipped, certificate forwarding
        // and the identity-provider middleware would silently start running before any credential
        // was validated.
        var registry = new SparkModuleRegistry();
        var ran = new List<string>();
        registry.AddMiddleware(_ => ran.Add("legacy"));

        registry.ApplyMiddleware(NewAppBuilder(), SparkMiddlewareStage.BeforeAuthentication);
        ran.Should().BeEmpty("the default must not be BeforeAuthentication");

        registry.ApplyMiddleware(NewAppBuilder(), SparkMiddlewareStage.AfterSpark);
        ran.Should().Equal("legacy");
    }

    [Fact]
    public void Default_enum_value_is_AfterSpark()
    {
        // Zero-value choice is load-bearing: default(SparkMiddlewareStage) must mean "where middleware
        // has always run", not a relocation nobody asked for.
        default(SparkMiddlewareStage).Should().Be(SparkMiddlewareStage.AfterSpark);
    }

    [Fact]
    public void Each_stage_runs_only_its_own_registrants()
    {
        var registry = new SparkModuleRegistry();
        var ran = new List<string>();
        registry.AddMiddleware(_ => ran.Add("early"), SparkMiddlewareStage.BeforeAuthentication);
        registry.AddMiddleware(_ => ran.Add("late"), SparkMiddlewareStage.AfterSpark);

        registry.ApplyMiddleware(NewAppBuilder(), SparkMiddlewareStage.BeforeAuthentication);
        ran.Should().Equal("early");

        registry.ApplyMiddleware(NewAppBuilder(), SparkMiddlewareStage.AfterSpark);
        ran.Should().Equal("early", "late");
    }

    [Fact]
    public void Registration_order_is_preserved_within_a_stage()
    {
        // A stage picks a side of authentication; it is not a general ordering mechanism, and modules
        // registered in one stage still compose in the order they were added.
        var registry = new SparkModuleRegistry();
        var ran = new List<string>();
        registry.AddMiddleware(_ => ran.Add("first"), SparkMiddlewareStage.BeforeAuthentication);
        registry.AddMiddleware(_ => ran.Add("second"), SparkMiddlewareStage.BeforeAuthentication);

        registry.ApplyMiddleware(NewAppBuilder(), SparkMiddlewareStage.BeforeAuthentication);

        ran.Should().Equal("first", "second");
    }

    [Theory]
    [InlineData(SparkMiddlewareStage.BeforeAuthentication)]
    [InlineData(SparkMiddlewareStage.AfterSpark)]
    public void Registering_into_an_already_applied_stage_throws(SparkMiddlewareStage stage)
    {
        // The request is unsatisfiable — the pipeline is past the point asked for — and without the
        // guard it is a silent no-op. That failure has bitten this repo before, which is why
        // AddIndexAssembly carries a doc comment about declarations arriving too late.
        var registry = new SparkModuleRegistry();
        registry.ApplyMiddleware(NewAppBuilder(), stage);

        var act = () => registry.AddMiddleware(_ => { }, stage);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*{stage}*");
    }

    [Fact]
    public void Applying_one_stage_does_not_close_the_other()
    {
        // UseSpark applies BeforeAuthentication first and AfterSpark last, with the whole Spark
        // pipeline built in between. A module registering during that window — from inside an
        // earlier middleware callback, say — must still be able to reach the later stage.
        var registry = new SparkModuleRegistry();
        registry.ApplyMiddleware(NewAppBuilder(), SparkMiddlewareStage.BeforeAuthentication);

        var ran = false;
        var act = () => registry.AddMiddleware(_ => ran = true, SparkMiddlewareStage.AfterSpark);

        act.Should().NotThrow();
        registry.ApplyMiddleware(NewAppBuilder(), SparkMiddlewareStage.AfterSpark);
        ran.Should().BeTrue();
    }

    [Fact]
    public void Applying_a_stage_with_no_registrants_is_a_no_op()
    {
        // UseSpark applies both stages unconditionally, and most apps register nothing early.
        var registry = new SparkModuleRegistry();

        var act = () => registry.ApplyMiddleware(NewAppBuilder(), SparkMiddlewareStage.BeforeAuthentication);

        act.Should().NotThrow();
    }

    private static IApplicationBuilder NewAppBuilder()
        => new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
}
