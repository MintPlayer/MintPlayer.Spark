using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The one watcher policy every application layer reloads through (composition D8): debounced, the
/// snapshot swapped whole, and a reload that does not compose keeping the previous snapshot (or, for
/// rights, refusing every read until it composes again).
/// </summary>
public sealed class AppLayerSnapshotTests : IDisposable
{
    private readonly string _tempDir;

    public AppLayerSnapshotTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spark-app-layer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* watcher locks — best-effort */ }
    }

    /// <summary>A failure bound, never an expected duration: the event normally arrives within the debounce.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed class Counter
    {
        public int Compositions;
        public Func<object> Next = () => new object();
        public object Compose() { Interlocked.Increment(ref Compositions); return Next(); }
    }

    private AppLayerSnapshot<object> Snapshot(Counter counter, AppLayerFailure onFailure = AppLayerFailure.KeepPrevious, string? directory = null, TimeSpan? debounce = null)
        => new("test layer", directory, ["layer.json"], counter.Compose, NullLogger.Instance, onFailure, debounce: debounce);

    private static Task Reloaded<T>(AppLayerSnapshot<T> snapshot) where T : class
    {
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        snapshot.Reloaded += () => reloaded.TrySetResult();
        return reloaded.Task;
    }

    [Fact]
    public void The_first_read_composes_once_and_later_reads_share_the_snapshot()
    {
        var counter = new Counter();
        using var snapshot = Snapshot(counter);

        var first = snapshot.Current;

        snapshot.Current.Should().BeSameAs(first);
        counter.Compositions.Should().Be(1);
    }

    [Fact]
    public void A_reload_swaps_the_snapshot_whole_and_raises_Reloaded()
    {
        var counter = new Counter();
        using var snapshot = Snapshot(counter);
        var first = snapshot.Current;
        var raised = 0;
        snapshot.Reloaded += () => raised++;

        snapshot.Reload().Should().BeTrue();

        snapshot.Current.Should().NotBeSameAs(first);
        raised.Should().Be(1);
    }

    [Fact]
    public async Task A_burst_of_signals_composes_once_after_the_debounce()
    {
        var counter = new Counter();
        using var snapshot = Snapshot(counter, debounce: TimeSpan.FromMilliseconds(200));
        _ = snapshot.Current;
        var reloaded = Reloaded(snapshot);

        // An editor's save: a write, a size change, a rename through a temporary file.
        for (var i = 0; i < 10; i++)
            snapshot.Signal();

        await reloaded.WaitAsync(Bound);
        // A second, late reload would land one debounce after the first; give it the room to show up.
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        counter.Compositions.Should().Be(2, "ten signals inside one debounce are one reload");
    }

    [Fact]
    public void A_reload_that_does_not_compose_keeps_the_previous_snapshot()
    {
        var counter = new Counter();
        using var snapshot = Snapshot(counter);
        var first = snapshot.Current;
        var raised = false;
        snapshot.Reloaded += () => raised = true;

        counter.Next = () => throw new InvalidOperationException("half-saved");

        snapshot.Reload().Should().BeFalse();
        snapshot.Current.Should().BeSameAs(first, "a half-saved edit must not take the running layer away");
        raised.Should().BeFalse();
    }

    [Fact]
    public void Refuse_fails_closed_until_a_reload_composes_again()
    {
        var counter = new Counter();
        using var snapshot = Snapshot(counter, AppLayerFailure.Refuse);
        _ = snapshot.Current;

        counter.Next = () => throw new InvalidOperationException("an unknown attribute");
        snapshot.Reload().Should().BeFalse();

        var read = () => snapshot.Current;
        read.Should().Throw<InvalidOperationException>().WithMessage("an unknown attribute");
        read.Should().Throw<InvalidOperationException>("every read refuses, not just the first");

        var fixedOne = new object();
        counter.Next = () => fixedOne;
        snapshot.Reload().Should().BeTrue();
        snapshot.Current.Should().BeSameAs(fixedOne);
    }

    [Fact]
    public void A_first_composition_that_fails_throws_and_is_tried_again_on_the_next_read()
    {
        var counter = new Counter { Next = () => throw new InvalidOperationException("broken at startup") };
        using var snapshot = Snapshot(counter);

        var read = () => snapshot.Current;
        read.Should().Throw<InvalidOperationException>();

        counter.Next = () => new object();
        read.Should().NotThrow();
    }

    [Fact]
    public async Task Writing_a_watched_file_reloads_the_layer()
    {
        var path = Path.Combine(_tempDir, "layer.json");
        File.WriteAllText(path, "{}");
        var counter = new Counter();
        using var snapshot = Snapshot(counter, directory: _tempDir);
        _ = snapshot.Current;
        var reloaded = Reloaded(snapshot);

        File.WriteAllText(path, """{ "changed": true }""");

        await reloaded.WaitAsync(Bound);
        counter.Compositions.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Dispose_is_idempotent_and_stops_reloads()
    {
        var counter = new Counter();
        var snapshot = Snapshot(counter);
        _ = snapshot.Current;

        snapshot.Dispose();
        var again = () => snapshot.Dispose();

        again.Should().NotThrow();
        snapshot.Reload().Should().BeFalse();
    }

    [Fact]
    public void A_translations_reload_recomposes_a_dependent_layer_at_once()
    {
        var host = Substitute.For<IHostEnvironment>();
        host.ContentRootPath.Returns(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "App_Data"));
        using var translations = TranslationsLoader.For(host, []);
        var counter = new Counter();
        using var snapshot = new AppLayerSnapshot<object>("dependent", null, [], counter.Compose, NullLogger.Instance, labels: translations);
        var first = snapshot.Current;

        translations.Reload().Should().BeTrue();

        snapshot.Current.Should().NotBeSameAs(first, "labels resolved against the translations follow their reload");
    }
}
