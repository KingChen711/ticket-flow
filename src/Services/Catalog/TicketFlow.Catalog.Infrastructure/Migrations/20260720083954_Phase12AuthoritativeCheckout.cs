using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12AuthoritativeCheckout : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "aggregate_version",
            schema: "catalog",
            table: "events",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "price_snapshot_version",
            schema: "catalog",
            table: "events",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "aggregate_version",
            schema: "catalog",
            table: "events");

        migrationBuilder.DropColumn(
            name: "price_snapshot_version",
            schema: "catalog",
            table: "events");
    }
}
