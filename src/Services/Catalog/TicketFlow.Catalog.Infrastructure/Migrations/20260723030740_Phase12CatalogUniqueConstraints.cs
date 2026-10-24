using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Catalog.Infrastructure.Migrations;

/// <summary>
/// Unlike Commit 6's payment migration, this one genuinely tightens: <c>seat_map_sections</c>
/// and <c>event_price_tiers</c> are not new expand-only tables, so pre-existing duplicate rows
/// (a duplicate section name, or two price tiers for the same section) would make this
/// migration fail rather than silently apply. No backfill is attempted — the domain's own
/// in-memory guards (Event.AddPriceTier, SeatMap.AddSection) already reject duplicates on
/// every write path this project has, so duplicate data was never actually reachable; this
/// only closes the race between two concurrent writes past those same guards.
///
/// The plain (case-sensitive) index above catches an exact-name race; it does not fully match
/// SeatMap.AddSection's OrdinalIgnoreCase comparison. EF's fluent API has no functional-index
/// support, so the case-insensitive backstop is the raw SQL below.
/// </summary>
public partial class Phase12CatalogUniqueConstraints : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_seat_map_sections_seat_map_id",
            schema: "catalog",
            table: "seat_map_sections");

        migrationBuilder.DropIndex(
            name: "ix_event_price_tiers_event_id",
            schema: "catalog",
            table: "event_price_tiers");

        migrationBuilder.CreateIndex(
            name: "ix_seat_map_sections_seat_map_id_name",
            schema: "catalog",
            table: "seat_map_sections",
            columns: new[] { "seat_map_id", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_event_price_tiers_event_id_section_id",
            schema: "catalog",
            table: "event_price_tiers",
            columns: new[] { "event_id", "section_id" },
            unique: true);

        migrationBuilder.Sql(
            """
            create unique index ix_seat_map_sections_seat_map_id_name_ci
                on catalog.seat_map_sections (seat_map_id, lower(name));
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop index catalog.ix_seat_map_sections_seat_map_id_name_ci;");

        migrationBuilder.DropIndex(
            name: "ix_seat_map_sections_seat_map_id_name",
            schema: "catalog",
            table: "seat_map_sections");

        migrationBuilder.DropIndex(
            name: "ix_event_price_tiers_event_id_section_id",
            schema: "catalog",
            table: "event_price_tiers");

        migrationBuilder.CreateIndex(
            name: "ix_seat_map_sections_seat_map_id",
            schema: "catalog",
            table: "seat_map_sections",
            column: "seat_map_id");

        migrationBuilder.CreateIndex(
            name: "ix_event_price_tiers_event_id",
            schema: "catalog",
            table: "event_price_tiers",
            column: "event_id");
    }
}
