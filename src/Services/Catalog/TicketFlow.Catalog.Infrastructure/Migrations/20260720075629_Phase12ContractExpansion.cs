using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12ContractExpansion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "organizer_id",
            schema: "catalog",
            table: "events",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_events_organizer_id",
            schema: "catalog",
            table: "events",
            column: "organizer_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_events_organizer_id",
            schema: "catalog",
            table: "events");

        migrationBuilder.DropColumn(
            name: "organizer_id",
            schema: "catalog",
            table: "events");

    }
}
