using System.Security.Claims;
using System.Text;
using CodeCoverage.ApiTokens;
using CodeCoverage.Controllers;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// Issue #417 §4: bound the work before doing any of it.
///
/// <para><c>MaxReportBytes</c> caps the <b>compressed</b> multipart body, which does
/// not bound what happens afterwards — a gzipped cobertura is a few KB, so hundreds of
/// them fit comfortably inside 50 MB and each costs an attachment store and a parse.
/// These rejections happen before the repository is even resolved, so a bad upload
/// costs one model-binding pass and nothing else.</para>
///
/// <para>The deliberately-null session is the assertion: reaching RavenDB would mean
/// the bound was checked too late.</para>
/// </summary>
public class UploadsControllerInputBoundsTests
{
    private sealed class NullMessageBus : IMessageBus
    {
        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static UploadsController CreateController()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAsyncDocumentSession>(_ => null!);
        services.AddSingleton<IMessageBus>(new NullMessageBus());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IGitHubDiffService>(new Services.ScriptedDiffService());
        services.AddScoped<IBaseResolver, BaseResolver>();
        services.AddScoped<IRepositoryResolver>(sp =>
            new TestRepositoryResolver(sp.GetService<IAsyncDocumentSession>()));
        services.AddScoped<UploadsController>();

        var controller = services.BuildServiceProvider().GetRequiredService<UploadsController>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ApiTokenAuthenticationHandler.ScopeClaim, "Account")],
                    ApiTokenAuthenticationHandler.SchemeName)),
            },
        };
        return controller;
    }

    private static IFormFile Report(string name) =>
        new FormFile(new MemoryStream(Encoding.UTF8.GetBytes("TN:\nSF:a.ts\nDA:1,1\nend_of_record\n")), 0, 34, name, name);

    private static UploadsController.UploadForm FormWith(int reportCount, string? fileList = null) => new()
    {
        Repository = "owner/name",
        CommitSha = "abcdef1234567890",
        Files = new FormFileCollection { Capacity = reportCount },
        FileList = fileList,
    };

    [Fact]
    public async Task Too_many_reports_in_one_upload_is_rejected_before_the_database_is_touched()
    {
        var form = FormWith(0);
        // 513 — one past the documented limit of 512.
        for (var i = 0; i < 513; i++)
            ((FormFileCollection)form.Files).Add(Report($"report-{i}.info"));

        var result = await CreateController().Upload(form, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Too many report files", bad.Value!.ToString());
    }

    /// <summary>
    /// The bound must not fire at exactly the limit — an off-by-one here would reject
    /// a legitimate monorepo upload.
    ///
    /// <para>Asserting on what the request does <i>not</i> say, rather than on how far
    /// it gets: past this check the controller resolves a repository that does not
    /// exist in this harness, so the outcome is a NotFound (or a throw, depending on
    /// the resolver). Either is fine; what matters is that it is not the
    /// too-many-reports rejection.</para>
    /// </summary>
    [Fact]
    public async Task A_report_count_at_the_limit_is_not_rejected_by_the_bound()
    {
        var form = FormWith(0);
        for (var i = 0; i < 512; i++)
            ((FormFileCollection)form.Files).Add(Report($"report-{i}.info"));

        ActionResult<UploadsController.UploadResponse>? result = null;
        try
        {
            result = await CreateController().Upload(form, CancellationToken.None);
        }
        catch
        {
            // Got past the bound and failed further in — which is the point.
            return;
        }

        if (result.Result is BadRequestObjectResult bad)
            Assert.DoesNotContain("Too many report files", bad.Value!.ToString());
    }

    [Fact]
    public async Task An_oversized_file_list_is_rejected()
    {
        var form = FormWith(0, fileList: new string('a', (8 * 1024 * 1024) + 1));
        ((FormFileCollection)form.Files).Add(Report("report.info"));

        var result = await CreateController().Upload(form, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("file list is too large", bad.Value!.ToString());
    }
}
