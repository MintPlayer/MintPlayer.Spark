using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class RecipientRegistrationProducer : Producer
{
    private readonly IEnumerable<RecipientClassInfo> recipientClasses;
    private readonly bool knowsMessaging;

    public RecipientRegistrationProducer(
        IEnumerable<RecipientClassInfo> recipientClasses,
        bool knowsMessaging,
        string rootNamespace)
        : base(rootNamespace, "SparkRecipientRegistrations.g.cs")
    {
        this.recipientClasses = recipientClasses;
        this.knowsMessaging = knowsMessaging;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var recipientList = recipientClasses.ToList();

        // Don't generate if no recipient classes found or project doesn't reference Spark.Messaging
        if (!knowsMessaging || recipientList.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("using Microsoft.Extensions.DependencyInjection;");
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkRecipientsExtensions"))
            {
                using (writer.OpenBlock("internal static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddSparkRecipients(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)"))
                {
                    foreach (var recipientClass in recipientList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteLine($"global::MintPlayer.Spark.Messaging.SparkMessagingExtensions.AddRecipient<{recipientClass.MessageTypeName}, {recipientClass.RecipientTypeName}>(services);");
                    }
                    writer.WriteLine("return services;");
                }
            }
        }
    }
}
