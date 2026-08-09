using FluentAssertions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Services;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// The correctness bar here is not "produces a valid name" but "producer and consumer
/// produce the SAME name" — MessageBus and MessageSubscriptionManager derive independently.
/// </summary>
public class QueueNamesTests
{
    private class PlainMessage { }

    private class GenericMessage<TPayload> { }

    private class TwoArgMessage<TFirst, TSecond> { }

    private class PayloadA { }

    private class PayloadB { }

    [MessageQueue("explicit-queue-name")]
    private class AttributedMessage { }

    [MessageQueue("explicit-generic")]
    private class AttributedGenericMessage<TPayload> { }

    private class Outer
    {
        public class Nested { }
    }

    [Fact]
    public void Non_generic_type_keeps_its_FullName_unchanged()
    {
        // Guards the existing MessageBusTests inferred-queue-name expectation: the
        // non-generic case is the recursion's base case and must not drift.
        QueueNames.ForMessageType(typeof(PlainMessage))
            .Should().Be(typeof(PlainMessage).FullName);
    }

    [Fact]
    public void Nested_non_generic_type_keeps_its_plus_separator()
    {
        var name = QueueNames.ForMessageType(typeof(Outer.Nested));

        name.Should().Be(typeof(Outer.Nested).FullName);
        name.Should().Contain("+");
        QueueNames.IsValid(name).Should().BeTrue();
    }

    [Fact]
    public void Closed_generic_type_yields_a_valid_queue_name()
    {
        // The bug: FullName of a constructed generic embeds assembly-qualified arguments,
        // so it carries '[', ']', ',', '=' and spaces and fails validation outright.
        typeof(GenericMessage<PayloadA>).FullName.Should().Contain("[[");
        QueueNames.IsValid(typeof(GenericMessage<PayloadA>).FullName!).Should().BeFalse();

        var name = QueueNames.ForMessageType(typeof(GenericMessage<PayloadA>));

        QueueNames.IsValid(name).Should().BeTrue();
        name.Should().NotContainAny("[", "]", ",", "=", " ");
    }

    [Fact]
    public void Closed_generic_name_retains_the_definition_and_argument_identities()
    {
        var name = QueueNames.ForMessageType(typeof(GenericMessage<PayloadA>));

        name.Should().StartWith(typeof(GenericMessage<>).FullName);
        name.Should().Contain(nameof(PayloadA));
    }

    [Fact]
    public void Generic_arguments_are_joined_without_a_comma()
    {
        var name = QueueNames.ForMessageType(typeof(TwoArgMessage<PayloadA, PayloadB>));

        // ',' is one of the characters IsValid rejects — the separator must be '-'.
        name.Should().NotContain(",");
        name.Should().Contain(nameof(PayloadA)).And.Contain(nameof(PayloadB));
        QueueNames.IsValid(name).Should().BeTrue();
    }

    [Fact]
    public void Nested_generic_arguments_stay_valid_and_distinct()
    {
        // The case that rules out deriving arguments by simple name: Foo<Bar<Baz>> would
        // otherwise collide with, or mis-derive against, Foo<Baz>.
        var nested = QueueNames.ForMessageType(typeof(GenericMessage<GenericMessage<PayloadA>>));
        var flat = QueueNames.ForMessageType(typeof(GenericMessage<PayloadA>));

        QueueNames.IsValid(nested).Should().BeTrue();
        nested.Should().NotBe(flat);
    }

    [Fact]
    public void Different_generic_arguments_yield_different_queues()
    {
        QueueNames.ForMessageType(typeof(GenericMessage<PayloadA>))
            .Should().NotBe(QueueNames.ForMessageType(typeof(GenericMessage<PayloadB>)));
    }

    [Fact]
    public void MessageQueue_attribute_wins_over_derivation()
    {
        QueueNames.ForMessageType(typeof(AttributedMessage)).Should().Be("explicit-queue-name");
        QueueNames.ForMessageType(typeof(AttributedGenericMessage<PayloadA>)).Should().Be("explicit-generic");
    }

    [Fact]
    public void Derivation_is_deterministic()
    {
        QueueNames.ForMessageType(typeof(GenericMessage<PayloadA>))
            .Should().Be(QueueNames.ForMessageType(typeof(GenericMessage<PayloadA>)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has'quote")]
    [InlineData("has,comma")]
    [InlineData("has[bracket]")]
    [InlineData("has=equals")]
    public void IsValid_rejects_names_that_could_escape_the_RQL_literal(string value)
        => QueueNames.IsValid(value).Should().BeFalse();

    [Theory]
    [InlineData("spark-github-all")]
    [InlineData("Ns.Outer+Inner")]
    [InlineData("Ns.Message`1-Ns.Arg")]
    public void IsValid_accepts_the_shapes_derivation_produces(string value)
        => QueueNames.IsValid(value).Should().BeTrue();
}
