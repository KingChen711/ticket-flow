using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Ticketing.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12EventOwnerProjection : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "event_owners",
            schema: "ticketing",
            columns: table => new
            {
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                organizer_id = table.Column<Guid>(type: "uuid", nullable: false),
                revision = table.Column<long>(type: "bigint", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_event_owners", x => x.event_id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "event_owners",
            schema: "ticketing");
    }
}
