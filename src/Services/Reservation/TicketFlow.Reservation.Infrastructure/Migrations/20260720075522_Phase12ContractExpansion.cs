using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Reservation.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12ContractExpansion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "reservation_operation_receipts",
            schema: "reservation",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                operation_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                business_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                outcome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_reservation_operation_receipts", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "seat_allocations",
            schema: "reservation",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                seat_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: true),
                owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                generation = table.Column<long>(type: "bigint", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                price_snapshot_version = table.Column<long>(type: "bigint", nullable: true),
                amount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_seat_allocations", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_reservation_operation_receipts_operation_type_business_key",
            schema: "reservation",
            table: "reservation_operation_receipts",
            columns: new[] { "operation_type", "business_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_seat_allocations_event_id_seat_id",
            schema: "reservation",
            table: "seat_allocations",
            columns: new[] { "event_id", "seat_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_seat_allocations_hold_id",
            schema: "reservation",
            table: "seat_allocations",
            column: "hold_id");

        migrationBuilder.CreateIndex(
            name: "ix_seat_allocations_order_id",
            schema: "reservation",
            table: "seat_allocations",
            column: "order_id");

        migrationBuilder.CreateIndex(
            name: "ix_seat_allocations_owner_user_id",
            schema: "reservation",
            table: "seat_allocations",
            column: "owner_user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "reservation_operation_receipts",
            schema: "reservation");

        migrationBuilder.DropTable(
            name: "seat_allocations",
            schema: "reservation");
    }
}
