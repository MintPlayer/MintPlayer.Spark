using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Contracts;
using MintPlayer.Spark.Contracts;

namespace MintPlayer.Spark.SchemaGenerator;

/// <summary>
/// The libraries' TypeScript contracts (commerce X20 item 2, <c>docs/guide-typescript-contracts.md</c>):
/// <c>contracts [--verify]</c>. Generates the <c>[SparkContract]</c> types of the Spark libraries this tool
/// references into the ng-spark entry points that claim their namespaces,
/// <c>libs/node_packages/ng-spark/&lt;entry&gt;/src/generated/contracts.ts</c>. An application's contracts come
/// from its own binary instead (<c>--spark-synchronize-contracts</c>), because only it references its types.
/// </summary>
/// <remarks>
/// A library that starts declaring contracts is added to <see cref="Libraries"/> (and referenced by this
/// project); its <c>[assembly: SparkClientModule]</c> names the entry point.
/// </remarks>
public static class SparkLibraryContracts
{
    /// <summary>The command each generated file names in its header.</summary>
    public const string RegenerateCommand = "dotnet run --project tools/SchemaGenerator -- contracts";

    /// <summary>The libraries whose contracts are written, one representative type each.</summary>
    public static IReadOnlyList<Assembly> Libraries { get; } =
    [
        typeof(PersistentObject).Assembly,       // MintPlayer.Spark.Abstractions
        typeof(TranslatedString).Assembly,       // MintPlayer.Spark.Model
        typeof(SparkContractGenerator).Assembly, // MintPlayer.Spark
        typeof(SparkAuthUser).Assembly,          // MintPlayer.Spark.Authorization
        typeof(MintPlayer.Spark.ReferenceData.Contracts.SuggestResponse).Assembly, // MintPlayer.Spark.ReferenceData.Abstractions
        typeof(MintPlayer.Spark.Commerce.Contracts.CommerceListingInfo).Assembly, // MintPlayer.Spark.Commerce
        typeof(MintPlayer.Spark.Commerce.Payments.Contracts.SparkPaymentMethod).Assembly, // MintPlayer.Spark.Commerce.Payments
        typeof(MintPlayer.Spark.Commerce.Checkout.Contracts.SparkBasket).Assembly, // MintPlayer.Spark.Commerce.Checkout
        typeof(MintPlayer.Spark.Commerce.Promotions.Contracts.SparkSalePriceDisplay).Assembly, // MintPlayer.Spark.Commerce.Promotions
        typeof(MintPlayer.Spark.Commerce.Shipping.Contracts.SparkShippingRate).Assembly, // MintPlayer.Spark.Commerce.Shipping
        typeof(MintPlayer.Spark.Commerce.Ticketing.Venues.Contracts.SeatMapPayload).Assembly, // MintPlayer.Spark.Commerce.Ticketing.Venues
        typeof(MintPlayer.Spark.Commerce.Ticketing.Events.Contracts.SparkPublicEvent).Assembly, // MintPlayer.Spark.Commerce.Ticketing.Events
        typeof(MintPlayer.Spark.Challenges.Contracts.SparkChallengeOffer).Assembly, // MintPlayer.Spark.Challenges
        typeof(MintPlayer.Spark.WaitingRoom.Contracts.WaitingRoomInfo).Assembly, // MintPlayer.Spark.WaitingRoom
        typeof(MintPlayer.Spark.IdentityProvider.Endpoints.Portal.ConnectedApplication).Assembly, // MintPlayer.Spark.IdentityProvider
        typeof(MintPlayer.Spark.Commerce.Invoicing.Contracts.SparkInvoiceSummary).Assembly, // MintPlayer.Spark.Commerce.Invoicing
        typeof(MintPlayer.Spark.Commerce.Shop.Contracts.SparkShopProduct).Assembly, // MintPlayer.Spark.Commerce.Shop
        typeof(MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts.SparkTicketView).Assembly, // MintPlayer.Spark.Commerce.Ticketing.Tickets
    ];

    /// <summary>Every library module, generated in memory.</summary>
    public static IReadOnlyList<SparkContractModule> Generate()
        => SparkContractGenerator.Generate(Libraries, [], RegenerateCommand);

    /// <summary>
    /// Writes the modules under <paramref name="repositoryRoot"/>, or with <paramref name="verifyOnly"/> only checks
    /// them; returns the exit code: 0, 2 when a contract cannot be stated, 3 when a committed file is out of date.
    /// </summary>
    public static int Run(string repositoryRoot, bool verifyOnly, TextWriter output, TextWriter error)
        => Run(Generate, repositoryRoot, verifyOnly, output, error);

    /// <summary>As <see cref="Run(string, bool, TextWriter, TextWriter)"/>, over the modules <paramref name="generate"/> returns (the tests' seam).</summary>
    public static int Run(Func<IReadOnlyList<SparkContractModule>> generate, string repositoryRoot, bool verifyOnly, TextWriter output, TextWriter error)
    {
        IReadOnlyList<SparkContractModule> modules;
        try
        {
            modules = generate();
        }
        catch (SparkContractException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }

        var drifted = SparkContractGenerator.Synchronize(modules, m => SparkContractGenerator.LibraryModulePath(repositoryRoot, m), verifyOnly);
        if (drifted.Count == 0)
        {
            output.WriteLine($"The TypeScript contracts are up to date ({modules.Count} modules).");
            return 0;
        }

        foreach (var path in drifted)
        {
            var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            if (verifyOnly)
                error.WriteLine($"::error file={relative}::{relative} is out of date. Regenerate it with: {RegenerateCommand}");
            else
                output.WriteLine($"Written: {relative}");
        }
        return verifyOnly ? 3 : 0;
    }

    /// <summary>The repository root: the nearest folder above <paramref name="start"/> holding <c>nx.json</c>.</summary>
    public static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "nx.json")))
                return directory.FullName;
        }
        throw new InvalidOperationException($"No repository root (a folder with nx.json) above {start}.");
    }
}
