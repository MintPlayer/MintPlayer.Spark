using MintPlayer.Spark;
using Raven.Client.Documents.Linq;
using WebhooksDemo.Entities;

namespace WebhooksDemo;

public class WebhooksDemoSparkContext : SparkContext
{
    public IRavenQueryable<GitHubProject> GitHubProjects => Session.Query<GitHubProject>();
}
