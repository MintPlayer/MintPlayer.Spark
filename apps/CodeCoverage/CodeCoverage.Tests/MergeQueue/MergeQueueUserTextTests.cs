using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.LookupReferences;
using CodeCoverage.MergeQueue;
using CodeCoverage.Services;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// §0 D72: no text the merge queue shows people cites the PRD's own decision ids, such as "(O1)", "(T3)", "§0 D65" or
/// "O21 A". Whoever reads a check run, a pull-request comment, an eviction message, the panel or an API refusal has never
/// seen the PRD, so those ids explain nothing there; they stay in comments and doc comments. Read from the compiled code:
/// every string literal and string constant of the types that produce such text, so a new string is held to this without
/// a test of its own.
/// </summary>
public class MergeQueueUserTextTests
{
    /// <summary>An id alone ("O25"), qualified ("§0 D65") or with its case ("O20 case 1", "O21 A").</summary>
    private static readonly Regex DecisionId = new(@"§|(?<![\w./-])[ODTSMQ]\d{1,2}(?![\w-])", RegexOptions.CultureInvariant);

    /// <summary>A .NET format string such as "D2" or "X8", which reads like an id and is none.</summary>
    private static readonly Regex FormatString = new(@"^[DdXxNnFfCcEeGgPpRr]\d{0,2}$", RegexOptions.CultureInvariant);

    /// <summary>Everything that writes what a person reads about the merge queue.</summary>
    private static IEnumerable<Type> UserFacingTypes()
    {
        bool InNamespace(Type type, string ns) => type.Namespace == ns;
        bool Within(Type type, Type root) => type == root || (type.DeclaringType is { } outer && Within(outer, root));

        var app = typeof(MergeQueueEngine).Assembly.GetTypes();
        var git = typeof(MergeQueueResolver).Assembly.GetTypes();
        var github = typeof(GitHubForgeMergeQueue).Assembly.GetTypes();
        var library = typeof(MergeQueueEntry).Assembly.GetTypes();

        Type[] roots =
        [
            typeof(MergeQueueController), typeof(AccountPermissionsController),
            typeof(GitHubForgeMergeQueue), typeof(GitHubInstallationPermissions),
            typeof(MergeQueueEntry), typeof(Entities.MergeQueue), typeof(Repository), typeof(Account),
        ];
        return app.Where(t => InNamespace(t, "CodeCoverage.MergeQueue"))
            .Concat(git.Where(t => InNamespace(t, "CodeCoverage.Git.MergeQueue")))
            .Concat(library.Where(t => t.Namespace == typeof(EMergeQueueApprovalFloor).Namespace && t.Name.Contains("MergeQueue", StringComparison.Ordinal)))
            .Concat(app.Concat(github).Concat(library).Where(t => roots.Any(root => Within(t, root))))
            .Distinct();
    }

    [Fact]
    public void No_string_the_merge_queue_shows_people_cites_a_decision_id()
    {
        var types = UserFacingTypes().ToList();
        types.Should().Contain(typeof(MergeQueueEngine)).And.Contain(typeof(MergeQueueController)).And.Contain(typeof(MergeQueueResolver));

        var citing = types
            .SelectMany(type => StringsOf(type).Select(text => (Type: type, Text: text)))
            .Where(s => !FormatString.IsMatch(s.Text) && DecisionId.IsMatch(s.Text))
            .Select(s => $"{s.Type.FullName}: \"{s.Text}\"")
            .Distinct()
            .ToList();

        citing.Should().BeEmpty("user-facing text explains itself; decision ids belong in comments (§0 D72)");
    }

    /// <summary>The scan reads what the compiler emitted: a literal in an interpolated string, a lambda and an async
    /// method's state machine are all found.</summary>
    [Fact]
    public void The_scan_finds_literals_in_interpolations_lambdas_and_async_methods()
    {
        var strings = typeof(Sample).GetNestedTypes(BindingFlags.NonPublic).Append(typeof(Sample)).SelectMany(StringsOf).ToList();

        strings.Should().Contain("interpolated (O1) ").And.Contain("lambda (T3)").And.Contain("async (§0 D65)").And.Contain("constant O21 A");
        strings.Where(s => DecisionId.IsMatch(s)).Should().HaveCount(4);
    }

    private static class Sample
    {
        public const string Constant = "constant O21 A";
        public static string Interpolated(int n) => $"interpolated (O1) {n}";
        public static Func<string> Lambda() => () => "lambda (T3)";
        public static async Task<string> Async() { await Task.Yield(); return "async (§0 D65)"; }
    }

    /// <summary>The string constants of <paramref name="type"/> and every string its methods load (<c>ldstr</c>).</summary>
    private static IEnumerable<string> StringsOf(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(all))
            if (field.IsLiteral && field.GetRawConstantValue() is string constant)
                yield return constant;

        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;
            foreach (var text in LoadedStrings(method.Module, il))
                yield return text;
        }
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => op.Value);

    /// <summary>Walks the IL instruction by instruction, so an operand byte is never read as an opcode.</summary>
    private static IEnumerable<string> LoadedStrings(Module module, byte[] il)
    {
        for (var at = 0; at < il.Length;)
        {
            short value = il[at++];
            if (value == 0xFE) value = (short)(0xFE00 | il[at++]);
            var op = OpCodesByValue[value];
            if (op == OpCodes.Ldstr)
                yield return module.ResolveString(BitConverter.ToInt32(il, at));
            at += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
                _ => 4,
            };
        }
    }
}
