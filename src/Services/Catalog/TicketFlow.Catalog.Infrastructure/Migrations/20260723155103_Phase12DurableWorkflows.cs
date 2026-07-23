using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TicketFlow.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12DurableWorkflows : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "event_cancellation_workflows",
            schema: "catalog",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                order_count = table.Column<int>(type: "integer", nullable: true),
                requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_event_cancellation_workflows", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "event_onboarding_workflows",
            schema: "catalog",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                creation_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                creates_venue = table.Column<bool>(type: "boolean", nullable: false),
                venue_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                venue_street = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                venue_city = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                venue_country = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                venue_postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                seat_map_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                event_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                event_artist = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                last_completed_step = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                failed_step = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_event_onboarding_workflows", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "event_onboarding_sections",
            schema: "catalog",
            columns: table => new
            {
                position = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                section_id = table.Column<Guid>(type: "uuid", nullable: false),
                price_tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                capacity = table.Column<int>(type: "integer", nullable: false),
                price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_event_onboarding_sections", x => new { x.workflow_id, x.position });
                table.ForeignKey(
                    name: "fk_event_onboarding_sections_event_onboarding_workflows_workfl",
                    column: x => x.workflow_id,
                    principalSchema: "catalog",
                    principalTable: "event_onboarding_workflows",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_event_cancellation_workflows_event_id",
            schema: "catalog",
            table: "event_cancellation_workflows",
            column: "event_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_event_onboarding_sections_price_tier_id",
            schema: "catalog",
            table: "event_onboarding_sections",
            column: "price_tier_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_event_onboarding_sections_section_id",
            schema: "catalog",
            table: "event_onboarding_sections",
            column: "section_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_event_onboarding_workflows_creation_key",
            schema: "catalog",
            table: "event_onboarding_workflows",
            column: "creation_key",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "event_cancellation_workflows",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "event_onboarding_sections",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "event_onboarding_workflows",
            schema: "catalog");
    }
}
