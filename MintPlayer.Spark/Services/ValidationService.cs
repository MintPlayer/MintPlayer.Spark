using System.Text.Json;
using System.Text.RegularExpressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface IValidationService
{
    ValidationResult Validate(PersistentObject persistentObject);
}

[Register(typeof(IValidationService), ServiceLifetime.Scoped, "AddSparkServices")]
internal partial class ValidationService : IValidationService
{
    [Inject] private readonly IModelLoader modelLoader;

    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UrlRegex = new(
        @"^https?:\/\/[^\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ValidationResult Validate(PersistentObject persistentObject)
    {
        var result = new ValidationResult();

        var entityType = modelLoader.GetEntityType(persistentObject.ObjectTypeId);
        if (entityType == null)
        {
            return result;
        }

        foreach (var attrDef in entityType.Attributes)
        {
            var attribute = persistentObject.Attributes.FirstOrDefault(a => a.Name == attrDef.Name);
            var value = attribute?.Value;

            // Check required
            if (attrDef.IsRequired && IsEmpty(value))
            {
                result.Errors.Add(new ValidationError
                {
                    AttributeName = attrDef.Name,
                    RuleType = "required",
                    ErrorMessage = $"{attrDef.Label ?? attrDef.Name} is required."
                });
                continue; // Skip other validations if required field is empty
            }

            // Skip validation rules if value is empty (and not required)
            if (IsEmpty(value))
            {
                continue;
            }

            // Apply validation rules
            foreach (var rule in attrDef.Rules ?? [])
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

    private ValidationError? ValidateRule(EntityAttributeDefinition attrDef, object? value, ValidationRule rule)
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

    private ValidationError? ValidateMaxLength(EntityAttributeDefinition attrDef, string value, ValidationRule rule)
    {
        if (!TryGetIntValue(rule.Value, out var maxLength))
            return null;

        if (value.Length > maxLength)
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "maxLength",
                ErrorMessage = rule.Message ?? $"{attrDef.Label ?? attrDef.Name} must be at most {maxLength} characters."
            };
        }
        return null;
    }

    private ValidationError? ValidateMinLength(EntityAttributeDefinition attrDef, string value, ValidationRule rule)
    {
        if (!TryGetIntValue(rule.Value, out var minLength))
            return null;

        if (value.Length < minLength)
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "minLength",
                ErrorMessage = rule.Message ?? $"{attrDef.Label ?? attrDef.Name} must be at least {minLength} characters."
            };
        }
        return null;
    }

    private ValidationError? ValidateRange(EntityAttributeDefinition attrDef, object? value, ValidationRule rule)
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
                ErrorMessage = rule.Message ?? $"{attrDef.Label ?? attrDef.Name} must be at least {rule.Min}."
            };
        }

        if (rule.Max.HasValue && numericValue > rule.Max.Value)
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "range",
                ErrorMessage = rule.Message ?? $"{attrDef.Label ?? attrDef.Name} must be at most {rule.Max}."
            };
        }

        return null;
    }

    private ValidationError? ValidateRegex(EntityAttributeDefinition attrDef, string value, ValidationRule rule)
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
                ErrorMessage = rule.Message ?? $"{attrDef.Label ?? attrDef.Name} has an invalid format."
            };
        }
        return null;
    }

    private ValidationError? ValidateEmail(EntityAttributeDefinition attrDef, string value, ValidationRule rule)
    {
        if (!EmailRegex.IsMatch(value))
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "email",
                ErrorMessage = rule.Message ?? $"{attrDef.Label ?? attrDef.Name} must be a valid email address."
            };
        }
        return null;
    }

    private ValidationError? ValidateUrl(EntityAttributeDefinition attrDef, string value, ValidationRule rule)
    {
        if (!UrlRegex.IsMatch(value))
        {
            return new ValidationError
            {
                AttributeName = attrDef.Name,
                RuleType = "url",
                ErrorMessage = rule.Message ?? $"{attrDef.Label ?? attrDef.Name} must be a valid URL."
            };
        }
        return null;
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
