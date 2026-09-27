using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The value and rule-argument shapes <see cref="ValidationService"/> accepts: a wire
/// <see cref="JsonElement"/>, a CLR number a refresh hook put there, or a string. The rule texts
/// themselves are pinned in <see cref="ValidationServiceTests"/>.
/// </summary>
public class ValidationServiceValueShapeTests
{
    private static readonly Guid TypeId = Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222");

    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();

    private ValidationService Service()
    {
        var translations = Substitute.For<ITranslationsLoader>();
        translations.Resolve(Arg.Any<string>()).Returns((TranslatedString?)null);
        return new ValidationService(_modelLoader, translations);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private ValidationResult Validate(object? value, bool isRequired = false, params ValidationRule[] rules)
    {
        _modelLoader.GetEntityType(TypeId).Returns(new EntityTypeDefinition
        {
            Id = TypeId,
            Name = "Probe",
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Field", IsRequired = isRequired, Rules = rules }],
        });

        return Service().Validate(new PersistentObject
        {
            Name = "Probe",
            ObjectTypeId = TypeId,
            Attributes = [new PersistentObjectAttribute { Name = "Field", Value = value }],
        });
    }

    private static ValidationRule Range(int? min = 0, int? max = 10) => new() { Type = "range", Min = min, Max = max };

    // --- range: every numeric shape -------------------------------------------

    public static TheoryData<object, bool> RangeValues() => new()
    {
        { Json("5"), true },
        { Json("11"), false },
        { Json("\"5\""), true },
        { Json("\"-1\""), false },
        { 5.5d, true },
        { 10.5d, false },
        { 2.5f, true },
        { -0.5f, false },
        { 7, true },
        { 70L, false },
        { 3m, true },
        { "9", true },
        { "12", false },
    };

    [Theory]
    [MemberData(nameof(RangeValues))]
    public void Range_is_judged_on_every_numeric_shape(object value, bool valid)
    {
        Validate(value, rules: Range()).IsValid.Should().Be(valid);
    }

    [Theory]
    [InlineData("\"ten\"")]
    [InlineData("true")]
    [InlineData("[1]")]
    public void Range_skips_a_wire_value_that_is_not_a_number(string json)
    {
        Validate(Json(json), rules: Range()).IsValid.Should().BeTrue("that is a type problem, not a range one");
    }

    // --- range: values with no decimal form -----------------------------------

    [Theory]
    [InlineData(double.NaN, "range")]
    [InlineData(double.PositiveInfinity, "range")]
    [InlineData(double.NegativeInfinity, "range")]
    [InlineData(1e30, "range")]
    [InlineData(-1e30, "range")]
    public void A_double_with_no_decimal_form_is_out_of_range_rather_than_a_500(double value, string ruleType)
    {
        // (decimal)NaN throws OverflowException, which used to escape as a 500 for the whole save.
        var result = Validate(value, rules: Range());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.RuleType.Should().Be(ruleType);
    }

    [Fact]
    public void A_float_infinity_is_judged_too()
    {
        Validate(float.PositiveInfinity, rules: Range()).IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_infinity_passes_on_its_unbounded_side()
    {
        Validate(double.PositiveInfinity, rules: Range(min: 0, max: null)).IsValid.Should().BeTrue();
        Validate(double.NegativeInfinity, rules: Range(min: null, max: 0)).IsValid.Should().BeTrue();
        Validate(double.NaN, rules: Range(min: null, max: null)).IsValid.Should().BeTrue("there is no bound to judge");
    }

    [Fact]
    public void NaN_reports_the_lower_bound_first()
    {
        var error = Validate(double.NaN, rules: Range(min: 1, max: 5)).Errors.Single();

        error.ErrorMessage.GetValue("en").Should().Contain("validation.rangeMin");
    }

    // --- length rules: rule argument shapes -----------------------------------

    public static TheoryData<object?, bool> MaxLengthArguments() => new()
    {
        { 3, false },
        { Json("3"), false },
        { Json("\"3\""), false },
        { "3", false },
        { Json("\"three\""), true },
        { Json("3.5"), true },
        { null, true },
    };

    [Theory]
    [MemberData(nameof(MaxLengthArguments))]
    public void MaxLength_reads_its_limit_from_every_argument_shape(object? limit, bool valid)
    {
        // An unreadable limit skips the rule rather than refusing every value.
        Validate("four", rules: new ValidationRule { Type = "maxLength", Value = limit }).IsValid.Should().Be(valid);
    }

    // --- custom messages ------------------------------------------------------

    public static TheoryData<ValidationRule, object> RulesWithMessages() => new()
    {
        { new ValidationRule { Type = "maxLength", Value = 1 }, "long" },
        { new ValidationRule { Type = "minLength", Value = 9 }, "short" },
        { new ValidationRule { Type = "range", Min = 5 }, 1 },
        { new ValidationRule { Type = "range", Max = 5 }, 9 },
        { new ValidationRule { Type = "regex", Value = "^[0-9]+$" }, "abc" },
        { new ValidationRule { Type = "email" }, "nobody" },
        { new ValidationRule { Type = "url" }, "ftp://x" },
    };

    [Theory]
    [MemberData(nameof(RulesWithMessages))]
    public void A_rule_message_replaces_the_generated_one(ValidationRule rule, object value)
    {
        rule.Message = TranslatedString.Create("Custom text");

        var error = Validate(value, rules: rule).Errors.Single();

        error.ErrorMessage.GetValue("en").Should().Be("Custom text");
    }

    [Fact]
    public void Rule_types_are_matched_case_insensitively()
    {
        Validate("abcdef", rules: new ValidationRule { Type = "MAXLENGTH", Value = 2 }).IsValid.Should().BeFalse();
    }

    // --- emptiness ------------------------------------------------------------

    [Theory]
    [InlineData("null")]
    [InlineData("\"   \"")]
    public void A_blank_wire_value_fails_required(string json)
    {
        Validate(Json(json), isRequired: true).Errors.Should().ContainSingle().Which.RuleType.Should().Be("required");
    }

    [Fact]
    public void A_wire_number_satisfies_required()
    {
        Validate(Json("0"), isRequired: true).IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_absent_attribute_is_validated_as_empty()
    {
        _modelLoader.GetEntityType(TypeId).Returns(new EntityTypeDefinition
        {
            Id = TypeId,
            Name = "Probe",
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Missing", IsRequired = true }],
        });

        var result = Service().Validate(new PersistentObject { Name = "Probe", ObjectTypeId = TypeId, Attributes = [] });

        result.Errors.Should().ContainSingle().Which.AttributeName.Should().Be("Missing");
    }

    // --- the effective object -------------------------------------------------

    [Fact]
    public void ValidateEffective_uses_the_rules_the_object_carries()
    {
        // A refresh hook made the field required and bounded; the model says neither.
        var effective = new PersistentObject
        {
            Name = "Probe",
            ObjectTypeId = TypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Blank", Value = null, IsRequired = true },
                new PersistentObjectAttribute { Name = "Big", Value = double.PositiveInfinity, Rules = [Range()] },
                new PersistentObjectAttribute { Name = "Fine", Value = 3 },
            ],
        };

        var result = Service().ValidateEffective(effective);

        result.Errors.Select(e => (e.AttributeName, e.RuleType)).Should().BeEquivalentTo(new[]
        {
            ("Blank", "required"),
            ("Big", "range"),
        });
    }
}
