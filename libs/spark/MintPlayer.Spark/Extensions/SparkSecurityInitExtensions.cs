using Microsoft.AspNetCore.Builder;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Writes a starting <c>App_Data/security.json</c> for an application that has none.
/// </summary>
/// <remarks>
/// The flag is <c>--spark-init-security</c>, <b>not</b> <c>--spark-synchronize-security</c>. That
/// one is taken by the posture baseline, and it resolves the reporter — which loads the
/// configuration, which is the very file this command exists to create. Naming both the same thing
/// would make the generator throw on the file it was asked to write.
/// <para>
/// This is the only authoring support that will exist, so the generated file carries the grammar
/// in comments rather than pointing at documentation. JSON has no comments, hence the
/// <c>_comment</c> keys — the loader ignores unknown properties, and a reader who deletes them
/// loses nothing.
/// </para>
/// </remarks>
public static class SparkSecurityInitExtensions
{
    internal const string InitFlag = "--spark-init-security";

    /// <summary>
    /// Handles <c>--spark-init-security</c> and reports whether the host should stop instead of
    /// starting.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the command was handled and the host should return from
    /// <c>Main</c>; <see langword="false"/> when the flag was not passed.
    /// </returns>
    /// <example>
    /// <code>
    /// if (builder.InitializeSparkSecurityIfRequested(args))
    ///     return;
    /// </code>
    /// </example>
    public static bool InitializeSparkSecurityIfRequested(this WebApplicationBuilder builder, string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        if (!args.Contains(InitFlag))
            return false;

        var path = Path.Combine(builder.Environment.ContentRootPath, SecurityConfigurationLoader.FilePath);

        // Never overwrite. A file that already exists is the application's authorization model, and
        // regenerating a starter over it would be the single most destructive thing this command
        // could do — silently, and with no way back short of source control.
        if (File.Exists(path))
        {
            Console.Error.WriteLine(
                $"Spark: {SecurityConfigurationLoader.FilePath} already exists and was left untouched. "
                + "Delete it first if you really mean to start over.");
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Starter);

        Console.WriteLine($"Spark: wrote a starting {SecurityConfigurationLoader.FilePath}.");
        Console.WriteLine("It grants nothing. Read the comments in it, then add the rights this application needs.");
        return true;
    }

    /// <summary>
    /// Grants nothing on purpose. A starter that granted something would be copied into production
    /// by somebody who never read it; one that grants nothing fails visibly on the first request
    /// and is fixed by the person who understands the application.
    /// </summary>
    private static readonly string Starter = string.Join('\n',
    [
        "{",
        "  \"_comment\": [",
        "    \"Spark's authorization model. Every application has one; a missing or malformed file\",",
        "    \"refuses startup rather than degrading into a permissive default.\",",
        "    \"\",",
        "    \"A RIGHT is '{action}/{target}', for example 'QueryRead/Person'.\",",
        "      \"actions:  Query (list rows), Read (open one), Edit, New, Delete,\",",
        "      \"          plus any custom action name from customActions.json.\",",
        "      \"combined: QueryRead, ReadEdit, EditNew, NewDelete, EditNewDelete, ReadEditNew,\",",
        "      \"          ReadEditNewDelete, QueryReadEdit, QueryReadEditNew, QueryReadEditNewDelete.\",",
        "      \"          These expand, on denials exactly as on grants.\",",
        "      \"wildcard: '*' on either half. 'Read/*', '*/Person', '*/*'. Use sparingly: a\",",
        "      \"          wildcard covers types and actions that do not exist yet.\",",
        "    \"\",",
        "    \"Query WITHOUT Read is the useful pair: the grid lists the rows and the first column\",",
        "    \"is not a link. That is how you publish a list whose rows have no detail page.\",",
        "    \"\",",
        "    \"PRECEDENCE, in order: important denial, important grant, denial, grant, then refuse.\",",
        "    \"A denial is absolute unless an important right overrides it -- it cannot be granted\",",
        "    \"around by adding the caller to another group, so a denial on 'authenticated' locks\",",
        "    \"out administrators too.\",",
        "    \"\",",
        "    \"GROUPS are keyed by id. 'wellKnown' says which group plays each role:\",",
        "      \"anonymous     -- a caller who has NOT signed in. Not 'everyone'.\",",
        "      \"authenticated -- every caller who has, whatever claims they carry.\",",
        "    \"A right both an anonymous visitor and a signed-in user should have is TWO grants.\",",
        "    \"Neither role can be claimed: they are decided from authentication state, so no\",",
        "    \"identity provider can hand a caller 'authenticated' by naming a group.\",",
        "    \"Every other group is matched by NAME against the caller's group claims, in any\",",
        "    \"translation -- so display names are load-bearing.\",",
        "    \"\",",
        "    \"A right looks like this:\",",
        "    \"  { \\\"id\\\": \\\"<new guid>\\\", \\\"resource\\\": \\\"QueryRead/Person\\\",\",",
        "    \"    \\\"groupId\\\": \\\"00000000-0000-0000-0000-000000000001\\\",\",",
        "    \"    \\\"isDenied\\\": false, \\\"isImportant\\\": false }\"",
        "  ],",
        "",
        "  \"wellKnown\": {",
        "    \"anonymous\": \"00000000-0000-0000-0000-000000000000\",",
        "    \"authenticated\": \"00000000-0000-0000-0000-000000000001\"",
        "  },",
        "",
        "  \"groups\": {",
        "    \"00000000-0000-0000-0000-000000000000\": { \"en\": \"Anonymous visitors\" },",
        "    \"00000000-0000-0000-0000-000000000001\": { \"en\": \"Signed-in users\" }",
        "  },",
        "",
        "  \"rights\": []",
        "}",
        "",
    ]);
}
