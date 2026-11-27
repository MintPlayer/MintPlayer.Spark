using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.Spark.LibraryGenerators.Models;
using System.CodeDom.Compiler;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// Gives every get-only member of a stored type a private backing field, so the SQL providers store it as RavenDB does
/// (#536 O13): RavenDB's serializer writes every public getter into the document, where it can be filtered and sorted.
/// <code>
/// [SparkStoredGetter("FullName", "&lt;sha-256 of the getter's tokens&gt;")]
/// partial class Person { private string __stored_FullName; }
/// </code>
/// The SQL providers map the member to the field with <c>PropertyAccessMode.PreferFieldDuringConstruction</c>: EF
/// writes the field when it materialises a row and reads the getter when it saves, so the column always holds the
/// getter's current value, and LINQ on the member reads the column. The attribute carries the formula's hash, which
/// <c>--spark-synchronize-model</c> records so it can name the getters whose formula changed.
/// <para>
/// A stored type is one with an <c>Id&lt;Self&gt;</c> (its own or a base type's) or a <c>[ValueObject]</c>. A member
/// qualifies when it is public, an instance member, has a getter with a body and no setter, and is not
/// <c>[JsonIgnore]</c>: exactly what the document holds and the setter-less half of it.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public class StoredGetterGenerator : IncrementalGenerator
{
    internal const string AttributeMetadataName = "MintPlayer.Spark.Abstractions.SparkStoredGetterAttribute";
    internal const string FieldPrefix = "__stored_";

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var gettersProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => IsGetterOnly(node),
                transform: static (ctx, ct) => Describe(ctx, ct))
            .Where(static x => x is not null)
            .Select(static (x, ct) => x!)
            .Collect()
            .Select(static (found, ct) => Distinct(found));

        var hasAttribute = context.CompilationProvider
            .Select(static (compilation, ct) => compilation.GetTypeByMetadataName(AttributeMetadataName) is not null);

        var sourceProvider = gettersProvider
            .Combine(settingsProvider)
            .Combine(hasAttribute)
            .Select(static Producer (p, ct) => new StoredGetterProducer(p.Left.Left, p.Left.Right.RootNamespace ?? "GeneratedCode", p.Right));

        var diagnosticsProvider = gettersProvider
            .Select(static IDiagnosticReporter (getters, ct) => new StoredGetterReporter(getters));

        context.ProduceCode(sourceProvider);
        context.ReportDiagnostics(diagnosticsProvider);
    }

    /// <summary>A property with a getter that has a body, no setter, and no <c>static</c> or <c>abstract</c>, in a class or record.</summary>
    private static bool IsGetterOnly(SyntaxNode node)
    {
        if (node is not PropertyDeclarationSyntax { Parent: ClassDeclarationSyntax or RecordDeclarationSyntax } property
            || property.Modifiers.Any(SyntaxKind.StaticKeyword)
            || property.Modifiers.Any(SyntaxKind.AbstractKeyword))
            return false;
        if (property.ExpressionBody is not null)
            return true;
        // No list pattern: netstandard2.0 has no System.Index.
        return property.AccessorList is { Accessors.Count: 1 } accessors
               && accessors.Accessors[0] is { RawKind: (int)SyntaxKind.GetAccessorDeclaration } get
               && (get.Body is not null || get.ExpressionBody is not null);
    }

    private static StoredGetterInfo? Describe(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var syntax = (PropertyDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(syntax, ct) is not IPropertySymbol property
            || property.IsStatic || property.IsIndexer || property.IsOverride || property.SetMethod is not null
            || property.DeclaredAccessibility != Accessibility.Public
            || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public }
            || property.GetAttributes().Any(static a => a.AttributeClass?.Name == "JsonIgnoreAttribute"))
            return null;

        var type = property.ContainingType;
        if (!IsStored(type))
            return null;

        var body = (SyntaxNode?)syntax.ExpressionBody?.Expression
                   ?? (SyntaxNode?)syntax.AccessorList!.Accessors[0].ExpressionBody?.Expression
                   ?? syntax.AccessorList!.Accessors[0].Body;
        if (body is null)
            return null;

        return new StoredGetterInfo
        {
            TypeFullyQualifiedName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            TypeName = type.Name,
            Header = PartialTypeHeader.For(type),
            PathSpec = type.GetPathSpec(ct),
            IsPartial = PartialTypeHeader.IsPartialAllTheWayUp(type, ct),
            PartialReportedElsewhere = DeclaresOwnId(type) || IsValueObject(type),
            IsRecord = type.IsRecord,
            Property = property.Name,
            FieldType = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Formula = Formula(body),
            ReadsClock = ClockRead(body, ctx.SemanticModel, ct),
            ReadsEntity = EntityRead(body, type, ctx.SemanticModel, ct),
            Location = property.Locations.FirstOrDefault()?.AsKey(),
        };
    }

    /// <summary>The SHA-256 of the body's tokens, one space apart: formatting and comments (trivia) don't count.</summary>
    internal static string Formula(SyntaxNode body)
    {
        var text = string.Join(" ", body.DescendantTokens().Select(static t => t.Text));
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            builder.Append(b.ToString("x2"));
        return builder.ToString();
    }

    /// <summary>A type Spark stores: its own or a base type's <c>Id&lt;Self&gt;</c>, or <c>[ValueObject]</c>.</summary>
    private static bool IsStored(INamedTypeSymbol type)
        => IsValueObject(type) || IsEntity(type);

    private static bool IsEntity(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (DeclaresOwnId(t.OriginalDefinition))
                return true;
        return false;
    }

    private static bool DeclaresOwnId(INamedTypeSymbol type)
        => type.GetMembers("Id").OfType<IPropertySymbol>().Any(p =>
            !p.IsStatic
            && p.Type is INamedTypeSymbol { IsGenericType: true } id
            && id.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::MintPlayer.Spark.Abstractions.Id<T>"
            && SymbolEqualityComparer.Default.Equals(id.TypeArguments[0].OriginalDefinition, type.OriginalDefinition));

    private static bool IsValueObject(INamedTypeSymbol type)
        => type.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "MintPlayer.Spark.Abstractions.ValueObjectAttribute");

    /// <summary>The first clock member the body reads, as <c>Type.Member</c>: its stored value would be as old as the last save.</summary>
    private static string? ClockRead(SyntaxNode body, SemanticModel model, CancellationToken ct)
    {
        foreach (var name in body.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            if (model.GetSymbolInfo(name, ct).Symbol is not { ContainingType: { } owner } symbol
                || symbol is not (IPropertySymbol or IMethodSymbol or IFieldSymbol))
                continue;
            var ownerName = owner.ToDisplayString();
            var isClock = ownerName switch
            {
                "System.DateTime" => symbol.Name is "Now" or "UtcNow" or "Today",
                "System.DateTimeOffset" => symbol.Name is "Now" or "UtcNow",
                "System.Environment" => symbol.Name is "TickCount" or "TickCount64",
                "System.Diagnostics.Stopwatch" => symbol.Name is "GetTimestamp" or "StartNew" or "GetElapsedTime",
                _ => DerivesFrom(owner, "System.TimeProvider"),
            };
            if (isClock)
                return $"{owner.Name}.{symbol.Name}";
        }
        return null;
    }

    /// <summary>The first other entity type the body reads a value of: its stored value would be as old as this one's last save.</summary>
    private static string? EntityRead(SyntaxNode body, INamedTypeSymbol self, SemanticModel model, CancellationToken ct)
    {
        foreach (var expression in body.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
        {
            if (model.GetTypeInfo(expression, ct).Type is not INamedTypeSymbol type || !IsEntity(type))
                continue;
            if (!DerivesFrom(self, type.OriginalDefinition.ToDisplayString()))
                return type.Name;
        }
        return null;
    }

    private static bool DerivesFrom(INamedTypeSymbol type, string fullName)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.OriginalDefinition.ToDisplayString() == fullName)
                return true;
        return false;
    }

    private static ImmutableArray<StoredGetterInfo> Distinct(IEnumerable<StoredGetterInfo> found)
    {
        var seen = new Dictionary<string, StoredGetterInfo>(StringComparer.Ordinal);
        foreach (var info in found)
        {
            var key = info.TypeFullyQualifiedName + "." + info.Property;
            if (!seen.ContainsKey(key))
                seen.Add(key, info);
        }
        return [.. seen.Values
            .OrderBy(static x => x.TypeFullyQualifiedName, StringComparer.Ordinal)
            .ThenBy(static x => x.Property, StringComparer.Ordinal)];
    }
}

/// <summary>Emits the backing fields and formula attributes <see cref="StoredGetterGenerator"/> found.</summary>
public class StoredGetterProducer : Producer
{
    private readonly ImmutableArray<StoredGetterInfo> getters;
    private readonly bool hasAttribute;

    public StoredGetterProducer(ImmutableArray<StoredGetterInfo> getters, string rootNamespace, bool hasAttribute)
        : base(rootNamespace, "SparkStoredGetters.g.cs")
    {
        this.getters = getters;
        this.hasAttribute = hasAttribute;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var emitted = getters.Where(static g => g.IsPartial && !g.IsRecord).ToArray();
        if (emitted.Length == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        // The field holds whatever the getter returned when the row was saved; a non-nullable reference type would
        // need an initializer that means nothing. EF writes it through reflection, so the compiler sees it unused.
        writer.WriteLine("#nullable disable");
        writer.WriteLine("#pragma warning disable CS0169, CS0414, CS0649");
        writer.WriteLine();

        foreach (var group in emitted.GroupBy(static g => g.PathSpec?.ContainingNamespace ?? string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDisposable? namespaceBlock = string.IsNullOrEmpty(group.Key) ? null : writer.OpenBlock($"namespace {group.Key}");

            foreach (var type in group.GroupBy(static g => g.TypeFullyQualifiedName))
            {
                var first = type.First();
                using var parents = writer.OpenPathSpec(first.PathSpec);
                if (hasAttribute)
                    foreach (var getter in type)
                        writer.WriteLine($"[global::MintPlayer.Spark.Abstractions.SparkStoredGetter(\"{getter.Property}\", \"{getter.Formula}\")]");
                using (writer.OpenBlock(first.Header))
                {
                    foreach (var getter in type)
                    {
                        writer.WriteLine($"/// <summary>Stores <see cref=\"{getter.Property}\"/> on SQL: EF writes it when it loads a row and reads the getter when it saves (#536 O13).</summary>");
                        writer.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                        writer.WriteLine($"private {getter.FieldType} {StoredGetterGenerator.FieldPrefix}{getter.Property};");
                    }
                }
            }

            namespaceBlock?.Dispose();
        }
    }
}

/// <summary>
/// Reports a stored getter that can't get its backing field (SPARK067), and one that reads the clock or another
/// entity (SPARK068).
/// </summary>
public class StoredGetterReporter : IDiagnosticReporter
{
    internal static readonly DiagnosticDescriptor NoBackingFieldRule = new(
        id: "SPARK067",
        title: "A stored getter gets no column on SQL",
        messageFormat: "'{0}.{1}' is stored in a RavenDB document, but gets no column on SQL Server or PostgreSQL: {2}",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RavenDB writes every public getter into the document, so it can be filtered and sorted. The SQL providers store a getter through a generated private backing field (#536 O13); without it the getter is not stored there, and a filter or sort on it fails.");

    internal static readonly DiagnosticDescriptor StaleValueRule = new(
        id: "SPARK068",
        title: "A stored getter reads the clock or another entity",
        messageFormat: "'{0}.{1}' reads {2}: the stored value is only as fresh as the last save, so a filter or sort on it reads an old value",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A get-only member is stored when its entity is saved (#536 O13), on every store. A value computed from the clock or from another entity changes without a save, so the stored copy goes stale. Compute it where it is read, or store the inputs.");

    private readonly ImmutableArray<StoredGetterInfo> getters;

    public StoredGetterReporter(ImmutableArray<StoredGetterInfo> getters)
    {
        this.getters = getters;
    }

    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
    {
        foreach (var getter in getters)
        {
            var location = getter.Location?.ToLocation(compilation);
            if (getter.IsRecord)
                yield return NoBackingFieldRule.Create(location, getter.TypeName, getter.Property,
                    $"'{getter.TypeName}' is a record, which compares every field, so a generated backing field would change its equality; make it a class or give the member a setter");
            else if (!getter.IsPartial && !getter.PartialReportedElsewhere)
                yield return NoBackingFieldRule.Create(location, getter.TypeName, getter.Property,
                    $"declare '{getter.TypeName}' partial, so Spark can generate the backing field");

            if (getter.ReadsClock is { } clock)
                yield return StaleValueRule.Create(location, getter.TypeName, getter.Property, $"the clock ({clock})");
            else if (getter.ReadsEntity is { } entity)
                yield return StaleValueRule.Create(location, getter.TypeName, getter.Property, $"another entity ({entity})");
        }
    }
}
