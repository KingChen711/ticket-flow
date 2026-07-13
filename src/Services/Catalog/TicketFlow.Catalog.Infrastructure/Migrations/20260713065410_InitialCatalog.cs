using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "events",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    artist = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    schedule_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "seat_maps",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seat_maps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "venues",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address_street = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    address_city = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    address_country = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    address_postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_venues", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "event_price_tiers",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_price_tiers", x => x.id);
                    table.ForeignKey(
                        name: "fk_event_price_tiers_events_event_id",
                        column: x => x.event_id,
                        principalSchema: "catalog",
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seat_map_sections",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    seat_map_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seat_map_sections", x => x.id);
                    table.ForeignKey(
                        name: "fk_seat_map_sections_seat_maps_seat_map_id",
                        column: x => x.seat_map_id,
                        principalSchema: "catalog",
                        principalTable: "seat_maps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_event_price_tiers_event_id",
                schema: "catalog",
                table: "event_price_tiers",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_seat_map_sections_seat_map_id",
                schema: "catalog",
                table: "seat_map_sections",
                column: "seat_map_id");

            migrationBuilder.CreateIndex(
                name: "ix_seat_maps_event_id",
                schema: "catalog",
                table: "seat_maps",
                column: "event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_price_tiers",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "seat_map_sections",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "venues",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "events",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "seat_maps",
                schema: "catalog");
        }
    }
}
