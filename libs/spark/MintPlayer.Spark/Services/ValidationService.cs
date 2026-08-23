using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Services;

public interface IValidationService
{
    /// <summary>
    /// Validates <paramref name="persistentObject"/>'s values against the rules the model declares.
    /// </summary>
    ValidationResult Validate(PersistentObject persistentObject);

    /// <summary>
    /// Validates an object against the rules <em>it carries</em>, rather than the ones the model
    /// declares.
    /// <para>
    /// This is what a refresh hook's reshaping is worth: a hook that makes a field required, or
    /// lifts a rule from one, has changed the effective contract, and only an object-driven pass
    /// enforces it. The caller is responsible for building that object from the model and running
    /// the hook — trusting rules straight off the wire would let a client declare itself valid.
    /// </para>
    /// </summary>
    ValidationResult ValidateEffective(PersistentObject effective);
}

/// <summary>
/// What the rule engine needs to know about one attribute. Exists so the same rules can be driven
/// by a model definition or by a reshaped wire attribute, which are different types carrying the
/// same four facts.
/// </summary>
internal readonly record struct ValidationTarget(
    string Name,
    TranslatedString? Label,
    bool IsRequired,
    ValidationRule[] Rules,
    object? Value);

[Register(typeof(IValidationService), ServiceLifetime.Scoped)]
internal partial class ValidationService : IValidationService
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ITranslationsLoader translationsLoader;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^https?:\/\/[^\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public ValidationResult Validate(PersistentObject persistentObject)
    {
        var entityType = modelLoader.GetEntityType(persistentObject.ObjectTypeId);
        if (entityType == null)
        {
            return new ValidationResult();
        }

        return Run(entityType.Attributes.Select(attrDef => new ValidationTarget(
            attrDef.Name,
            attrDef.Label,
            attrDef.IsRequired,
            attrDef.Rules ?? [],
            persistentObject.Attributes.FirstOrDefault(a => a.Name == attrDef.Name)?.Value)));
    }

    public ValidationResult ValidateEffective(PersistentObject effective) =>
        Run(effective.Attributes.Select(attribute => new ValidationTarget(
            attribute.Name,
            attribute.Label,
            attribute.IsRequired,
            attribute.Rules ?? [],
            attribute.Value)));

    private ValidationResult Run(IEnumerable<ValidationTarget> targets)
    {
        var result = new ValidationResult();

        foreach (var attrDef in targets)
        {
            var value = attrDef.Value;

            // Check required
            if (attrDef.IsRequired && IsEmpty(value))
            {
                result.Errors.Add(new ValidationError
                {
                    AttributeName = attrDef.Name,
                    RuleType = "required",
                    ErrorMessage = FormatTranslatedMessage("validation.required", attrDef.Label, attrDef.Name)
                });
                continue; // Skip other validations if required field is empty
            }

            // Skip validation rules if value is empty (and not required)
            if (IsEmpty(value))
            {
                continue;
            }

            // Apply validation rules
            foreach (var rule in attrDef.Rules)
            {
                var error = ValidateRule(attrDef, value, rule);
                if (error != null)
                {
                    result.Errors.Add(error);
                }
            }
        }

        return result;
    }

    private ValidationError? ValidateRule(in ValidationTarget attrDef, object? value, ValidationRule rule)
    {
        var stringValue = value?.ToString() ?? string.Empty;

        return rule.Type.ToLowerInvariant() switch
        {
            "maxlength" => ValidateMaxLength(attrDef, stringValue, rule),
            "minlength" => ValidateMinLength(attrDef, stringValue, rule),
            "range" => ValidateRange(attrDef, value, rule),
            "regex" => ValidateRegex(attrDef, stringValue, rule),
            "email" => ValidateEmail(attrDef, stringValue, rule),
            "url" => ValidateUrl(attrDef, stringValue, rule),
            _ => null
        };
    }

    private ValidationError? ValidateMaxLength(in ValidationTarget attrDef, string value, ValidationRule rule)
    {
        if (!TryGetIntValue(rule.Value, out var maxLength))
            return null;

        if (value.Length > maxLength)
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "maxLength",
                ErrorMessage = rule.Message ?? FormatTranslatedMessage("validation.maxLength", attrDef.Label, attrDef.Name, maxLength)
            };
        }
        return null;
    }

    private ValidationError? ValidateMinLength(in ValidationTarget attrDef, string value, ValidationRule rule)
    {
        if (!TryGetIntValue(rule.Value, out var minLength))
            return null;

        if (value.Length < minLength)
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "minLength",
                ErrorMessage = rule.Message ?? FormatTranslatedMessage("validation.minLength", attrDef.Label, attrDef.Name, minLength)
            };
        }
        return null;
    }

    private ValidationError? ValidateRange(in ValidationTarget attrDef, object? value, ValidationRule rule)
    {
        if (!TryConvertToDecimal(value, out var numericValue))
        {
            return null;
        }

        if (rule.Min.HasValue && numericValue < rule.Min.Value)
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "range",
                ErrorMessage = rule.Message ?? FormatTranslatedMessage("validation.rangeMin", attrDef.Label, attrDef.Name, rule.Min.Value)
            };
        }

        if (rule.Max.HasValue && numericValue > rule.Max.Value)
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "range",
                ErrorMessage = rule.Message ?? FormatTranslatedMessage("validation.rangeMax", attrDef.Label, attrDef.Name, rule.Max.Value)
            };
        }

        return null;
    }

    private ValidationError? ValidateRegex(in ValidationTarget attrDef, string value, ValidationRule rule)
    {
        var pattern = rule.Value?.ToString();
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        if (!Regex.IsMatch(value, pattern))
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "regex",
                ErrorMessage = rule.Message ?? FormatTranslatedMessage("validation.invalidFormat", attrDef.Label, attrDef.Name)
            };
        }
        return null;
    }

    private ValidationError? ValidateEmail(in ValidationTarget attrDef, string value, ValidationRule rule)
    {
        if (!EmailRegex().IsMatch(value))
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "email",
                ErrorMessage = rule.Message ?? FormatTranslatedMessage("validation.invalidEmail", attrDef.Label, attrDef.Name)
            };
        }
        return null;
    }

    private ValidationError? ValidateUrl(in ValidationTarget attrDef, string value, ValidationRule rule)
    {
        if (!UrlRegex().IsMatch(value))
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "url",
                ErrorMessage = rule.Message ?? FormatTranslatedMessage("validation.invalidUrl", attrDef.Label, attrDef.Name)
            };
        }
        return null;
    }

    /// <summary>
    /// Builds a TranslatedString by looking up a translation key and formatting each language
    /// with the attribute label (in that language) and any additional parameters.
    /// </summary>
    private TranslatedString FormatTranslatedMessage(string translationKey, TranslatedString? label, string attributeName, params object[] extraParams)
    {
        var templateString = translationsLoader.Resolve(translationKey);

        if (templateString is not null)
        {
            var result = new TranslatedString();
            foreach (var (language, template) in templateString.Translations)
            {
                var fieldName = label?.GetValue(language) ?? attributeName;
                var formatArgs = new object[1 + extraParams.Length];
                formatArgs[0] = fieldName;
                Array.Copy(extraParams, 0, formatArgs, 1, extraParams.Length);
                result.Translations[language] = string.Format(template, formatArgs);
            }
            return result;
        }

        // Fallback: English-only message using attribute name
        var fallbackLabel = label?.GetDefaultValue() ?? attributeName;
        return TranslatedString.Create($"{fallbackLabel}: validation failed ({translationKey})");
    }

    private static bool IsEmpty(object? value)
    {
        if (value == null) return true;
        if (value is string str) return string.IsNullOrWhiteSpace(str);
        if (value is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.Null ||
                   je.ValueKind == JsonValueKind.Undefined ||
                   (je.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(je.GetString()));
        }
        return false;
    }

    private static bool TryGetIntValue(object? value, out int result)
    {
        result = 0;
        if (value == null) return false;

        if (value is int i)
        {
            result = i;
            return true;
        }

        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out result))
                return true;
            if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out result))
                return true;
            return false;
        }

        return int.TryParse(value.ToString(), out result);
    }

    private static bool TryConvertToDecimal(object? value, out decimal result)
    {
        result = 0;
        if (value == null) return false;

        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetDecimal(out result))
                return true;
            if (je.ValueKind == JsonValueKind.String && decimal.TryParse(je.GetString(), out result))
                return true;
            return false;
        }

        return value switch
        {
            decimal d => (result = d) == d,
            double db => (result = (decimal)db) == (decimal)db,
            float f => (result = (decimal)f) == (decimal)f,
            int i => (result = i) == i,
            long l => (result = l) == l,
            _ => decimal.TryParse(value.ToString(), out result)
        };
    }
}
