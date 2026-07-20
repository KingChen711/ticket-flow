using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Payment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12ContractExpansion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "payment_attempts",
            schema: "payment",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                operation_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                gateway_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_payment_attempts", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "payment_refunds",
            schema: "payment",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                operation_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                gateway_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_payment_refunds", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_payment_attempts_operation_key",
            schema: "payment",
            table: "payment_attempts",
            column: "operation_key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_payment_attempts_order_id",
            schema: "payment",
            table: "payment_attempts",
            column: "order_id");

        migrationBuilder.CreateIndex(
            name: "ix_payment_refunds_operation_key",
            schema: "payment",
            table: "payment_refunds",
            column: "operation_key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_payment_refunds_payment_id",
            schema: "payment",
            table: "payment_refunds",
            column: "payment_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "payment_attempts",
            schema: "payment");

        migrationBuilder.DropTable(
            name: "payment_refunds",
            schema: "payment");
    }
}
