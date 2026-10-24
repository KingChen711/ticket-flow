using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Ticketing.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12TicketCancellationTombstone : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<uint>(
            name: "xmin",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            type: "xid",
            rowVersion: true,
            nullable: false,
            defaultValue: 0u);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "xmin",
            schema: "ticketing",
            table: "ticket_issuance_jobs");
    }
}
