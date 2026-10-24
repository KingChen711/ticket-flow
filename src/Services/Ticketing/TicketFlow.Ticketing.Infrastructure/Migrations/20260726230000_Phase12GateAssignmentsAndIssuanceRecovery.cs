using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Ticketing.Infrastructure.Migrations;

[DbContext(typeof(TicketingDbContext))]
[Migration("20260726230000_Phase12GateAssignmentsAndIssuanceRecovery")]
public sealed class Phase12GateAssignmentsAndIssuanceRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "artifacts_reconciled_at",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "customer_email",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            type: "character varying(320)",
            maxLength: 320,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "customer_name",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<Guid[]>(
            name: "expected_seat_ids",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            type: "uuid[]",
            nullable: false,
            defaultValueSql: "'{}'::uuid[]");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_attempt_at",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_recovery_at",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "operation_key",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "price_snapshot_version",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        // Existing jobs predate the command snapshot. They stay non-recoverable until a genuine
        // broker redelivery captures that intent, but their last attempt is known to be creation.
        migrationBuilder.Sql(
            """
            UPDATE ticketing.ticket_issuance_jobs
            SET last_attempt_at = created_at
            WHERE last_attempt_at IS NULL;
            """);

        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "last_attempt_at",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            type: "timestamp with time zone",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "timestamp with time zone",
            oldNullable: true);

        migrationBuilder.Sql(
            """
            ALTER TABLE ticketing.ticket_issuance_jobs
            ALTER COLUMN expected_seat_ids DROP DEFAULT;
            """);

        migrationBuilder.CreateTable(
            name: "gate_assignments",
            schema: "ticketing",
            columns: table => new
            {
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                gate_id = table.Column<string>(
                    type: "character varying(80)",
                    maxLength: 80,
                    nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                assigned_by = table.Column<Guid>(type: "uuid", nullable: false),
                assigned_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "pk_gate_assignments",
                    assignment => new
                    {
                        assignment.event_id,
                        assignment.gate_id,
                        assignment.user_id,
                    });
            });

        migrationBuilder.CreateIndex(
            name: "ix_gate_assignments_user_id",
            schema: "ticketing",
            table: "gate_assignments",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_ticket_issuance_jobs_state_completed_at_artifacts_reconcile",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            columns: new[] { "state", "completed_at", "artifacts_reconciled_at" });

        migrationBuilder.CreateIndex(
            name: "ix_ticket_issuance_jobs_state_last_attempt_at_last_recovery_at",
            schema: "ticketing",
            table: "ticket_issuance_jobs",
            columns: new[] { "state", "last_attempt_at", "last_recovery_at" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "gate_assignments",
            schema: "ticketing");

        migrationBuilder.DropIndex(
            name: "ix_ticket_issuance_jobs_state_completed_at_artifacts_reconcile",
            schema: "ticketing",
            table: "ticket_issuance_jobs");

        migrationBuilder.DropIndex(
            name: "ix_ticket_issuance_jobs_state_last_attempt_at_last_recovery_at",
            schema: "ticketing",
            table: "ticket_issuance_jobs");

        migrationBuilder.DropColumn(
            name: "artifacts_reconciled_at",
            schema: "ticketing",
            table: "ticket_issuance_jobs");

        migrationBuilder.DropColumn(
            name: "customer_email",
            schema: "ticketing",
            table: "ticket_issuance_jobs");

        migrationBuilder.DropColumn(
            name: "customer_name",
            schema: "ticketing",
            table: "ticket_issuance_jobs");

        migrationBuilder.DropColumn(
            name: "expected_seat_ids",
            schema: "ticketing",
            table: "ticket_issuance_jobs");

        migrationBuilder.DropColumn(
            name: "last_attempt_at",
            schema: "ticketing",
            table: "ticket_issuance_jobs");

        migrationBuilder.DropColumn(
            name: "last_recovery_at",
            schema: "ticketing",
            table: "ticket_issuance_jobs");

        migrationBuilder.DropColumn(
            name: "operation_key",
            schema: "ticketing",
            table: "ticket_issuance_jobs");

        migrationBuilder.DropColumn(
            name: "price_snapshot_version",
            schema: "ticketing",
            table: "ticket_issuance_jobs");
    }
}
