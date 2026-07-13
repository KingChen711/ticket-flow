using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Reservation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reservation");

            migrationBuilder.CreateTable(
                name: "seats",
                schema: "reservation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seats", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_seats_event_id_label",
                schema: "reservation",
                table: "seats",
                columns: new[] { "event_id", "label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seats_event_id_section_id",
                schema: "reservation",
                table: "seats",
                columns: new[] { "event_id", "section_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "seats",
                schema: "reservation");
        }
    }
}
