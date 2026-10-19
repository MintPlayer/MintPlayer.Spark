using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Replication.Abstractions;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>One field of a contract record and the source path it is read from.</summary>
/// <param name="Target">The contract record's member name.</param>
/// <param name="Path">The member path on the source document, outermost first.</param>
internal sealed record EtlField(string Target, IReadOnlyList<string> Path);

/// <summary>
/// Turns a contract's C# projection into a RavenDB ETL transform (X20 item 7, warehouse Q-W17).
/// <para>
/// Only what JavaScript can do identically is accepted: a field selection with optional renames,
/// written as a member initializer or a positional record constructor whose values are member paths
/// on the source parameter. Nested paths are read null-safely. Everything else — method calls,
/// arithmetic, conditionals, constants, captured variables, <c>Nullable.Value</c> — is refused with
/// an <see cref="InvalidOperationException"/> naming the contract and the member, at startup.
/// </para>
/// <para>
/// Every identifier the script contains is checked against <c>^[A-Za-z_][A-Za-z0-9_]*$</c> before
/// it is written: the script is code that runs on the owner's database, so nothing reaches it that
/// is not a plain name (F16).
/// </para>
/// </summary>
internal static partial class EtlTransformGenerator
{
    /// <summary>The fields <paramref name="contract"/>'s projection selects, or a refusal.</summary>
    /// <exception cref="InvalidOperationException">The projection is not ETL-expressible.</exception>
    public static IReadOnlyList<EtlField> Translate(ReplicationContract contract)
    {
        var parameter = contract.Projection.Parameters.Single();
        var fields = new List<EtlField>();

        switch (contract.Projection.Body)
        {
            case MemberInitExpression init:
                AddConstructorArguments(contract, init.NewExpression, parameter, fields);
                foreach (var binding in init.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        throw Refused(contract, binding.Member.Name, "uses a nested or collection initializer");
                    Add(contract, fields, assignment.Member.Name, assignment.Expression, parameter);
                }
                break;

            case NewExpression ctor:
                AddConstructorArguments(contract, ctor, parameter, fields);
                break;

            default:
                throw Refused(contract, null, "does not construct the contract record (write s => new " +
                    contract.ContractType.Name + " { … } or a positional record constructor)");
        }

        var duplicate = fields.GroupBy(f => f.Target, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw Refused(contract, duplicate.Key, "is assigned twice");

        if (fields.Count == 0)
            throw Refused(contract, null, "selects no fields");

        return fields;
    }

    /// <summary>
    /// The ETL transform script that loads <paramref name="fields"/> into
    /// <paramref name="targetCollection"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="targetCollection"/> is not a plain identifier.</exception>
    public static string Script(IReadOnlyList<EtlField> fields, string targetCollection)
    {
        if (!IsIdentifier(targetCollection))
            throw new ArgumentException($"'{targetCollection}' is not a valid ETL target collection: only letters, digits and '_' are allowed.", nameof(targetCollection));

        var script = new StringBuilder();
        script.Append("loadTo").Append(targetCollection).Append("({\n");
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            // Translate already checked these; the second check keeps a future caller that builds
            // EtlField by hand from writing an unchecked name into code.
            if (!IsIdentifier(field.Target) || field.Path.Count == 0 || !field.Path.All(IsIdentifier))
                throw new ArgumentException($"Field '{field.Target}' is not a valid ETL field.", nameof(fields));

            script.Append("    ").Append(field.Target).Append(": ").Append(Access("this", field.Path, 0));
            script.Append(i < fields.Count - 1 ? ",\n" : "\n");
        }
        script.Append("});\n");
        return script.ToString();
    }

    /// <summary>Whether <paramref name="value"/> is a plain ASCII identifier.</summary>
    public static bool IsIdentifier(string? value) => value is not null && IdentifierPattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    /// <summary><c>this.A</c>, or <c>(this.A == null ? null : this.A.B)</c> for a nested path, and so on.</summary>
    private static string Access(string prefix, IReadOnlyList<string> path, int index)
    {
        var current = prefix + "." + path[index];
        return index == path.Count - 1
            ? current
            : $"({current} == null ? null : {Access(current, path, index + 1)})";
    }

    private static void AddConstructorArguments(ReplicationContract contract, NewExpression ctor, ParameterExpression parameter, List<EtlField> fields)
    {
        if (ctor.Arguments.Count == 0)
            return;

        var parameters = ctor.Constructor?.GetParameters() ?? [];
        for (var i = 0; i < ctor.Arguments.Count; i++)
        {
            // An anonymous type names its members; a positional record names its parameters after
            // the properties they initialize.
            var target = ctor.Members?[i].Name
                ?? (i < parameters.Length ? MatchingProperty(contract.ContractType, parameters[i].Name) : null)
                ?? throw Refused(contract, i < parameters.Length ? parameters[i].Name : $"#{i}",
                    "is a constructor argument that initializes no property of the same name");
            Add(contract, fields, target, ctor.Arguments[i], parameter);
        }
    }

    private static string? MatchingProperty(Type type, string? parameterName)
        => parameterName is null
            ? null
            : type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase))?.Name;

    private static void Add(ReplicationContract contract, List<EtlField> fields, string target, Expression value, ParameterExpression parameter)
    {
        // The document id travels with the ETL itself; a body field named Id would only duplicate it.
        if (target == "Id")
            return;

        if (!IsIdentifier(target))
            throw Refused(contract, target, "is not a plain identifier");

        if (!TryPath(value, parameter, out var path, out var reason))
            throw Refused(contract, target, reason);

        fields.Add(new EtlField(target, path));
    }

    private static bool TryPath(Expression value, ParameterExpression parameter, out IReadOnlyList<string> path, out string reason)
    {
        path = [];

        // Lifting to Nullable<T> or an upcast is a no-op in JavaScript; any other conversion is not.
        if (value is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
        {
            var lifts = Nullable.GetUnderlyingType(conversion.Type) == conversion.Operand.Type;
            var upcasts = !conversion.Type.IsValueType && conversion.Type.IsAssignableFrom(conversion.Operand.Type);
            if (!lifts && !upcasts)
            {
                reason = $"converts {conversion.Operand.Type.Name} to {conversion.Type.Name}";
                return false;
            }
            value = conversion.Operand;
        }

        var names = new List<string>();
        var current = value;
        while (current is MemberExpression member)
        {
            if (member.Expression is null)
            {
                reason = $"reads the static member {member.Member.Name}";
                return false;
            }
            if (member.Member.DeclaringType is { IsGenericType: true } declaring && declaring.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                reason = $"reads Nullable.{member.Member.Name}; select the nullable value itself";
                return false;
            }
            if (!IsIdentifier(member.Member.Name))
            {
                reason = $"reads '{member.Member.Name}', which is not a plain identifier";
                return false;
            }
            names.Add(member.Member.Name);
            current = member.Expression;
        }

        if (current != parameter || names.Count == 0)
        {
            reason = current switch
            {
                MethodCallExpression call => $"calls {call.Method.Name}()",
                ConstantExpression or MemberExpression => "is a constant or a captured variable",
                BinaryExpression => "computes a value",
                ConditionalExpression => "is conditional",
                ParameterExpression when names.Count == 0 => "is the whole source document",
                _ => $"is a {current.NodeType} expression",
            };
            return false;
        }

        names.Reverse();
        path = names;
        reason = string.Empty;
        return true;
    }

    private static InvalidOperationException Refused(ReplicationContract contract, string? member, string reason) => new(
        $"Replication contract '{contract.Name}' v{contract.Version} cannot be published over ETL: " +
        (member is null ? "the projection " : $"member '{member}' ") + reason + ". " +
        "The owner generates the ETL transform from the projection, so it must be a plain field selection with optional renames, " +
        $"e.g. s => new {contract.ContractType.Name} {{ Plate = s.LicensePlate, Street = s.Address.Street }}. " +
        "Store a computed value on the source document and select it, or publish the contract over the HTTP transport.");
}
