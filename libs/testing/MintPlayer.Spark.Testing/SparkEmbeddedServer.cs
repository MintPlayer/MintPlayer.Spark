using Raven.Client.Documents;
using Raven.Embedded;
using Raven.TestDriver;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Configures the one embedded RavenDB server a test run uses, exactly once per process.
/// </summary>
/// <remarks>
/// Both drivers share this. <see cref="SparkTestDriver"/> takes a database per test case;
/// <see cref="SparkSharedDatabase"/> takes one per test class. They must not disagree about how the
/// server is licensed, and neither may configure it twice — <c>ConfigureServer</c> throws once the
/// server has started.
/// <para>
/// <b>Thread-safety.</b> The work lives in a type initialiser, which the CLR guarantees runs exactly
/// once with correct publication however many threads race to touch the type. That matters here
/// because xUnit runs test classes in parallel, so several fixtures can reach this simultaneously on
/// a cold process. The obvious hand-rolled alternative — a <c>static bool isConfigured</c> checked
/// and then set — is a check-then-act race: two threads both observe <see langword="false"/>, both
/// configure, and the second throws. (That is precisely the shape of the equivalent code in
/// CronosCore's driver, which is safe only because NUnit is sequential by default. Do not copy it.)
/// </para>
/// </remarks>
internal static class SparkEmbeddedServer
{
    static SparkEmbeddedServer()
    {
        // Loud on an invalid licence, tolerant of an absent one — the two halves are separable
        // because they are triggered by different conditions.
        //
        // With a licence present the server validates it and refuses to start on a bad one, which is
        // what we want: ThrowOnInvalidOrMissingLicense is not consulted at all in that case. Setting
        // the flag unconditionally instead would turn an *invalid* licence from a startup error into
        // a silent downgrade to restricted mode, surfacing much later as an obscure "feature not
        // available in this licence" inside whichever test first touches ETL, encryption or
        // compression.
        //
        // With no licence at all there is nothing to validate, so refusing to start buys no
        // diagnostic — it just makes every RavenDB test fail for a contributor who cannot have one.
        // Whether that is tolerable is the fixture's call, not the server's: RequireLicense decides,
        // at the fixture's own initialisation. This has to be split that way because ConfigureServer
        // is static and runs once per process before any instance exists, so an instance member
        // cannot reach it.
        var license = LicenseHelper.LoadOrNull();
        RavenTestDriver.ConfigureServer(new TestServerOptions
        {
            Licensing = license is not null
                ? new ServerOptions.LicensingOptions
                {
                    License = license,
                    EulaAccepted = true,
                }
                : new ServerOptions.LicensingOptions
                {
                    ThrowOnInvalidOrMissingLicense = false,
                },
        });
    }

    /// <summary>
    /// Ensures the server has been configured. Idempotent, and safe to call concurrently.
    /// </summary>
    /// <remarks>
    /// The body is deliberately empty: touching the type is what runs the initialiser. A method
    /// rather than a field read so the call site says what it means.
    /// </remarks>
    internal static void EnsureConfigured()
    {
    }

    /// <summary>
    /// Prints the embedded server's URL once per process, so a developer can open Studio against the
    /// server a test run is actually using.
    /// </summary>
    /// <remarks>
    /// The port is chosen per process and appears nowhere else: the server is not the
    /// <c>Raven.Server.exe</c> Windows service on 8080, it is a <c>dotnet.exe</c> hosting
    /// <c>RavenDBServer/Raven.Server.dll</c> from the test project's output on an ephemeral port. So
    /// <c>tasklist | findstr Raven</c> finds the wrong process, and there is otherwise no way to
    /// attach to the right one while a run is in flight.
    /// <para>
    /// Written to standard output, so it appears only under
    /// <c>--logger "console;verbosity=detailed"</c> and stays out of a normal run.
    /// </para>
    /// </remarks>
    internal static void ReportUrls(IDocumentStore store)
    {
        // Interlocked rather than a bool: test classes initialise in parallel, and a check-then-act
        // here would print several times — the same race the type initialiser above exists to avoid.
        if (Interlocked.Exchange(ref reported, 1) != 0)
            return;

        Console.WriteLine($"[SparkEmbeddedServer] {string.Join(", ", store.Urls)}");
    }

    private static int reported;
}
