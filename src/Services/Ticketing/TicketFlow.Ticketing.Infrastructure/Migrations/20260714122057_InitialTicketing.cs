using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Ticketing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialTicketing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ticketing");

            migrationBuilder.CreateTable(
                name: "tickets",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_id = table.Column<Guid>(type: "uuid", nullable: false),
                    qr_payload = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    pdf_object_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_order_id_seat_id",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "order_id", "seat_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tickets",
                schema: "ticketing");
        }
    }
}
