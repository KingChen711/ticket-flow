using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Reservation.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12AuthoritativeCheckout : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "event_sale_policies",
            schema: "reservation",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                sale_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                sale_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                price_snapshot_version = table.Column<long>(type: "bigint", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_event_sale_policies", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "hold_quotes",
            schema: "reservation",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                seat_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                price_snapshot_version = table.Column<long>(type: "bigint", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_hold_quotes", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "event_sale_price_tiers",
            schema: "reservation",
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
                table.PrimaryKey("pk_event_sale_price_tiers", x => x.id);
                table.ForeignKey(
                    name: "fk_event_sale_price_tiers_event_sale_policies_event_id",
                    column: x => x.event_id,
                    principalSchema: "reservation",
                    principalTable: "event_sale_policies",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_event_sale_price_tiers_event_id_section_id",
            schema: "reservation",
            table: "event_sale_price_tiers",
            columns: new[] { "event_id", "section_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_hold_quotes_event_id",
            schema: "reservation",
            table: "hold_quotes",
            column: "event_id");

        migrationBuilder.CreateIndex(
            name: "ix_hold_quotes_owner_user_id",
            schema: "reservation",
            table: "hold_quotes",
            column: "owner_user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "event_sale_price_tiers",
            schema: "reservation");

        migrationBuilder.DropTable(
            name: "hold_quotes",
            schema: "reservation");

        migrationBuilder.DropTable(
            name: "event_sale_policies",
            schema: "reservation");
    }
}
