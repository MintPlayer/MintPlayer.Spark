using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Contracts;

/// <summary>One generated <c>contracts.ts</c>: the module that owns it and its text.</summary>
/// <param name="Module">The import specifier (<c>@mintplayer/ng-spark/models</c>) or <see cref="SparkClientModuleAttribute.Application"/>.</param>
/// <param name="Content">The file's text, <c>\n</c> line endings, ending in one newline.</param>
public sealed record SparkContractModule(string Module, string Content);

/// <summary>The contracts could not be generated; <see cref="Errors"/> lists every reason, in a stable order.</summary>
public sealed class SparkContractException(IReadOnlyList<string> errors)
    : Exception("The TypeScript contracts cannot be generated:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Generates TypeScript from the C# types the client reads and writes (commerce X20 item 2,
/// <c>docs/guide-typescript-contracts.md</c>): every <see cref="SparkContractAttribute"/> type of the
/// <em>own</em> assemblies, and every type its members reach, as one <c>contracts.ts</c> per module that
/// claims their namespaces (<see cref="SparkClientModuleAttribute"/>).
/// </summary>
/// <remarks>
/// <para>
/// The TypeScript states the <b>wire</b>, as ASP.NET Core's web defaults write it (the options every Spark
/// endpoint serializes with), following System.Text.Json's own rules: public instance properties with a public
/// getter, plus <c>[JsonInclude]</c> members; <c>[JsonIgnore]</c> drops a member and its <c>WhenWritingNull</c>/
/// <c>WhenWritingDefault</c> conditions make it optional; <c>[JsonPropertyName]</c>, else camelCase. A nullable
/// member (annotated <c>?</c>) is <c>name?: T | null</c>: the server writes the null, and a client building a
/// request may leave it out. An enum is a TypeScript <c>enum</c> of its numbers, unless a
/// <c>JsonStringEnumConverter</c> (on the type or the member) writes its names: then a union of those names, or
/// <c>string</c> for a <c>[Flags]</c> enum, whose combinations are comma-joined names.
/// </para>
/// <para>
/// ⚠️ Deterministic on purpose: CI regenerates and fails on any difference. No doc comments (whether an XML
/// documentation file sits next to an assembly would decide the output), no timestamps, stable order.
/// </para>
/// <para>
/// A type in a module an own assembly claims is emitted; a type in another module (a library's, seen from an
/// application) is imported from that module, which owns its declaration. A reached type whose namespace no
/// module claims is an error, as are shapes the TypeScript cannot state truthfully (open generics, extension
/// data, polymorphism, a converter-backed type nobody vouched for).
/// </para>
/// </remarks>
public static partial class SparkContractGenerator
{
    private static readonly JsonNamingPolicy NamingPolicy = JsonNamingPolicy.CamelCase;

    /// <summary>
    /// Generates every module the <paramref name="own"/> assemblies claim.
    /// </summary>
    /// <param name="own">The assemblies whose contracts are emitted.</param>
    /// <param name="visible">Further assemblies whose claims and hand-written types are known (referenced libraries); their types are imported, never emitted.</param>
    /// <param name="regenerateCommand">The command named in each file's header.</param>
    /// <exception cref="SparkContractException">A contract cannot be stated; every reason is listed.</exception>
    public static IReadOnlyList<SparkContractModule> Generate(IEnumerable<Assembly> own, IEnumerable<Assembly> visible, string regenerateCommand)
        => Generate(own, visible, regenerateCommand, static _ => true);

    /// <summary>As the public overload, with only the <see cref="SparkContractAttribute"/> types <paramref name="isRoot"/> accepts as roots (the tests' seam: one fixture set per test).</summary>
    internal static IReadOnlyList<SparkContractModule> Generate(IEnumerable<Assembly> own, IEnumerable<Assembly> visible, string regenerateCommand, Func<Type, bool> isRoot)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(regenerateCommand);
        ArgumentNullException.ThrowIfNull(isRoot);

        var ownSet = own.Distinct().ToList();
        var all = ownSet.Concat(visible).Distinct().OrderBy(a => a.GetName().Name, StringComparer.Ordinal).ToList();
        return new Run(ownSet.ToHashSet(), all, regenerateCommand, isRoot).Execute();
    }

    /// <summary>
    /// The file of a library module: <c>@mintplayer/&lt;package&gt;/&lt;entry&gt;</c> is
    /// <c>libs/node_packages/&lt;package&gt;/&lt;entry&gt;/src/generated/contracts.ts</c> under <paramref name="repositoryRoot"/>.
    /// </summary>
    public static string LibraryModulePath(string repositoryRoot, string module)
    {
        const string scope = "@mintplayer/";
        if (!module.StartsWith(scope, StringComparison.Ordinal))
            throw new ArgumentException($"Library module '{module}' is not in the {scope} scope.", nameof(module));

        var segments = module[scope.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            throw new ArgumentException($"Library module '{module}' names no package.", nameof(module));

        return Path.Combine([repositoryRoot, "libs", "node_packages", .. segments, "src", "generated", "contracts.ts"]);
    }

    /// <summary>The file of an application's module: <c>ClientApp/src/app/generated/contracts.ts</c> under its content root.</summary>
    public static string ApplicationModulePath(string contentRootPath)
        => Path.Combine(contentRootPath, "ClientApp", "src", "app", "generated", "contracts.ts");

    /// <summary>
    /// Writes each module to <paramref name="pathOf"/>'s file, or with <paramref name="verifyOnly"/> only compares,
    /// and returns the files that differed (missing, or other text). Line endings are compared normalized, so a
    /// checkout with CRLF is not drift.
    /// </summary>
    public static IReadOnlyList<string> Synchronize(IReadOnlyList<SparkContractModule> modules, Func<string, string> pathOf, bool verifyOnly)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(pathOf);

        var drifted = new List<string>();
        foreach (var module in modules)
        {
            var path = pathOf(module.Module);
            var current = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
            if (current == module.Content)
                continue;

            drifted.Add(path);
            if (!verifyOnly)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, module.Content);
            }
        }
        return drifted;
    }

    private sealed record Claim(string Namespace, string Module, Assembly Declarer);

    private sealed record HandWritten(string Name, string File);

    private sealed class Declaration(Type type, string module, string name)
    {
        public Type Type { get; } = type;
        public string Module { get; } = module;
        public string Name { get; } = name;
        public string? Text { get; set; }
    }

    private sealed partial class Run(HashSet<Assembly> own, IReadOnlyList<Assembly> all, string regenerateCommand, Func<Type, bool> isRoot)
    {
        private readonly List<string> errors = [];
        private readonly List<Claim> claims = [];
        private readonly Dictionary<Type, HandWritten> handWritten = [];
        private readonly Dictionary<Type, Declaration> declarations = [];
        private readonly Queue<Declaration> pending = new();

        /// <summary>Per module being written: import specifier → names imported from it.</summary>
        private readonly Dictionary<string, SortedDictionary<string, SortedSet<string>>> imports = new(StringComparer.Ordinal);

        public IReadOnlyList<SparkContractModule> Execute()
        {
            ReadClaims();

            foreach (var root in all.Where(own.Contains).SelectMany(LoadableTypes)
                         .Where(t => t.IsDefined(typeof(SparkContractAttribute), inherit: false) && isRoot(t))
                         .OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (root.IsGenericTypeDefinition)
                {
                    errors.Add($"{root.FullName}: a [SparkContract] type cannot be generic.");
                    continue;
                }
                Reference(root, fromModule: null, where: root.FullName!);
            }

            while (pending.TryDequeue(out var declaration))
                declaration.Text = Declare(declaration);

            var ownModules = claims.Where(c => own.Contains(c.Declarer)).Select(c => c.Module).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();

            foreach (var group in declarations.Values.GroupBy(d => (d.Module, d.Name)).Where(g => g.Count() > 1))
                errors.Add($"Module '{group.Key.Module}' would declare '{group.Key.Name}' twice: {string.Join(", ", group.Select(d => d.Type.FullName).Order(StringComparer.Ordinal))}. Rename one with [SparkContract(\"Name\")].");

            if (errors.Count > 0)
                throw new SparkContractException([.. errors.Distinct().Order(StringComparer.Ordinal)]);

            return [.. ownModules.Select(m => new SparkContractModule(m, Render(m)))];
        }

        private void ReadClaims()
        {
            foreach (var assembly in all)
            {
                foreach (var claim in assembly.GetCustomAttributes<SparkClientModuleAttribute>())
                {
                    if (string.IsNullOrWhiteSpace(claim.Namespace) || string.IsNullOrWhiteSpace(claim.Module))
                    {
                        errors.Add($"{assembly.GetName().Name}: [SparkClientModule] needs a namespace and a module.");
                        continue;
                    }
                    if (claims.FirstOrDefault(c => c.Namespace == claim.Namespace) is { } existing)
                    {
                        if (existing.Module != claim.Module)
                            errors.Add($"Namespace '{claim.Namespace}' is claimed by '{existing.Module}' ({existing.Declarer.GetName().Name}) and by '{claim.Module}' ({assembly.GetName().Name}).");
                        continue;
                    }
                    claims.Add(new Claim(claim.Namespace, claim.Module, assembly));
                }

                foreach (var type in assembly.GetCustomAttributes<SparkClientTypeAttribute>())
                    handWritten[type.Type] = new HandWritten(type.Name, type.File);
            }
        }

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.OfType<Type>();
            }
        }

        private Claim? ClaimOf(Type type)
        {
            var ns = type.Namespace ?? "";
            return claims
                .Where(c => ns == c.Namespace || ns.StartsWith(c.Namespace + ".", StringComparison.Ordinal))
                .MaxBy(c => c.Namespace.Length);
        }

        /// <summary>The TypeScript name of a named type (declared, or hand-written), recording the import <paramref name="fromModule"/> needs.</summary>
        private string Reference(Type type, string? fromModule, string where)
        {
            var claim = ClaimOf(type);

            if (handWritten.TryGetValue(type, out var written))
            {
                if (claim is null)
                {
                    errors.Add($"{type.FullName} (reached from {where}) is hand-written in TypeScript, but no [assembly: SparkClientModule] claims its namespace '{type.Namespace}', so its module is unknown.");
                    return "unknown";
                }
                if (fromModule is not null)
                    Import(fromModule, claim.Module == fromModule ? "../" + written.File : claim.Module, written.Name);
                return written.Name;
            }

            if (claim is null)
            {
                errors.Add($"{type.FullName} (reached from {where}) is in namespace '{type.Namespace}', which no [assembly: SparkClientModule] claims.");
                return "unknown";
            }

            var name = type.GetCustomAttribute<SparkContractAttribute>()?.Name ?? type.Name;
            if (own.Contains(claim.Declarer))
            {
                if (!declarations.ContainsKey(type))
                {
                    var declaration = new Declaration(type, claim.Module, name);
                    declarations[type] = declaration;
                    pending.Enqueue(declaration);
                }
            }
            else if (own.Contains(type.Assembly))
            {
                errors.Add($"{type.FullName} is declared in {type.Assembly.GetName().Name}, but its namespace is claimed by '{claim.Module}' ({claim.Declarer.GetName().Name}), which this run does not write.");
            }

            if (fromModule is not null && claim.Module != fromModule)
                Import(fromModule, claim.Module, name);
            return name;
        }

        private void Import(string intoModule, string specifier, string name)
        {
            if (!imports.TryGetValue(intoModule, out var bySpecifier))
                imports[intoModule] = bySpecifier = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            if (!bySpecifier.TryGetValue(specifier, out var names))
                bySpecifier[specifier] = names = new SortedSet<string>(StringComparer.Ordinal);
            names.Add(name);
        }

        private string Declare(Declaration declaration)
        {
            var type = declaration.Type;
            if (type.IsGenericType)
            {
                errors.Add($"{type.FullName}: generic contract types are not supported.");
                return "";
            }
            if (type.IsEnum)
                return DeclareEnum(declaration);

            if (type.IsDefined(typeof(JsonPolymorphicAttribute), inherit: false) || type.IsDefined(typeof(JsonDerivedTypeAttribute), inherit: false))
                errors.Add($"{type.FullName}: [JsonPolymorphic]/[JsonDerivedType] are not supported; the TypeScript would have to be a union.");

            if (CustomConverter(type) is { } converter && !type.IsDefined(typeof(SparkContractAttribute), inherit: false))
                errors.Add($"{type.FullName} is written by {converter.Name}, so its properties need not be its wire shape. Declare its TypeScript with [assembly: SparkClientType], or mark it [SparkContract] if the converter writes exactly its properties.");

            var builder = new StringBuilder();
            builder.Append("export interface ").Append(declaration.Name);
            if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType))
                builder.Append(" extends ").Append(Reference(baseType, declaration.Module, type.FullName!));
            builder.Append(" {\n");

            foreach (var member in Members(type))
            {
                var where = $"{type.FullName}.{member.Info.Name}";
                if (member.Info.IsDefined(typeof(JsonExtensionDataAttribute), inherit: false))
                {
                    errors.Add($"{where}: [JsonExtensionData] is not supported.");
                    continue;
                }

                var ignore = member.Info.GetCustomAttribute<JsonIgnoreAttribute>();
                if (ignore is not null && ignore.Condition == JsonIgnoreCondition.Always)
                    continue;
                var omittedWhenDefault = ignore?.Condition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault;

                var nullability = member.Nullability;
                var (ts, nullable) = Render(member.Type, nullability, member.Info.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType, declaration.Module, where);

                var name = member.Info.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? NamingPolicy.ConvertName(member.Info.Name);
                builder.Append("  ").Append(PropertyKey(name));
                if (omittedWhenDefault)
                    builder.Append("?: ").Append(ts);
                else if (nullable)
                    builder.Append("?: ").Append(ts == "any" ? ts : ts + " | null");
                else
                    builder.Append(": ").Append(ts);
                builder.Append(";\n");
            }

            builder.Append('}');
            return builder.ToString();
        }

        private string DeclareEnum(Declaration declaration)
        {
            var type = declaration.Type;
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .OrderBy(f => Convert.ToInt64(f.GetRawConstantValue()))
                .ThenBy(f => f.MetadataToken)
                .ToList();

            if (IsStringEnumConverter(CustomConverter(type)))
            {
                // A [Flags] enum with names never gets here: it is rendered as `string` where it is used.
                var names = fields.Select(f => "'" + EnumMemberName(f) + "'");
                return $"export type {declaration.Name} = {string.Join(" | ", names)};";
            }

            var builder = new StringBuilder();
            builder.Append("export enum ").Append(declaration.Name).Append(" {\n");
            foreach (var field in fields)
                builder.Append("  ").Append(field.Name).Append(" = ").Append(Convert.ToInt64(field.GetRawConstantValue())).Append(",\n");
            builder.Append('}');
            return builder.ToString();
        }

        private static string EnumMemberName(FieldInfo field)
            => field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? field.Name;

        private sealed record Member(MemberInfo Info, Type Type, NullabilityInfo Nullability);

        /// <summary>The members System.Text.Json serializes, declared on <paramref name="type"/> itself, in declaration order.</summary>
        private static IEnumerable<Member> Members(Type type)
        {
            var context = new NullabilityInfoContext();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            var properties = type.GetProperties(flags)
                .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null)
                .Where(p => p.GetMethod!.IsPublic || p.IsDefined(typeof(JsonIncludeAttribute), inherit: false))
                .OrderBy(p => p.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0)
                .ThenBy(p => p.MetadataToken)
                .Select(p => new Member(p, p.PropertyType, context.Create(p)));

            var fields = type.GetFields(flags)
                .Where(f => f.IsDefined(typeof(JsonIncludeAttribute), inherit: false))
                .OrderBy(f => f.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0)
                .ThenBy(f => f.MetadataToken)
                .Select(f => new Member(f, f.FieldType, context.Create(f)));

            return properties.Concat(fields);
        }

        /// <summary>The TypeScript of a value of <paramref name="type"/>, and whether it may be null.</summary>
        private (string Ts, bool Nullable) Render(Type type, NullabilityInfo? nullability, Type? memberConverter, string module, string where)
        {
            var nullable = false;
            if (Nullable.GetUnderlyingType(type) is { } underlying)
            {
                type = underlying;
                nullable = true;
            }
            else if (!type.IsValueType && nullability?.ReadState == NullabilityState.Nullable)
            {
                nullable = true;
            }

            return (RenderNonNull(type, nullability, memberConverter, module, where), nullable);
        }

        private string RenderElement(Type type, NullabilityInfo? nullability, string module, string where)
        {
            var (ts, nullable) = Render(type, nullability, memberConverter: null, module, where);
            return nullable && ts != "any" ? $"{ts} | null" : ts;
        }

        private string RenderNonNull(Type type, NullabilityInfo? nullability, Type? memberConverter, string module, string where)
        {
            if (handWritten.ContainsKey(type))
                return Reference(type, module, where);

            if (type.IsEnum)
            {
                var converter = memberConverter ?? CustomConverter(type);
                if (IsStringEnumConverter(converter))
                {
                    if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
                        return "string";
                    if (memberConverter is not null)
                        return string.Join(" | ", type.GetFields(BindingFlags.Public | BindingFlags.Static)
                            .OrderBy(f => Convert.ToInt64(f.GetRawConstantValue())).ThenBy(f => f.MetadataToken)
                            .Select(f => "'" + EnumMemberName(f) + "'"));
                }
                else if (converter is not null)
                {
                    errors.Add($"{where}: the enum {type.FullName} is written by {converter.Name}, whose output is unknown.");
                    return "unknown";
                }
                return Reference(type, module, where);
            }

            if (Primitive(type) is { } primitive)
                return primitive;

            if (memberConverter is not null)
            {
                errors.Add($"{where} is written by {memberConverter.Name}, whose output is unknown. Give the member a type whose JSON is its shape.");
                return "unknown";
            }

            if (type.IsArray)
            {
                var element = type.GetElementType()!;
                return $"{Parenthesize(RenderElement(element, nullability?.ElementType, module, where))}[]";
            }

            if (DictionaryTypes(type) is { } dictionary)
            {
                if (Primitive(dictionary.Key) is not ("string" or "number") && !dictionary.Key.IsEnum)
                {
                    errors.Add($"{where}: a dictionary keyed by {dictionary.Key.Name} has no JSON object form.");
                    return "unknown";
                }
                var valueNullability = type.IsGenericType && type.GetGenericArguments().Length == 2 ? nullability?.GenericTypeArguments.ElementAtOrDefault(1) : null;
                return $"Record<string, {RenderElement(dictionary.Value, valueNullability, module, where)}>";
            }

            if (EnumerableElement(type) is { } item)
            {
                var itemNullability = type.IsGenericType && type.GetGenericArguments().Length == 1 ? nullability?.GenericTypeArguments.ElementAtOrDefault(0) : null;
                return $"{Parenthesize(RenderElement(item, itemNullability, module, where))}[]";
            }

            if (type.IsGenericType)
            {
                errors.Add($"{where}: the generic type {type.Name} is not supported; declare a closed record instead.");
                return "unknown";
            }

            if (type.IsInterface || type.IsAbstract && !type.IsDefined(typeof(SparkContractAttribute), inherit: false) || typeof(Delegate).IsAssignableFrom(type))
            {
                errors.Add($"{where}: {type.FullName} has no fixed JSON shape (an interface, abstract or delegate type).");
                return "unknown";
            }

            return Reference(type, module, where);
        }

        private static string Parenthesize(string ts) => ts.Contains(' ') ? $"({ts})" : ts;

        private static string? Primitive(Type type)
        {
            if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) || type == typeof(Uri)
                || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly)
                || type == typeof(TimeSpan) || type == typeof(byte[]) || type == typeof(Version))
                return "string";
            if (type == typeof(bool))
                return "boolean";
            // long and decimal too: System.Text.Json writes them as JSON numbers, so the client receives a number.
            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
                || type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type == typeof(Half))
                return "number";
            if (type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(JsonValue) || type == typeof(JsonDocument))
                return "any";
            if (type == typeof(JsonObject))
                return "Record<string, any>";
            if (type == typeof(JsonArray))
                return "any[]";
            return null;
        }

        private static (Type Key, Type Value)? DictionaryTypes(Type type)
        {
            foreach (var candidate in type.GetInterfaces().Prepend(type))
            {
                if (!candidate.IsGenericType)
                    continue;
                var definition = candidate.GetGenericTypeDefinition();
                if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                {
                    var arguments = candidate.GetGenericArguments();
                    return (arguments[0], arguments[1]);
                }
            }
            return null;
        }

        private static Type? EnumerableElement(Type type)
        {
            if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
                return null;
            return type.GetInterfaces().Prepend(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
        }

        private static Type? CustomConverter(Type type) => type.GetCustomAttribute<JsonConverterAttribute>(inherit: false)?.ConverterType;

        private static bool IsStringEnumConverter(Type? converter)
        {
            for (var current = converter; current is not null; current = current.BaseType)
            {
                if (current == typeof(JsonStringEnumConverter))
                    return true;
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>))
                    return true;
            }
            return false;
        }

        private static string PropertyKey(string name) => IdentifierPattern().IsMatch(name) ? name : "'" + name.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

        [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$")]
        private static partial Regex IdentifierPattern();

        private string Render(string module)
        {
            var builder = new StringBuilder();
            builder.Append("// <auto-generated>\n");
            builder.Append("// Generated from the C# types marked [SparkContract] (docs/guide-typescript-contracts.md).\n");
            builder.Append("// Do not edit: regenerate with `").Append(regenerateCommand).Append("`.\n");
            builder.Append("// CI fails when this file differs from what the generator writes.\n");
            builder.Append("// </auto-generated>\n");

            if (imports.TryGetValue(module, out var bySpecifier) && bySpecifier.Count > 0)
            {
                builder.Append('\n');
                foreach (var (specifier, names) in bySpecifier)
                    builder.Append("import type { ").Append(string.Join(", ", names)).Append(" } from '").Append(specifier).Append("';\n");
            }

            var moduleDeclarations = declarations.Values
                .Where(d => d.Module == module && !string.IsNullOrEmpty(d.Text))
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();

            if (moduleDeclarations.Count == 0)
            {
                // Keeps the file a module, so the entry point's `export *` of it compiles.
                builder.Append("\nexport {};\n");
                return builder.ToString();
            }

            foreach (var declaration in moduleDeclarations)
                builder.Append('\n').Append(declaration.Text).Append('\n');
            return builder.ToString();
        }
    }
}
