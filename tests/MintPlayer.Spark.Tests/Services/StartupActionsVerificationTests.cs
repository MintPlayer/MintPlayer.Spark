using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services
{
    /// <summary>
    /// Commerce F10 and F11: two ways an app could ship a broken actions setup and only find out on a
    /// request.
    /// <list type="bullet">
    /// <item>F10: two classes named <c>{Entity}Actions</c>. <c>ActionsResolver</c> refuses the ambiguity, but
    /// only when it first resolves the type; the startup check that resolves actions classes
    /// (<c>VerifyRowPolicyDeclarations</c>) swallows the failure, so the app started and the first request
    /// for that type answered 500.</item>
    /// <item>F11: two custom action classes serving the same action name. Discovery kept the first one loaded
    /// and dropped the other without a word, so which class ran depended on assembly load order.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The F11 fixtures are plain classes, not <c>ICustomAction</c>s: discovery scans every loaded assembly
    /// once per process, so a real duplicate in this assembly would stop every other test host from starting.
    /// The test drives the indexing step discovery uses.
    /// </remarks>
    public class StartupActionsVerificationTests : SparkTestDriver
    {
        private static readonly Guid DupTypeId = Guid.Parse("f1000000-0000-4000-8000-000000000001");
        private static readonly Guid SoloTypeId = Guid.Parse("f1000000-0000-4000-8000-000000000002");

        [Fact]
        public void An_entity_with_two_actions_classes_fails_at_startup()
        {
            var act = () => new SparkEndpointFactory<F10Context>(Store, [Model(DupTypeId, "F10DupEntity", typeof(F10DupEntity))]);

            act.Should().Throw<InvalidOperationException>()
                .Which.Message.Should().Contain("F10DupEntityActions");
        }

        [Fact]
        public async Task An_entity_with_one_actions_class_still_starts()
        {
            // Throws out of the constructor if the startup check misfires on a lone actions class.
            await using var factory = new SparkEndpointFactory<F10Context>(Store, [Model(SoloTypeId, "F10SoloEntity", typeof(F10SoloEntity))]);
        }

        [Fact]
        public void Two_custom_actions_with_one_name_are_refused()
        {
            var act = () => CustomActionResolver.IndexByName([typeof(First.F11DupAction), typeof(Second.F11DupAction)]);

            var message = act.Should().Throw<InvalidOperationException>().Which.Message;
            message.Should().Contain("First.F11DupAction");
            message.Should().Contain("Second.F11DupAction");
        }

        [Fact]
        public void The_Action_suffix_does_not_make_two_names_distinct()
        {
            // "F11Dup" and "F11DupAction" both serve the action "F11Dup".
            var act = () => CustomActionResolver.IndexByName([typeof(First.F11DupAction), typeof(Second.F11Dup)]);

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Distinct_custom_action_names_are_indexed()
        {
            var index = CustomActionResolver.IndexByName([typeof(First.F11DupAction), typeof(Second.F11OtherAction)]);

            index.Keys.OrderBy(k => k).ToArray().Should().Equal("F11Dup", "F11Other");
        }

        private static EntityTypeFile Model(Guid id, string name, Type clr) => new()
        {
            PersistentObject = new EntityTypeDefinition
            {
                Id = id,
                Name = name,
                ClrType = clr.FullName!,
                Breadcrumb = "{Name}",
                Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
            },
        };
    }

    public class F10DupEntity
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class F10SoloEntity
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class F10SoloEntityActions(global::MintPlayer.Spark.Services.IEntityMapper entityMapper) : global::MintPlayer.Spark.Actions.DefaultPersistentObjectActions<F10SoloEntity>(entityMapper);

    public class F10Context : SparkContext
    {
        public IRavenQueryable<F10DupEntity> Dups => Session.Query<F10DupEntity>();
        public IRavenQueryable<F10SoloEntity> Solos => Session.Query<F10SoloEntity>();
    }
}

namespace MintPlayer.Spark.Tests.Services.F10First
{
    public class F10DupEntityActions(global::MintPlayer.Spark.Services.IEntityMapper entityMapper) : global::MintPlayer.Spark.Actions.DefaultPersistentObjectActions<F10DupEntity>(entityMapper);
}

namespace MintPlayer.Spark.Tests.Services.F10Second
{
    public class F10DupEntityActions(global::MintPlayer.Spark.Services.IEntityMapper entityMapper) : global::MintPlayer.Spark.Actions.DefaultPersistentObjectActions<F10DupEntity>(entityMapper);
}

namespace MintPlayer.Spark.Tests.Services.First
{
    public class F11DupAction;
}

namespace MintPlayer.Spark.Tests.Services.Second
{
    public class F11DupAction;
    public class F11Dup;
    public class F11OtherAction;
}
