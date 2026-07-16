using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class DropPasswordHash : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "password_hash",
            schema: "identity",
            table: "users");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "password_hash",
            schema: "identity",
            table: "users",
            type: "text",
            nullable: false,
            defaultValue: "");
    }
}
