using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Ticketing.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12ContractExpansion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "customer_id",
            schema: "ticketing",
            table: "tickets",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "event_id",
            schema: "ticketing",
            table: "tickets",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "issuance_state",
            schema: "ticketing",
            table: "tickets",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "revocation_reason",
            schema: "ticketing",
            table: "tickets",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "revoked_at",
            schema: "ticketing",
            table: "tickets",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_tickets_customer_id",
            schema: "ticketing",
            table: "tickets",
            column: "customer_id");

        migrationBuilder.CreateIndex(
            name: "ix_tickets_event_id",
            schema: "ticketing",
            table: "tickets",
            column: "event_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_tickets_customer_id",
            schema: "ticketing",
            table: "tickets");

        migrationBuilder.DropIndex(
            name: "ix_tickets_event_id",
            schema: "ticketing",
            table: "tickets");

        migrationBuilder.DropColumn(
            name: "customer_id",
            schema: "ticketing",
            table: "tickets");

        migrationBuilder.DropColumn(
            name: "event_id",
            schema: "ticketing",
            table: "tickets");

        migrationBuilder.DropColumn(
            name: "issuance_state",
            schema: "ticketing",
            table: "tickets");

        migrationBuilder.DropColumn(
            name: "revocation_reason",
            schema: "ticketing",
            table: "tickets");

        migrationBuilder.DropColumn(
            name: "revoked_at",
            schema: "ticketing",
            table: "tickets");
    }
}
