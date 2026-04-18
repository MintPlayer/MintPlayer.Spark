using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

public class ValidationServiceTests
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly ITranslationsLoader _translations = Substitute.For<ITranslationsLoader>();

    private ValidationService CreateService()
    {
        _translations.Resolve(Arg.Any<string>()).Returns((TranslatedString?)null); // force English fallback
        return new ValidationService(_modelLoader, _translations);
    }

    private void SetupType(params EntityAttributeDefinition[] attrs)
    {
        _modelLoader.GetEntityType(PersonTypeId).Returns(new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = "Test.Person",
            Attributes = attrs
        });
    }

    private static PersistentObject Po(params (string name, object? value)[] attrs) => new()
    {
        Name = "Person",
        ObjectTypeId = PersonTypeId,
        Attributes = attrs.Select(a => new PersistentObjectAttribute { Name = a.name, Value = a.value }).ToArray()
    };

    private static EntityAttributeDefinition Attr(string name, bool isRequired = false, params ValidationRule[] rules) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        IsRequired = isRequired,
        Rules = rules
    };

    [Fact]
    public void Returns_empty_result_when_entity_type_is_unknown()
    {
        _modelLoader.GetEntityType(PersonTypeId).Returns((EntityTypeDefinition?)null);
        var service = CreateService();

        var result = service.Validate(Po());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Required_field_missing_produces_required_error()
    {
        SetupType(Attr("FirstName", isRequired: true));
        var service = CreateService();

        var result = service.Validate(Po(("FirstName", null)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.RuleType.Should().Be("required");
    }

    [Fact]
    public void Required_field_with_whitespace_is_treated_as_missing()
    {
        SetupType(Attr("FirstName", isRequired: true));
        var service = CreateService();

        var result = service.Validate(Po(("FirstName", "   ")));

        result.Errors.Should().ContainSingle().Which.RuleType.Should().Be("required");
    }

    [Fact]
    public void Required_field_present_does_not_produce_error()
    {
        SetupType(Attr("FirstName", isRequired: true));
        var service = CreateService();

        var result = service.Validate(Po(("FirstName", "John")));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Optional_empty_field_skips_further_rule_evaluation()
    {
        SetupType(Attr("Bio", rules: new ValidationRule { Type = "minlength", Value = 10 }));
        var service = CreateService();

        var result = service.Validate(Po(("Bio", null)));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void MaxLength_violation_produces_error()
    {
        SetupType(Attr("Name", rules: new ValidationRule { Type = "maxlength", Value = 5 }));
        var service = CreateService();

        var result = service.Validate(Po(("Name", "TooLong")));

        result.Errors.Should().ContainSingle().Which.RuleType.Should().Be("maxLength");
    }

    [Fact]
    public void MinLength_violation_produces_error()
    {
        SetupType(Attr("Code", rules: new ValidationRule { Type = "minlength", Value = 3 }));
        var service = CreateService();

        var result = service.Validate(Po(("Code", "a")));

        result.Errors.Should().ContainSingle().Which.RuleType.Should().Be("minLength");
    }

    [Fact]
    public void Range_rule_enforces_min_bound()
    {
        SetupType(Attr("Age", rules: new ValidationRule { Type = "range", Min = 18, Max = 99 }));
        var service = CreateService();

        var result = service.Validate(Po(("Age", 10)));

        result.Errors.Should().ContainSingle().Which.RuleType.Should().Be("range");
    }

    [Fact]
    public void Range_rule_enforces_max_bound()
    {
        SetupType(Attr("Age", rules: new ValidationRule { Type = "range", Min = 18, Max = 99 }));
        var service = CreateService();

        var result = service.Validate(Po(("Age", 150)));

        result.Errors.Should().ContainSingle().Which.RuleType.Should().Be("range");
    }

    [Fact]
    public void Range_rule_passes_inside_bounds()
    {
        SetupType(Attr("Age", rules: new ValidationRule { Type = "range", Min = 18, Max = 99 }));
        var service = CreateService();

        var result = service.Validate(Po(("Age", 42)));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Regex_rule_rejects_non_matching_value()
    {
        SetupType(Attr("Code", rules: new ValidationRule { Type = "regex", Value = @"^[A-Z]{3}$" }));
        var service = CreateService();

        var result = service.Validate(Po(("Code", "abc")));

        result.Errors.Should().ContainSingle().Which.RuleType.Should().Be("regex");
    }

    [Fact]
    public void Regex_rule_accepts_matching_value()
    {
        SetupType(Attr("Code", rules: new ValidationRule { Type = "regex", Value = @"^[A-Z]{3}$" }));
        var service = CreateService();

        var result = service.Validate(Po(("Code", "ABC")));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Email_rule_rejects_invalid_format()
    {
        SetupType(Attr("Email", rules: new ValidationRule { Type = "email" }));
        var service = CreateService();

        var result = service.Validate(Po(("Email", "not-an-email")));

        result.Errors.Should().ContainSingle().Which.RuleType.Should().Be("email");
    }

    [Fact]
    public void Email_rule_accepts_valid_format()
    {
        SetupType(Attr("Email", rules: new ValidationRule { Type = "email" }));
        var service = CreateService();

        var result = service.Validate(Po(("Email", "user@example.com")));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Url_rule_rejects_non_http_value()
    {
        SetupType(Attr("Website", rules: new ValidationRule { Type = "url" }));
        var service = CreateService();

        var result = service.Validate(Po(("Website", "example.com")));

        result.Errors.Should().ContainSingle().Which.RuleType.Should().Be("url");
    }

    [Fact]
    public void Url_rule_accepts_https_value()
    {
        SetupType(Attr("Website", rules: new ValidationRule { Type = "url" }));
        var service = CreateService();

        var result = service.Validate(Po(("Website", "https://example.com")));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Multiple_rule_violations_are_all_reported()
    {
        SetupType(
            Attr("Email", isRequired: true, new ValidationRule { Type = "email" }),
            Attr("Age", rules: new ValidationRule { Type = "range", Min = 18, Max = 99 }));
        var service = CreateService();

        var result = service.Validate(Po(("Email", "bad"), ("Age", 200)));

        result.Errors.Should().HaveCount(2);
        result.Errors.Select(e => e.RuleType).Should().BeEquivalentTo(["email", "range"]);
    }

    [Fact]
    public void Unknown_rule_type_is_ignored()
    {
        SetupType(Attr("Field", rules: new ValidationRule { Type = "notARealRuleType", Value = "x" }));
        var service = CreateService();

        var result = service.Validate(Po(("Field", "anything")));

        result.IsValid.Should().BeTrue();
    }
}
