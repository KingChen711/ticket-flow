using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Catalog.Infrastructure.Migrations;

/// <summary>
/// Commit 14. Additive and nullable, like every ownership column this phase has added: an
/// onboarding workflow started before this migration has no organizer, and null must keep
/// meaning "unowned" rather than being backfilled from a guess (mục 10). Event.OrganizerId
/// itself needed no migration — Commit 2 already created the column expand-only; this commit
/// is the first to write it.
/// </summary>
public partial class Phase12OrganizerOwnership : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "organizer_id",
            schema: "catalog",
            table: "event_onboarding_workflows",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "organizer_id",
            schema: "catalog",
            table: "event_onboarding_workflows");
    }
}
