using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Payment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12PaymentCancellationTombstone : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "cancellation_requested_at",
            schema: "payment",
            table: "payment_attempts",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "cancellation_requested_at",
            schema: "payment",
            table: "payment_attempts");
    }
}
