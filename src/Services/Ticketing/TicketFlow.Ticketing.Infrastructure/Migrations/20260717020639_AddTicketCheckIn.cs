using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Ticketing.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddTicketCheckIn : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "checked_in_at",
            schema: "ticketing",
            table: "tickets",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_tickets_qr_payload",
            schema: "ticketing",
            table: "tickets",
            column: "qr_payload",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_tickets_qr_payload",
            schema: "ticketing",
            table: "tickets");

        migrationBuilder.DropColumn(
            name: "checked_in_at",
            schema: "ticketing",
            table: "tickets");
    }
}
