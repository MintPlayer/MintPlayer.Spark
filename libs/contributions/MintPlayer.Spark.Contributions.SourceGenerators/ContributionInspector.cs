using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Text;

namespace MintPlayer.Spark.Contributions.SourceGenerators;

/// <summary>A rule violation found by <see cref="ContributionInspector"/>.</summary>
internal sealed class ContributionIssue
{
    public ContributionIssue(DiagnosticDescriptor rule, Location location, object?[] args, bool blocksGeneration,
        ImmutableDictionary<string, string?>? properties = null)
    {
        Rule = rule;
        Location = location;
        Args = args;
        BlocksGeneration = blocksGeneration;
        Properties = properties ?? ImmutableDictionary<string, string?>.Empty;
    }

    public DiagnosticDescriptor Rule { get; }
    public Location Location { get; }
    public object?[] Args { get; }

    /// <summary>Whether the generator must emit nothing for the declaration (the output would not compile, or would be wrong).</summary>
    public bool BlocksGeneration { get; }

    public ImmutableDictionary<string, string?> Properties { get; }

    public Diagnostic ToDiagnostic() => Diagnostic.Create(Rule, Location, Properties, Args);
}

/// <summary>
/// The one reading of a <c>[Contribution]</c> declaration, shared by the generator (which emits the
/// model when no issue blocks it) and the analyzer (which reports the issues). Keeping both on one
/// reading is what guarantees the generator never emits for a declaration the analyzer accepts
/// differently.
/// </summary>
/// <remarks>
/// Symbol-based throughout: records, classes and element types from referenced assemblies are read the
/// same way. No filtering on <c>ClassDeclarationSyntax</c>.
/// </remarks>
internal static class ContributionInspector
{
    internal const string ContributionAttributeMetadataName = "MintPlayer.Spark.Contributions.ContributionAttribute";
    internal const string ContributionSlotAttributeMetadataName = "MintPlayer.Spark.Contributions.ContributionSlotAttribute";
    internal const string SoftDeletableMetadataName = "MintPlayer.Spark.SoftDelete.ISoftDeletable";
    private const string NewtonsoftJsonIgnore = "Newtonsoft.Json.JsonIgnoreAttribute";
    private const string ValueObjectAttribute = "MintPlayer.Spark.Abstractions.ValueObjectAttribute";
    private const string ValueKeyAttribute = "MintPlayer.Spark.Abstractions.ValueKeyAttribute";

    /// <summary>Generated files end in this, so the analyzer can tell the generator's members from the author's.</summary>
    internal const string GeneratedFileSuffix = ".Contribution.g.cs";

    /// <summary>Names the generated documents or the element partial declare; an element member may not use them.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "Id", "TargetId", "ContributorId", "ContributionId", "UpdatedAt", "ContributionCount", "ContributorName", "Key",
        "IsDeleted", "DeletedAt", "DeletedBy", "DeleteReason",
    };

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static (ContributionModel? Model, IReadOnlyList<ContributionIssue> Issues) Inspect(
        IPropertySymbol property, AttributeData attribute, Compilation compilation, CancellationToken cancellationToken)
    {
        var issues = new List<ContributionIssue>();
        var location = property.Locations.FirstOrDefault() ?? Location.None;
        var owner = property.ContainingType;
        var display = $"{owner.Name}.{property.Name}";

        void Report(DiagnosticDescriptor rule, bool blocks, params object?[] args)
            => issues.Add(new ContributionIssue(rule, location, args, blocks));

        void Unsupported(string why) => Report(ContributionDiagnostics.UnsupportedShape, true, display, why);

        // --- The owner and the property -------------------------------------------------------
        if (owner.TypeKind != TypeKind.Class || owner.IsStatic)
            Unsupported("the owner must be a class or record class");
        else if (owner.IsGenericType || HasGenericContainer(owner))
            Unsupported("the owner must not be generic, because the generated types are named after it");

        if (property.IsStatic || property.IsIndexer)
            Unsupported("[Contribution] needs an instance property, not a static property or an indexer");
        if (property.GetMethod is null || property.GetMethod.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
            Unsupported("the property needs a public or internal getter");

        if (!property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == NewtonsoftJsonIgnore))
            Report(ContributionDiagnostics.MissingJsonIgnore, false, display);

        if (!HasStringId(owner))
            Report(ContributionDiagnostics.OwnerNotAnEntity, false, owner.Name);

        var (elementType, shape) = Unwrap(property.Type);
        var writable = property.SetMethod is { IsInitOnly: false } setter
            && setter.DeclaredAccessibility is not (Accessibility.Private or Accessibility.Protected);
        if (!writable && shape is not (CollectionShape.List or CollectionShape.IList or CollectionShape.ICollection))
            Unsupported($"a {DescribeShape(shape)} property needs a public or internal setter (only List<T>, IList<T> and ICollection<T> can be filled in place)");

        // --- The element type -----------------------------------------------------------------
        if (elementType is not INamedTypeSymbol element
            || element.TypeKind != TypeKind.Class
            || element.SpecialType != SpecialType.None
            || element.IsAbstract || element.IsStatic)
        {
            Unsupported($"the element type '{property.Type.ToDisplayString()}' must be a non-abstract class or record class");
            return (null, issues);
        }

        if (element.IsGenericType || HasGenericContainer(element))
            Unsupported($"the element '{element.Name}' must not be generic or nested in a generic type");
        else if (element.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable))
            Unsupported($"'{element.ToDisplayString()}' is a collection type the generator does not unwrap; use T, T[], List<T>, IList<T>, IReadOnlyList<T>, ICollection<T> or IEnumerable<T>");
        if (!element.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal))
            Unsupported($"the element '{element.Name}' needs a public parameterless constructor (a positional record has none)");

        var isExternal = !SymbolEqualityComparer.Default.Equals(element.ContainingAssembly, compilation.Assembly);
        if (isExternal)
            Report(ContributionDiagnostics.ElementExternal, true, element.Name, display, element.ContainingAssembly?.Name);

        if (element.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == ValueObjectAttribute))
            Report(ContributionDiagnostics.ReservedMember, true, element.Name, display,
                "remove [ValueObject]: its generator would add a Guid row key, while a contribution row is keyed by its slots");

        // --- Slots and values, base type first, in declaration order ---------------------------
        var slots = new List<SlotModel>();
        var values = new List<ValueModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usedParameterNames = new HashSet<string>(StringComparer.Ordinal) { "targetId", "userId" };
        var declaredSlots = 0; // every [ContributionSlot], valid or not: a refused slot type is still a slot

        foreach (var member in PropertiesBaseFirst(element))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member.IsStatic || member.IsIndexer || !seen.Add(member.Name) || IsGeneratedByUs(member))
                continue;

            var isSlot = member.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == ContributionSlotAttributeMetadataName);
            var hasKeyAttribute = member.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == ValueKeyAttribute);
            var readable = member.DeclaredAccessibility == Accessibility.Public && member.GetMethod is not null;
            var settable = member.SetMethod is { DeclaredAccessibility: Accessibility.Public or Accessibility.Internal };

            if (hasKeyAttribute)
            {
                Report(ContributionDiagnostics.ReservedMember, true, element.Name, display,
                    $"remove [ValueKey] from '{member.Name}': the generator emits the row key from the slots");
                continue;
            }

            if (ReservedNames.Contains(member.Name))
            {
                Report(ContributionDiagnostics.ReservedMember, true, element.Name, display,
                    $"rename '{member.Name}', a name the generated code declares");
                continue;
            }

            if (isSlot)
            {
                declaredSlots++;

                if (!readable || !settable)
                {
                    Unsupported($"slot '{member.Name}' must be public with a getter and a setter (set or init)");
                    continue;
                }

                if (Classify(member.Type) is not { } classified)
                {
                    Report(ContributionDiagnostics.SlotTypeNotAllowed, true, member.Name, element.Name, member.Type.ToDisplayString());
                    continue;
                }

                var parameterName = ParameterName(member.Name, usedParameterNames);
                slots.Add(new SlotModel(member.Name, member.Type.ToDisplayString(TypeFormat), classified.Kind, classified.EnumType,
                    parameterName, Initializer(member.Type)));
            }
            else if (readable && settable)
            {
                values.Add(new ValueModel(member.Name, member.Type.ToDisplayString(TypeFormat), Initializer(member.Type)));
            }
        }

        if (declaredSlots > 0 && shape == CollectionShape.Single)
            Report(ContributionDiagnostics.CardinalityMismatch, true, display,
                $"holds a single '{element.Name}', whose {declaredSlots} [ContributionSlot] properties would allow several current versions; make it a collection, or remove the slots");
        else if (declaredSlots == 0 && shape != CollectionShape.Single)
            Report(ContributionDiagnostics.CardinalityMismatch, true, display,
                $"is a collection, but '{element.Name}' has no [ContributionSlot] property to tell its rows apart; mark the slot properties, or make the property single-valued");

        if (values.Count == 0)
            Report(ContributionDiagnostics.NoValueProperties, true, element.Name, display);

        var attribution = attribute.NamedArguments
            .Where(a => a.Key == "Attribution")
            .Select(a => a.Value.Value is int i ? i : 0)
            .FirstOrDefault();

        // The element gets a partial only when there is something to put in it.
        var elementNeedsPartial = slots.Count > 0 || attribution != 0;
        if (elementNeedsPartial && !isExternal && !IsPartialAllTheWayUp(element))
        {
            var what = slots.Count == 0 ? "its attribution properties"
                : attribution != 0 ? "its row key and attribution properties"
                : "its row key";
            issues.Add(new ContributionIssue(
                ContributionDiagnostics.ElementNotPartial, location,
                [element.ToDisplayString(), what],
                blocksGeneration: true,
                ImmutableDictionary<string, string?>.Empty.Add(ContributionDiagnostics.ElementMetadataNameProperty, MetadataName(element))));
        }

        // --- SoftDelete (PRD Q4) --------------------------------------------------------------
        var softDeletable = compilation.GetTypeByMetadataName(SoftDeletableMetadataName);
        var ns = owner.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : "";
        var emitSoftDelete = false;
        if (softDeletable is null)
        {
            Report(ContributionDiagnostics.NotSoftDeletable, false, owner.Name + property.Name + "Contribution");
        }
        else
        {
            // The app may already have made its own partial ISoftDeletable; declaring the members twice would not compile.
            var contributionName = (ns.Length == 0 ? "" : ns + ".") + owner.Name + property.Name + "Contribution";
            var existing = compilation.GetTypeByMetadataName(contributionName);
            emitSoftDelete = existing is null || !existing.AllInterfaces.Contains(softDeletable, SymbolEqualityComparer.Default);
        }

        if (issues.Any(i => i.BlocksGeneration))
            return (null, issues);

        var elementModel = new ElementDeclaration(
            element.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            element.ContainingNamespace is { IsGlobalNamespace: false } en ? en.ToDisplayString() : "",
            PartialHeader(element),
            new EquatableArray<string>(ContainingTypes(element).Select(PartialHeader)));

        var isPublic = IsEffectivelyPublic(owner) && IsEffectivelyPublic(element);
        var model = new ContributionModel(
            ns, owner.Name, owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), property.Name,
            elementModel, shape, writable,
            new EquatableArray<SlotModel>(slots), new EquatableArray<ValueModel>(values),
            attribution, softDeletable is not null, emitSoftDelete, isPublic,
            ShapeHash(owner, property, elementModel, shape, slots, values, attribution, softDeletable is not null));

        return (model, issues);
    }

    /// <summary>The element type and how the property holds it.</summary>
    private static (ITypeSymbol Element, CollectionShape Shape) Unwrap(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } array)
            return (array.ElementType, CollectionShape.Array);

        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named)
        {
            var element = named.TypeArguments[0];
            switch (named.OriginalDefinition.SpecialType)
            {
                case SpecialType.System_Collections_Generic_IList_T: return (element, CollectionShape.IList);
                case SpecialType.System_Collections_Generic_IReadOnlyList_T: return (element, CollectionShape.IReadOnlyList);
                case SpecialType.System_Collections_Generic_ICollection_T: return (element, CollectionShape.ICollection);
                case SpecialType.System_Collections_Generic_IEnumerable_T: return (element, CollectionShape.IEnumerable);
            }

            if (named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
                return (element, CollectionShape.List);
        }

        return (type, CollectionShape.Single);
    }

    private static string DescribeShape(CollectionShape shape) => shape switch
    {
        CollectionShape.Single => "single-valued",
        CollectionShape.Array => "T[]",
        CollectionShape.IReadOnlyList => "IReadOnlyList<T>",
        CollectionShape.IEnumerable => "IEnumerable<T>",
        _ => shape.ToString(),
    };

    /// <summary>The T2 kind of a slot type, or <see langword="null"/> when it is not allowed.</summary>
    private static (SlotKind Kind, string? EnumType)? Classify(ITypeSymbol type)
    {
        var underlying = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

        if (underlying.TypeKind == TypeKind.Enum)
            return (SlotKind.Enum, underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        switch (underlying.SpecialType)
        {
            case SpecialType.System_String: return (SlotKind.String, null);
            case SpecialType.System_Boolean: return (SlotKind.Bool, null);
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return (SlotKind.Integral, null);
        }

        if (underlying.ToDisplayString() == "System.Guid")
            return (SlotKind.Guid, null);

        return null;
    }

    /// <summary>The initializer a generated document property gets, so a non-nullable reference is never null on a fresh instance.</summary>
    private static string Initializer(ITypeSymbol type)
    {
        if (type.IsValueType || type.NullableAnnotation == NullableAnnotation.Annotated)
            return "";
        return type.SpecialType == SpecialType.System_String ? " = \"\";" : " = default!;";
    }

    private static string ParameterName(string propertyName, HashSet<string> used)
    {
        var name = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
        if (used.Contains(name))
            name += "Slot";
        used.Add(name);
        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
            ? "@" + name
            : name;
    }

    private static IEnumerable<IPropertySymbol> PropertiesBaseFirst(INamedTypeSymbol type)
    {
        var chain = new Stack<INamedTypeSymbol>();
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            chain.Push(t);

        // Base first, so inherited slots come before the type's own (id order). An override keeps the
        // base declaration's position; the caller skips names it has seen.
        var ordered = new List<IPropertySymbol>();
        while (chain.Count > 0)
            ordered.AddRange(chain.Pop().GetMembers().OfType<IPropertySymbol>());
        return ordered;
    }

    /// <summary>Whether this generator declared the member (the analyzer runs on the post-generator compilation).</summary>
    private static bool IsGeneratedByUs(ISymbol member) =>
        member.DeclaringSyntaxReferences.Length > 0
        && member.DeclaringSyntaxReferences.All(r => r.SyntaxTree.FilePath.EndsWith(GeneratedFileSuffix, StringComparison.Ordinal));

    private static bool HasStringId(INamedTypeSymbol owner)
    {
        for (var t = owner; t is not null; t = t.BaseType)
        {
            if (t.GetMembers("Id").OfType<IPropertySymbol>().Any(p =>
                    !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && p.Type.SpecialType == SpecialType.System_String))
                return true;
        }
        return false;
    }

    private static bool HasGenericContainer(INamedTypeSymbol type)
    {
        for (var t = type.ContainingType; t is not null; t = t.ContainingType)
        {
            if (t.IsGenericType)
                return true;
        }
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> ContainingTypes(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var t = type.ContainingType; t is not null; t = t.ContainingType)
            chain.Insert(0, t);
        return chain;
    }

    private static bool IsPartialAllTheWayUp(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
        {
            var declarations = t.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .OfType<TypeDeclarationSyntax>()
                .ToList();
            if (declarations.Count == 0 || !declarations.Any(d => d.Modifiers.Any(SyntaxKind.PartialKeyword)))
                return false;
        }
        return true;
    }

    private static string PartialHeader(INamedTypeSymbol type)
    {
        var keyword = type.TypeKind switch
        {
            TypeKind.Struct when type.IsRecord => "record struct",
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            _ when type.IsRecord => "record",
            _ => "class",
        };
        return $"partial {keyword} {type.Name}";
    }

    private static bool IsEffectivelyPublic(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility != Accessibility.Public)
                return false;
        }
        return true;
    }

    /// <summary>The name <c>Compilation.GetTypeByMetadataName</c> resolves (<c>NS.Outer+Inner</c>).</summary>
    internal static string MetadataName(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        for (var t = type.ContainingType; t is not null; t = t.ContainingType)
            name = t.MetadataName + "+" + name;
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
    }

    /// <summary>
    /// A stable hash of everything that decides what a current document holds. FNV-1a 64 over a
    /// canonical text, never <c>string.GetHashCode</c> (randomized per process).
    /// </summary>
    private static string ShapeHash(INamedTypeSymbol owner, IPropertySymbol property, ElementDeclaration element,
        CollectionShape shape, List<SlotModel> slots, List<ValueModel> values, int attribution, bool softDelete)
    {
        var text = new StringBuilder("contributions-shape-v1")
            .Append('|').Append(owner.ToDisplayString())
            .Append('|').Append(property.Name)
            .Append('|').Append(element.FullyQualified)
            .Append('|').Append(shape == CollectionShape.Single ? "single" : "collection")
            .Append("|slots:").Append(string.Join(",", slots.Select(s => s.Name + ":" + s.Type)))
            .Append("|values:").Append(string.Join(",", values.Select(v => v.Name + ":" + v.Type)))
            .Append("|attribution:").Append(attribution)
            .Append("|softdelete:").Append(softDelete ? 1 : 0)
            .ToString();

        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash = unchecked(hash * 1099511628211UL);
        }
        return hash.ToString("x16");
    }
}
