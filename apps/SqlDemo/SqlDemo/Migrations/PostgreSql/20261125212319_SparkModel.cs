using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlDemo.Migrations.PostgreSql;

/// <inheritdoc />
public partial class _20261125212319_SparkModel : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AgentGrants",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false, collation: "C"),
                UserId = table.Column<string>(type: "text", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                ClientId = table.Column<string>(type: "text", nullable: true),
                Name = table.Column<string>(type: "text", nullable: true),
                VerifiedHost = table.Column<string>(type: "text", nullable: true),
                Permissions = table.Column<int>(type: "integer", nullable: false),
                Types = table.Column<string>(type: "jsonb", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                TokenHash = table.Column<string>(type: "text", nullable: true),
                SecurityStamp = table.Column<string>(type: "text", nullable: true),
                OidcGrantId = table.Column<string>(type: "text", nullable: true),
                Issuer = table.Column<string>(type: "text", nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                SparkChangeStamp = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                SparkExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentGrants", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Authors",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Country = table.Column<string>(type: "text", nullable: true),
                SparkChangeStamp = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                SparkExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Authors", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SparkMailDeliveries",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false, collation: "C"),
                Email = table.Column<string>(type: "text", nullable: false),
                Template = table.Column<string>(type: "text", nullable: false),
                Stream = table.Column<string>(type: "text", nullable: false),
                CampaignId = table.Column<string>(type: "text", nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                Detail = table.Column<string>(type: "text", nullable: true),
                ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                SparkChangeStamp = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                SparkExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SparkMailDeliveries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SparkMailSuppressions",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false, collation: "C"),
                All = table.Column<bool>(type: "boolean", nullable: false),
                Streams = table.Column<List<string>>(type: "text[]", nullable: false),
                Reason = table.Column<int>(type: "integer", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                SparkChangeStamp = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                SparkExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SparkMailSuppressions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SparkMessageProgresses",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false, collation: "C"),
                MessageId = table.Column<string>(type: "text", nullable: false),
                HandlerIndex = table.Column<int>(type: "integer", nullable: false),
                Steps = table.Column<List<string>>(type: "text[]", nullable: false),
                SparkChangeStamp = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                SparkExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SparkMessageProgresses", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SparkMessages",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false, collation: "C"),
                QueueName = table.Column<string>(type: "text", nullable: false),
                MessageType = table.Column<string>(type: "text", nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                Priority = table.Column<int>(type: "integer", nullable: false),
                Expedite = table.Column<bool>(type: "boolean", nullable: false),
                ReclaimCount = table.Column<int>(type: "integer", nullable: false),
                RateLimitedUntilUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                QueuedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                DeadLetterReason = table.Column<int>(type: "integer", nullable: true),
                ScrubPayloadOnTerminal = table.Column<bool>(type: "boolean", nullable: false),
                Handlers = table.Column<string>(type: "jsonb", nullable: false),
                WakeUp = table.Column<bool>(type: "boolean", nullable: false),
                LastWakeUpUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                OwnerId = table.Column<string>(type: "text", nullable: true),
                ClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                SparkChangeStamp = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                SparkExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SparkMessages", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SparkPendingExternalLogins",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false, collation: "C"),
                TokenHash = table.Column<string>(type: "text", nullable: false),
                UserId = table.Column<string>(type: "text", nullable: false),
                LoginProvider = table.Column<string>(type: "text", nullable: false),
                ProviderKey = table.Column<string>(type: "text", nullable: false),
                ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                SparkChangeStamp = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                SparkExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SparkPendingExternalLogins", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SparkRoles",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false, collation: "C"),
                Name = table.Column<string>(type: "text", nullable: true),
                NormalizedName = table.Column<string>(type: "text", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                Claims = table.Column<string>(type: "jsonb", nullable: false),
                SparkChangeStamp = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                SparkExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SparkRoles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SparkUsers",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false, collation: "C"),
                UserName = table.Column<string>(type: "text", nullable: true),
                NormalizedUserName = table.Column<string>(type: "text", nullable: true),
                Email = table.Column<string>(type: "text", nullable: true),
                NormalizedEmail = table.Column<string>(type: "text", nullable: true),
                EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                PasswordHash = table.Column<string>(type: "text", nullable: true),
                SecurityStamp = table.Column<string>(type: "text", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                PhoneNumber = table.Column<string>(type: "text", nullable: true),
                PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                AccessFailedCount = table.Column<int>(type: "integer", nullable: false),
                AuthenticatorKey = table.Column<string>(type: "text", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                RegistrationMethod = table.Column<string>(type: "text", nullable: true),
                PreferredCulture = table.Column<string>(type: "text", nullable: true),
                BypassTwoFactorForExternalLogin = table.Column<bool>(type: "boolean", nullable: false),
                Roles = table.Column<List<string>>(type: "text[]", nullable: false),
                Claims = table.Column<string>(type: "jsonb", nullable: false),
                Logins = table.Column<string>(type: "jsonb", nullable: false),
                TwoFactorRecoveryCodes = table.Column<List<string>>(type: "text[]", nullable: false),
                Tokens = table.Column<string>(type: "jsonb", nullable: false),
                Passkeys = table.Column<string>(type: "jsonb", nullable: false),
                SparkChangeStamp = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                SparkExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SparkUsers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Books",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                Author = table.Column<Guid>(type: "uuid", nullable: true),
                Year = table.Column<int>(type: "integer", nullable: false),
                Isbn = table.Column<string>(type: "text", nullable: true),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                SparkChangeStamp = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                SparkExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Books", x => x.Id);
                table.ForeignKey(
                    name: "FK_Books_Authors_Author",
                    column: x => x.Author,
                    principalTable: "Authors",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AgentGrants_SparkChangeStamp",
            table: "AgentGrants",
            column: "SparkChangeStamp");

        migrationBuilder.CreateIndex(
            name: "IX_AgentGrants_SparkExpiresAt",
            table: "AgentGrants",
            column: "SparkExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_Authors_SparkChangeStamp",
            table: "Authors",
            column: "SparkChangeStamp");

        migrationBuilder.CreateIndex(
            name: "IX_Authors_SparkExpiresAt",
            table: "Authors",
            column: "SparkExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_Books_Author",
            table: "Books",
            column: "Author");

        migrationBuilder.CreateIndex(
            name: "IX_Books_SparkChangeStamp",
            table: "Books",
            column: "SparkChangeStamp");

        migrationBuilder.CreateIndex(
            name: "IX_Books_SparkExpiresAt",
            table: "Books",
            column: "SparkExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_SparkMailDeliveries_SparkChangeStamp",
            table: "SparkMailDeliveries",
            column: "SparkChangeStamp");

        migrationBuilder.CreateIndex(
            name: "IX_SparkMailDeliveries_SparkExpiresAt",
            table: "SparkMailDeliveries",
            column: "SparkExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_SparkMailSuppressions_SparkChangeStamp",
            table: "SparkMailSuppressions",
            column: "SparkChangeStamp");

        migrationBuilder.CreateIndex(
            name: "IX_SparkMailSuppressions_SparkExpiresAt",
            table: "SparkMailSuppressions",
            column: "SparkExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_SparkMessageProgresses_SparkChangeStamp",
            table: "SparkMessageProgresses",
            column: "SparkChangeStamp");

        migrationBuilder.CreateIndex(
            name: "IX_SparkMessageProgresses_SparkExpiresAt",
            table: "SparkMessageProgresses",
            column: "SparkExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_SparkMessages_SparkChangeStamp",
            table: "SparkMessages",
            column: "SparkChangeStamp");

        migrationBuilder.CreateIndex(
            name: "IX_SparkMessages_SparkExpiresAt",
            table: "SparkMessages",
            column: "SparkExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_SparkPendingExternalLogins_SparkChangeStamp",
            table: "SparkPendingExternalLogins",
            column: "SparkChangeStamp");

        migrationBuilder.CreateIndex(
            name: "IX_SparkPendingExternalLogins_SparkExpiresAt",
            table: "SparkPendingExternalLogins",
            column: "SparkExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_SparkRoles_SparkChangeStamp",
            table: "SparkRoles",
            column: "SparkChangeStamp");

        migrationBuilder.CreateIndex(
            name: "IX_SparkRoles_SparkExpiresAt",
            table: "SparkRoles",
            column: "SparkExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_SparkUsers_SparkChangeStamp",
            table: "SparkUsers",
            column: "SparkChangeStamp");

        migrationBuilder.CreateIndex(
            name: "IX_SparkUsers_SparkExpiresAt",
            table: "SparkUsers",
            column: "SparkExpiresAt");

        migrationBuilder.Sql("CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON \"AgentGrants\" FOR EACH ROW EXECUTE FUNCTION spark.set_change_stamp();");

        migrationBuilder.Sql("CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON \"Authors\" FOR EACH ROW EXECUTE FUNCTION spark.set_change_stamp();");

        migrationBuilder.Sql("CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON \"SparkMailDeliveries\" FOR EACH ROW EXECUTE FUNCTION spark.set_change_stamp();");

        migrationBuilder.Sql("CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON \"SparkMailSuppressions\" FOR EACH ROW EXECUTE FUNCTION spark.set_change_stamp();");

        migrationBuilder.Sql("CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON \"SparkMessageProgresses\" FOR EACH ROW EXECUTE FUNCTION spark.set_change_stamp();");

        migrationBuilder.Sql("CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON \"SparkMessages\" FOR EACH ROW EXECUTE FUNCTION spark.set_change_stamp();");

        migrationBuilder.Sql("CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON \"SparkPendingExternalLogins\" FOR EACH ROW EXECUTE FUNCTION spark.set_change_stamp();");

        migrationBuilder.Sql("CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON \"SparkRoles\" FOR EACH ROW EXECUTE FUNCTION spark.set_change_stamp();");

        migrationBuilder.Sql("CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON \"SparkUsers\" FOR EACH ROW EXECUTE FUNCTION spark.set_change_stamp();");

        migrationBuilder.Sql("CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON \"Books\" FOR EACH ROW EXECUTE FUNCTION spark.set_change_stamp();");

        migrationBuilder.Sql("CREATE VIEW \"Books_WithAuthor\" AS\nSELECT b.\"Id\", b.\"Title\", b.\"Author\", (\n    SELECT a.\"Name\"\n    FROM \"Authors\" AS a\n    WHERE a.\"Id\" = b.\"Author\"\n    LIMIT 1) AS \"AuthorName\", b.\"Year\", b.\"Isbn\"\nFROM \"Books\" AS b;");

        migrationBuilder.Sql("CREATE VIEW \"SparkMessages_ByState\" AS\nSELECT s.\"Id\", s.\"QueueName\", s.\"Status\", s.\"Priority\", s.\"Expedite\", s.\"QueuedAtUtc\", s.\"CreatedAtUtc\", s.\"NextAttemptAtUtc\", s.\"WakeUp\", s.\"ClaimExpiresAtUtc\"\nFROM \"SparkMessages\" AS s;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP VIEW \"Books_WithAuthor\";");

        migrationBuilder.Sql("DROP VIEW \"SparkMessages_ByState\";");

        migrationBuilder.DropTable(
            name: "AgentGrants");

        migrationBuilder.DropTable(
            name: "Books");

        migrationBuilder.DropTable(
            name: "SparkMailDeliveries");

        migrationBuilder.DropTable(
            name: "SparkMailSuppressions");

        migrationBuilder.DropTable(
            name: "SparkMessageProgresses");

        migrationBuilder.DropTable(
            name: "SparkMessages");

        migrationBuilder.DropTable(
            name: "SparkPendingExternalLogins");

        migrationBuilder.DropTable(
            name: "SparkRoles");

        migrationBuilder.DropTable(
            name: "SparkUsers");

        migrationBuilder.DropTable(
            name: "Authors");
    }
}
