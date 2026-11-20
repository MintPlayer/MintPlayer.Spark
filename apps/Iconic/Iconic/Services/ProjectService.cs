using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Iconic.Entities;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace Iconic.Services;

// The kind travels by name ("Icon"), as GetAsync answers it; System.Text.Json reads enums only as numbers otherwise.
public sealed record CreateProjectRequest(string? Name, [property: JsonConverter(typeof(JsonStringEnumConverter<ProjectKind>))] ProjectKind? Kind, int? Width, int? Height, JsonElement? Scene);
public sealed record UpdateProjectRequest(string? Name, [property: JsonConverter(typeof(JsonStringEnumConverter<ProjectKind>))] ProjectKind? Kind, int? Width, int? Height);
public sealed record SaveSceneRequest(long Version, JsonElement Scene);
public sealed record SceneOp(string? Op, List<string>? Path, JsonElement? Value);
public sealed record PatchSceneRequest(long Version, List<SceneOp>? Ops);
public sealed record SnapshotRequest(string? Name);
public sealed record ShareRequest(bool Enabled, bool NewLink);
public sealed record ReportRequest(string? Reason);
public sealed record TakedownRequest(string? Token);

/// <summary>
/// The editor's server side (docs/iconic_PRD.md D10, D20–D22): projects, their scene, attachments, snapshots and
/// share links. Every value reaches RavenDB as a parameter or a patch argument; nothing is spliced into a query or a
/// script (CLAUDE.md, "RavenDB queries").
/// </summary>
public sealed partial class ProjectService
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IconicAccess access;
    [Inject] private readonly IOptions<IconicQuotas> quotas;
    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly IConfiguration configuration;

    private IconicQuotas Q => quotas.Value;

    /// <summary>The attachment names a client may write: what the editor stores, nothing else.</summary>
    [GeneratedRegex(@"^(paint/[A-Za-z0-9_-]{1,64}/[0-9]{1,4}\.png|thumbnail\.png|fonts/[A-Za-z0-9_-]{1,64}\.(ttf|otf))$")]
    private static partial Regex WritableFile();

    /// <summary>The files a share link exposes: paint layers, fonts and the thumbnail, never the snapshots.</summary>
    [GeneratedRegex(@"^(paint/|fonts/|thumbnail\.png$)")]
    private static partial Regex SharedFile();

    /// <summary>A project key: the part after <c>Projects/</c>, which the editor's URLs carry.</summary>
    [GeneratedRegex(@"^[a-f0-9]{32}$")]
    private static partial Regex ProjectKey();

    private static readonly HashSet<string> ForbiddenSegments = ["__proto__", "constructor", "prototype"];

    private static string ProjectId(string key) => $"Projects/{key}";
    private static string SceneId(string key) => $"ProjectScenes/{key}";
    private static string KeyOf(string projectId) => projectId["Projects/".Length..];

    // ---------------------------------------------------------------- projects

    public async Task<IResult> CreateAsync(CreateProjectRequest request, CancellationToken ct)
    {
        if (access.UserId is not { } userId) return TypedResults.Unauthorized();
        var count = await session.Query<Project>().Where(p => p.OwnerId == userId).CountAsync(ct);
        if (count >= Q.MaxProjects)
            return Problem(409, "quota.projects", $"You have {count} projects, the most allowed is {Q.MaxProjects}. Delete one first.");

        var json = request.Scene is { ValueKind: JsonValueKind.Object } s ? s.GetRawText() : "{}";
        if (json.Length > Q.MaxSceneBytes) return SceneTooBig(json.Length);

        var key = Guid.NewGuid().ToString("N");
        var project = NewProject(key, userId, request.Name, request.Kind, request.Width, request.Height, json.Length);
        await session.StoreAsync(project, project.Id, ct);
        await session.StoreAsync(new ProjectScene { Id = SceneId(key), ProjectId = project.Id!, OwnerId = userId, Version = 1, Json = json, Bytes = json.Length }, SceneId(key), ct);
        await session.SaveChangesAsync(ct);
        return TypedResults.Ok(new { key, id = project.Id, version = 1L });
    }

    /// <summary>The caller's projects for the editor's home page, newest change first, with the quota they count against.</summary>
    public async Task<IResult> ListAsync(CancellationToken ct)
    {
        if (access.UserId is not { } userId) return TypedResults.Unauthorized();
        var projects = await session.Query<Project>()
            .Where(p => p.OwnerId == userId)
            .OrderByDescending(p => p.ModifiedAt)
            .Take(Q.MaxProjects)
            .ToListAsync(ct);
        return TypedResults.Ok(new
        {
            projects = projects.Select(p => new
            {
                key = KeyOf(p.Id!),
                p.Name,
                kind = p.Kind.ToString(),
                p.Width,
                p.Height,
                p.ModifiedAt,
                p.IsPublic,
                hasThumbnail = FileTuples(p).Any(f => f.Name == "thumbnail.png"),
            }),
            maxProjects = Q.MaxProjects,
        });
    }

    private static Project NewProject(string key, string userId, string? name, ProjectKind? kind, int? width, int? height, long sceneBytes) => new()
    {
        Id = ProjectId(key),
        Name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim()[..Math.Min(name.Trim().Length, 200)],
        Kind = kind ?? ProjectKind.Icon,
        Width = Math.Clamp(width ?? 256, 1, 4096),
        Height = Math.Clamp(height ?? 256, 1, 4096),
        OwnerId = userId,
        SceneBytes = sceneBytes,
        CreatedBy = userId,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedBy = userId,
        ModifiedAt = DateTimeOffset.UtcNow,
    };

    public async Task<IResult> GetAsync(string key, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        var scene = await session.LoadAsync<ProjectScene>(SceneId(key), ct);
        return TypedResults.Ok(new
        {
            key,
            id = project.Id,
            project.Name,
            kind = project.Kind.ToString(),
            project.Width,
            project.Height,
            project.IsPublic,
            shareToken = project.IsPublic ? project.ShareToken : null,
            project.ShareTakenDown,
            version = scene?.Version ?? 0,
            scene = Parse(scene?.Json ?? "{}"),
            snapshots = project.Snapshots.Select(s => new { s.Id, s.Name, s.CreatedAt, s.Bytes }),
            files = Files(project),
            usage = Usage(project),
        });
    }

    public async Task<IResult> UpdateAsync(string key, UpdateProjectRequest request, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        if (!string.IsNullOrWhiteSpace(request.Name)) project.Name = request.Name.Trim()[..Math.Min(request.Name.Trim().Length, 200)];
        if (request.Kind is { } kind) project.Kind = kind;
        if (request.Width is { } w) project.Width = Math.Clamp(w, 1, 4096);
        if (request.Height is { } h) project.Height = Math.Clamp(h, 1, 4096);
        Touch(project);
        await session.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>The editor's delete: the project (with its attachments: bitmaps, fonts, snapshots) and its scene, in one save.</summary>
    public async Task<IResult> DeleteAsync(string key, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        session.Delete(project);
        session.Delete(SceneId(key));
        await session.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Deletes the scene beside a project; the Spark delete removes the project and its attachments.</summary>
    public async Task DeleteSceneAsync(string projectId, CancellationToken ct)
    {
        if (!projectId.StartsWith("Projects/", StringComparison.Ordinal)) return;
        session.Delete(SceneId(KeyOf(projectId)));
        await session.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- scene

    /// <summary>Replaces the whole scene. The editor uses it for the first save and after a version conflict.</summary>
    public async Task<IResult> SaveSceneAsync(string key, SaveSceneRequest request, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        if (request.Scene.ValueKind != JsonValueKind.Object) return Problem(400, "scene.invalid", "The scene must be a JSON object.");
        var json = request.Scene.GetRawText();
        if (json.Length > Q.MaxSceneBytes) return SceneTooBig(json.Length);

        var scene = await session.LoadAsync<ProjectScene>(SceneId(key), ct);
        if (scene is null)
        {
            scene = new ProjectScene { Id = SceneId(key), ProjectId = project.Id!, OwnerId = project.OwnerId, Version = 0 };
            await session.StoreAsync(scene, scene.Id, ct);
        }
        if (scene.Version != request.Version) return VersionConflict(scene.Version);
        scene.Json = json;
        scene.Bytes = json.Length;
        scene.Version++;
        project.SceneBytes = json.Length;
        Touch(project);
        await session.SaveChangesAsync(ct);
        return TypedResults.Ok(new { version = scene.Version, usage = Usage(project) });
    }

    /// <summary>
    /// Applies the editor's changes since the last save (D21, S5): set and remove operations on the scene's object
    /// tree. The operations travel as one JSON string argument of a fixed patch script, which parses the scene, applies
    /// them and checks the version and the quota, all inside the database in one step.
    /// </summary>
    public async Task<IResult> PatchSceneAsync(string key, PatchSceneRequest request, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        var ops = request.Ops ?? [];
        if (ops.Count == 0) return Problem(400, "patch.empty", "No changes to apply.");
        if (ops.Count > 10_000) return Problem(400, "patch.large", "Too many changes in one save; save the whole scene instead.");
        foreach (var op in ops)
        {
            if (op.Op is not ("set" or "remove")) return Problem(400, "patch.op", "Each change is 'set' or 'remove'.");
            if (op.Path is not { Count: > 0 and <= 32 } path || path.Any(s => string.IsNullOrEmpty(s) || s.Length > 128 || ForbiddenSegments.Contains(s)))
                return Problem(400, "patch.path", "A change has an invalid path.");
            if (op.Op == "set" && op.Value is null) return Problem(400, "patch.value", "A 'set' change needs a value.");
        }
        var opsJson = JsonSerializer.Serialize(ops.Select(o => new { op = o.Op, path = o.Path, value = o.Value }));

        try
        {
            var status = await store.Operations.SendAsync(new PatchOperation(SceneId(key), null, new PatchRequest
            {
                Script = PatchScript,
                Values = { ["version"] = request.Version, ["ops"] = opsJson, ["max"] = Q.MaxSceneBytes },
            }), token: ct);
            if (status == PatchStatus.DocumentDoesNotExist) return VersionConflict(0);
        }
        catch (RavenException ex) when (ex.Message.Contains("ICONIC_VERSION", StringComparison.Ordinal))
        {
            var current = await session.LoadAsync<ProjectScene>(SceneId(key), ct);
            return VersionConflict(current?.Version ?? 0);
        }
        catch (RavenException ex) when (ex.Message.Contains("ICONIC_QUOTA", StringComparison.Ordinal))
        {
            return Problem(413, "quota.scene", $"The drawing would be larger than {Q.MaxSceneBytes / (1024 * 1024)} MB. Remove something or move bitmaps to paint layers.");
        }

        // The patch ran outside this session: read the result in a fresh one so the numbers are current.
        using var fresh = store.OpenAsyncSession();
        var patched = await fresh.LoadAsync<ProjectScene>(SceneId(key), ct);
        project.SceneBytes = patched?.Bytes ?? project.SceneBytes;
        Touch(project);
        await session.SaveChangesAsync(ct);
        return TypedResults.Ok(new { version = patched?.Version ?? request.Version + 1, usage = Usage(project) });
    }

    /// <summary>
    /// The fixed patch script: every input comes from <c>args</c>. Object keys are created on the way down, so a set
    /// on a new shape's style works without the shape having been set first. Thrown markers map to 409 and 413.
    /// </summary>
    internal const string PatchScript = """
        if (this.Version !== args.version) throw new Error('ICONIC_VERSION');
        var scene = JSON.parse(this.Json);
        var ops = JSON.parse(args.ops);
        for (var i = 0; i < ops.length; i++) {
            var op = ops[i];
            var path = op.path;
            var target = scene;
            for (var j = 0; j < path.length - 1; j++) {
                var key = path[j];
                if (target[key] === null || typeof target[key] !== 'object') target[key] = {};
                target = target[key];
            }
            var last = path[path.length - 1];
            if (op.op === 'set') target[last] = op.value;
            else if (Array.isArray(target)) target.splice(Number(last), 1);
            else delete target[last];
        }
        var json = JSON.stringify(scene);
        if (json.length > args.max) throw new Error('ICONIC_QUOTA');
        this.Json = json;
        this.Bytes = json.length;
        this.Version = this.Version + 1;
        """;

    // ---------------------------------------------------------------- attachments

    public async Task<IResult> PutFileAsync(string key, string name, HttpRequest http, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        if (!WritableFile().IsMatch(name)) return Problem(400, "file.name", "That file name is not one the editor stores.");

        var others = session.Advanced.Attachments.GetNames(project).Where(a => a.Name != name).Sum(a => a.Size);
        var room = Q.MaxAttachmentBytes - others;
        var buffer = new MemoryStream();
        var read = await CopyLimitedAsync(http.Body, buffer, room, ct);
        if (read < 0) return AttachmentsTooBig();
        if (read == 0) return Problem(400, "file.empty", "The file is empty.");
        buffer.Position = 0;
        if (!LooksLike(name, buffer)) return Problem(400, "file.type", "The file's content does not match its name.");

        var contentType = name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : name.EndsWith(".otf", StringComparison.Ordinal) ? "font/otf" : "font/ttf";
        session.Advanced.Attachments.Store(project, name, buffer, contentType);
        project.AttachmentBytes = others + read;
        Touch(project);
        await session.SaveChangesAsync(ct);
        return TypedResults.Ok(new { usage = Usage(project) });
    }

    public async Task<IResult> GetFileAsync(string key, string name, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        return await StreamAttachmentAsync(project, name, ct);
    }

    public async Task<IResult> DeleteFileAsync(string key, string name, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        if (!WritableFile().IsMatch(name)) return TypedResults.NotFound();
        if (!await session.Advanced.Attachments.ExistsAsync(project.Id!, name, ct)) return TypedResults.NoContent();
        session.Advanced.Attachments.Delete(project, name);
        project.AttachmentBytes = session.Advanced.Attachments.GetNames(project).Where(a => a.Name != name).Sum(a => a.Size);
        Touch(project);
        await session.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private async Task<IResult> StreamAttachmentAsync(Project project, string name, CancellationToken ct)
    {
        var attachment = await session.Advanced.Attachments.GetAsync(project, name, ct);
        if (attachment is null) return TypedResults.NotFound();
        var bytes = new MemoryStream();
        await attachment.Stream.CopyToAsync(bytes, ct);
        attachment.Dispose();
        return TypedResults.File(bytes.ToArray(), attachment.Details.ContentType ?? "application/octet-stream");
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes; returns the count, or -1 when the body is larger.</summary>
    private static async Task<long> CopyLimitedAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            if (total > limit) return -1;
            await target.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        return total;
    }

    /// <summary>A PNG starts with its signature; a TrueType font with 0x00010000 or 'true'; an OpenType one with 'OTTO'.</summary>
    private static bool LooksLike(string name, MemoryStream data)
    {
        var head = data.GetBuffer().AsSpan(0, (int)Math.Min(8, data.Length));
        if (name.EndsWith(".png", StringComparison.Ordinal))
            return head.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        if (head.Length < 4) return false;
        var tag = head[..4];
        return tag.SequenceEqual(new byte[] { 0, 1, 0, 0 }) || tag.SequenceEqual("true"u8) || tag.SequenceEqual("OTTO"u8);
    }

    // ---------------------------------------------------------------- snapshots (D22)

    public async Task<IResult> CreateSnapshotAsync(string key, SnapshotRequest request, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        var scene = await session.LoadAsync<ProjectScene>(SceneId(key), ct);
        var result = AddSnapshot(project, scene?.Json ?? "{}", request.Name);
        if (result is not null) return result;
        Touch(project);
        await session.SaveChangesAsync(ct);
        return TypedResults.Ok(new { snapshots = project.Snapshots.Select(s => new { s.Id, s.Name, s.CreatedAt, s.Bytes }), usage = Usage(project) });
    }

    /// <summary>Stores a snapshot attachment and its row; returns an error result when a limit refuses it.</summary>
    private IResult? AddSnapshot(Project project, string json, string? name)
    {
        if (project.Snapshots.Count >= Q.MaxSnapshots)
            return Problem(409, "quota.snapshots", $"A project keeps at most {Q.MaxSnapshots} versions. Delete one first.");
        var bytes = Encoding.UTF8.GetBytes(json);
        var used = session.Advanced.Attachments.GetNames(project).Sum(a => a.Size);
        if (used + bytes.Length > Q.MaxAttachmentBytes) return AttachmentsTooBig();

        var id = Guid.NewGuid().ToString("N");
        session.Advanced.Attachments.Store(project, $"snapshots/{id}.json", new MemoryStream(bytes), "application/json");
        project.Snapshots.Add(new ProjectSnapshot
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? $"Version {project.Snapshots.Count + 1}" : name.Trim()[..Math.Min(name.Trim().Length, 100)],
            CreatedAt = DateTime.UtcNow,
            Bytes = bytes.Length,
        });
        project.AttachmentBytes = used + bytes.Length;
        return null;
    }

    /// <summary>Restores a snapshot. The current scene becomes a snapshot first, so a restore never loses work.</summary>
    public async Task<IResult> RestoreSnapshotAsync(string key, string snapshotId, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        var snapshot = project.Snapshots.FirstOrDefault(s => s.Id == snapshotId);
        if (snapshot is null) return TypedResults.NotFound();

        var attachment = await session.Advanced.Attachments.GetAsync(project, $"snapshots/{snapshot.Id}.json", ct);
        if (attachment is null) return TypedResults.NotFound();
        string restored;
        using (attachment)
        using (var reader = new StreamReader(attachment.Stream, Encoding.UTF8))
            restored = await reader.ReadToEndAsync(ct);

        var scene = await session.LoadAsync<ProjectScene>(SceneId(key), ct);
        if (scene is null) return TypedResults.NotFound();
        var before = AddSnapshot(project, scene.Json, $"Before restoring “{snapshot.Name}”");
        if (before is not null) return before;

        scene.Json = restored;
        scene.Bytes = restored.Length;
        scene.Version++;
        project.SceneBytes = restored.Length;
        Touch(project);
        await session.SaveChangesAsync(ct);
        return TypedResults.Ok(new { version = scene.Version, scene = Parse(restored), snapshots = project.Snapshots.Select(s => new { s.Id, s.Name, s.CreatedAt, s.Bytes }), usage = Usage(project) });
    }

    public async Task<IResult> DeleteSnapshotAsync(string key, string snapshotId, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        var snapshot = project.Snapshots.FirstOrDefault(s => s.Id == snapshotId);
        if (snapshot is null) return TypedResults.NoContent();
        var name = $"snapshots/{snapshot.Id}.json";
        session.Advanced.Attachments.Delete(project, name);
        project.Snapshots.Remove(snapshot);
        project.AttachmentBytes = session.Advanced.Attachments.GetNames(project).Where(a => a.Name != name).Sum(a => a.Size);
        Touch(project);
        await session.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- sharing (D20)

    public async Task<IResult> ShareAsync(string key, ShareRequest request, CancellationToken ct)
    {
        var (project, error) = await LoadOwnedAsync(key, ct);
        if (project is null) return error!;
        if (request.Enabled && project.ShareTakenDown)
            return Problem(409, "share.takenDown", "This project was taken down after a report and can no longer be shared.");
        if (request.Enabled && (project.ShareToken is null || request.NewLink))
            project.ShareToken = NewToken();
        project.IsPublic = request.Enabled;
        Touch(project);
        await session.SaveChangesAsync(ct);
        return TypedResults.Ok(new { isPublic = project.IsPublic, shareToken = project.IsPublic ? project.ShareToken : null });
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    private async Task<Project?> LoadSharedAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64) return null;
        // A parameterised query: the token is a value, never part of the query text.
        return await session.Query<Project>()
            .Where(p => p.ShareToken == token && p.IsPublic && !p.ShareTakenDown)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IResult> GetSharedAsync(string token, CancellationToken ct)
    {
        var project = await LoadSharedAsync(token, ct);
        if (project is null) return TypedResults.NotFound();
        var scene = await session.LoadAsync<ProjectScene>(SceneId(KeyOf(project.Id!)), ct);
        return TypedResults.Ok(new
        {
            project.Name,
            kind = project.Kind.ToString(),
            project.Width,
            project.Height,
            scene = Parse(scene?.Json ?? "{}"),
            files = Files(project).Where(f => SharedFile().IsMatch(f.Name)),
        });
    }

    public async Task<IResult> GetSharedFileAsync(string token, string name, CancellationToken ct)
    {
        var project = await LoadSharedAsync(token, ct);
        if (project is null || !SharedFile().IsMatch(name)) return TypedResults.NotFound();
        return await StreamAttachmentAsync(project, name, ct);
    }

    /// <summary>Copies a shared project into the caller's own projects: scene, paint layers and fonts.</summary>
    public async Task<IResult> RemixAsync(string token, CancellationToken ct)
    {
        if (access.UserId is not { } userId) return TypedResults.Unauthorized();
        var source = await LoadSharedAsync(token, ct);
        if (source is null) return TypedResults.NotFound();
        var count = await session.Query<Project>().Where(p => p.OwnerId == userId).CountAsync(ct);
        if (count >= Q.MaxProjects)
            return Problem(409, "quota.projects", $"You have {count} projects, the most allowed is {Q.MaxProjects}. Delete one first.");

        var sourceScene = await session.LoadAsync<ProjectScene>(SceneId(KeyOf(source.Id!)), ct);
        var json = sourceScene?.Json ?? "{}";
        var key = Guid.NewGuid().ToString("N");
        var copy = NewProject(key, userId, $"{source.Name} (remix)", source.Kind, source.Width, source.Height, json.Length);
        await session.StoreAsync(copy, copy.Id, ct);
        await session.StoreAsync(new ProjectScene { Id = SceneId(key), ProjectId = copy.Id!, OwnerId = userId, Version = 1, Json = json, Bytes = json.Length }, SceneId(key), ct);
        await session.SaveChangesAsync(ct);

        // RavenDB copies attachments only onto a stored document, so the files follow in a second step.
        var files = session.Advanced.Attachments.GetNames(source).Where(a => SharedFile().IsMatch(a.Name)).ToList();
        foreach (var file in files) session.Advanced.Attachments.Copy(source, file.Name, copy, file.Name);
        copy.AttachmentBytes = files.Sum(f => f.Size);
        await session.SaveChangesAsync(ct);
        return TypedResults.Ok(new { key, id = copy.Id });
    }

    /// <summary>"Report this" on a shared page (D20): mails the site's moderator with the link and a takedown link.</summary>
    public async Task<IResult> ReportAsync(string token, ReportRequest request, string publicBaseUrl, CancellationToken ct)
    {
        var project = await LoadSharedAsync(token, ct);
        if (project is null) return TypedResults.NotFound();
        var to = configuration["Iconic:ReportAddress"];
        var mailer = services.GetService<ISparkMailer>();
        if (string.IsNullOrWhiteSpace(to) || mailer is null)
            return Problem(503, "report.unavailable", "Reporting is not set up on this server.");
        var reason = (request.Reason ?? string.Empty).Trim();
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = "ShareReported",
            To = to,
            Data = new
            {
                project_name = project.Name,
                reason = reason[..Math.Min(reason.Length, 2000)],
                share_url = $"{publicBaseUrl.TrimEnd('/')}/s/{token}",
                takedown_url = $"{publicBaseUrl.TrimEnd('/')}/admin/takedown?token={Uri.EscapeDataString(token)}",
            },
            // One mail per shared project per retention window, however often it is reported.
            DeduplicationKey = $"iconic-report:{project.Id}",
        }, ct);
        return TypedResults.Accepted((string?)null);
    }

    /// <summary>An administrator takes a shared project down: the link stops working and cannot be shared again.</summary>
    public async Task<IResult> TakedownAsync(TakedownRequest request, CancellationToken ct)
    {
        if (!await access.IsAdministratorAsync()) return access.UserId is null ? TypedResults.Unauthorized() : Problem(403, "takedown.forbidden", "Only an administrator can take a shared drawing down.");
        if (string.IsNullOrEmpty(request.Token)) return Problem(400, "takedown.token", "Name the share link's token.");
        var project = await session.Query<Project>().Where(p => p.ShareToken == request.Token).FirstOrDefaultAsync(ct);
        if (project is null) return TypedResults.NotFound();
        project.IsPublic = false;
        project.IsListed = false;
        project.ShareTakenDown = true;
        await session.SaveChangesAsync(ct);
        return TypedResults.Ok(new { project.Name });
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(Project? Project, IResult? Error)> LoadOwnedAsync(string key, CancellationToken ct)
    {
        if (access.UserId is not { } userId) return (null, TypedResults.Unauthorized());
        if (!ProjectKey().IsMatch(key)) return (null, TypedResults.NotFound());
        var project = await session.LoadAsync<Project>(ProjectId(key), ct);
        // Someone else's project answers 404, not 403, so ids of other users' projects don't leak.
        if (project is null || project.OwnerId != userId) return (null, TypedResults.NotFound());
        return (project, null);
    }

    private void Touch(Project project)
    {
        project.ModifiedAt = DateTimeOffset.UtcNow;
        project.ModifiedBy = access.UserId;
    }

    private IEnumerable<(string Name, long Size)> FileTuples(Project project)
        => session.Advanced.Attachments.GetNames(project).Select(a => (a.Name, a.Size));

    private IEnumerable<FileInfoDto> Files(Project project)
        => FileTuples(project).Where(f => !f.Name.StartsWith("snapshots/", StringComparison.Ordinal)).Select(f => new FileInfoDto(f.Name, f.Size));

    private object Usage(Project project) => new
    {
        sceneBytes = project.SceneBytes,
        maxSceneBytes = Q.MaxSceneBytes,
        attachmentBytes = project.AttachmentBytes,
        maxAttachmentBytes = Q.MaxAttachmentBytes,
        snapshots = project.Snapshots.Count,
        maxSnapshots = Q.MaxSnapshots,
        maxProjects = Q.MaxProjects,
    };

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private IResult SceneTooBig(long size)
        => Problem(413, "quota.scene", $"The drawing is {size / 1024} KB; the most allowed is {Q.MaxSceneBytes / (1024 * 1024)} MB.");

    private IResult AttachmentsTooBig()
        => Problem(413, "quota.files", $"Bitmaps, fonts and versions together may use at most {Q.MaxAttachmentBytes / (1024 * 1024)} MB per project.");

    private static IResult VersionConflict(long current)
        => TypedResults.Json(new { code = "scene.version", message = "The drawing changed elsewhere (another tab?). Reload to continue.", version = current }, statusCode: 409);

    private static IResult Problem(int status, string code, string message)
        => TypedResults.Json(new { code, message }, statusCode: status);
}

public sealed record FileInfoDto(string Name, long Size);
