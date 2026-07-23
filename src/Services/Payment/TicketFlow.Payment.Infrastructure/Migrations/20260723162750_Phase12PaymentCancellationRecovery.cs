using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Payment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12PaymentCancellationRecovery : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "cancellation_completed_at",
            schema: "payment",
            table: "payment_attempts",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "cancellation_operation_key",
            schema: "payment",
            table: "payment_attempts",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "cancellation_workflow_id",
            schema: "payment",
            table: "payment_attempts",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "cancellation_completed_at",
            schema: "payment",
            table: "payment_attempts");

        migrationBuilder.DropColumn(
            name: "cancellation_operation_key",
            schema: "payment",
            table: "payment_attempts");

        migrationBuilder.DropColumn(
            name: "cancellation_workflow_id",
            schema: "payment",
            table: "payment_attempts");
    }
}
