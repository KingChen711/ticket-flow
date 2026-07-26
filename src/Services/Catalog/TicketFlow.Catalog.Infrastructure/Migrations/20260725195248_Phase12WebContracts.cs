using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase12WebContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "schedule_time_zone_id",
                schema: "catalog",
                table: "events",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.AddColumn<string>(
                name: "time_zone_id",
                schema: "catalog",
                table: "event_onboarding_workflows",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "UTC");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "schedule_time_zone_id",
                schema: "catalog",
                table: "events");

            migrationBuilder.DropColumn(
                name: "time_zone_id",
                schema: "catalog",
                table: "event_onboarding_workflows");
        }
    }
}
