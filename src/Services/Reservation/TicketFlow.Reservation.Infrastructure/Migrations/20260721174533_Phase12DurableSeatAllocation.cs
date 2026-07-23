using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TicketFlow.Reservation.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Phase12DurableSeatAllocation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateSequence(
            name: "seat_allocation_generation",
            schema: "reservation");

        // Widen only: new writers store JSON snapshots as text while the Commit-3 binary can
        // continue writing its scalar varchar outcome during the rolling migration window.
        migrationBuilder.AlterColumn<string>(
            name: "outcome",
            schema: "reservation",
            table: "reservation_operation_receipts",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(100)",
            oldMaxLength: 100,
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "business_key",
            schema: "reservation",
            table: "reservation_operation_receipts",
            type: "character varying(300)",
            maxLength: 300,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(200)",
            oldMaxLength: 200);

        migrationBuilder.AddColumn<string>(
            name: "request_fingerprint",
            schema: "reservation",
            table: "reservation_operation_receipts",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<long>(
            name: "generation",
            schema: "reservation",
            table: "hold_quotes",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "order_id",
            schema: "reservation",
            table: "hold_quotes",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "state",
            schema: "reservation",
            table: "hold_quotes",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "terminal_at",
            schema: "reservation",
            table: "hold_quotes",
            type: "timestamp with time zone",
            nullable: true);

        // Pre-Commit-4 quotes have neither a durable generation nor a typed Redis owner
        // token. Invalidate them fail-closed instead of guessing ownership during cutover.
        migrationBuilder.Sql(
            """
            UPDATE reservation.hold_quotes
            SET generation = nextval('reservation.seat_allocation_generation'),
                state = 'Invalidated',
                terminal_at = CURRENT_TIMESTAMP;

            UPDATE reservation.reservation_operation_receipts
            SET request_fingerprint = md5(id::text) || md5(operation_type || ':' || business_key)
            WHERE request_fingerprint = '';
            """);

        migrationBuilder.AlterColumn<long>(
            name: "generation",
            schema: "reservation",
            table: "hold_quotes",
            type: "bigint",
            nullable: false,
            defaultValueSql: "nextval('reservation.seat_allocation_generation')",
            oldClrType: typeof(long),
            oldType: "bigint",
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "state",
            schema: "reservation",
            table: "hold_quotes",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "Active",
            oldClrType: typeof(string),
            oldType: "character varying(20)",
            oldMaxLength: 20,
            oldNullable: true);

        migrationBuilder.CreateTable(
            name: "inbox_state",
            schema: "reservation",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                consumer_id = table.Column<Guid>(type: "uuid", nullable: false),
                lock_id = table.Column<Guid>(type: "uuid", nullable: false),
                row_version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                receive_count = table.Column<int>(type: "integer", nullable: false),
                expiration_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                last_sequence_number = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_inbox_state", x => x.id);
                table.UniqueConstraint("ak_inbox_state_message_id_consumer_id", x => new { x.message_id, x.consumer_id });
            });

        migrationBuilder.CreateTable(
            name: "outbox_state",
            schema: "reservation",
            columns: table => new
            {
                outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                lock_id = table.Column<Guid>(type: "uuid", nullable: false),
                row_version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                last_sequence_number = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_outbox_state", x => x.outbox_id);
            });

        migrationBuilder.CreateTable(
            name: "outbox_message",
            schema: "reservation",
            columns: table => new
            {
                sequence_number = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                enqueue_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                sent_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                headers = table.Column<string>(type: "text", nullable: true),
                properties = table.Column<string>(type: "text", nullable: true),
                inbox_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                inbox_consumer_id = table.Column<Guid>(type: "uuid", nullable: true),
                outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                content_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                message_type = table.Column<string>(type: "text", nullable: false),
                body = table.Column<string>(type: "text", nullable: false),
                conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                initiator_id = table.Column<Guid>(type: "uuid", nullable: true),
                request_id = table.Column<Guid>(type: "uuid", nullable: true),
                source_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                destination_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                response_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                fault_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                expiration_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_outbox_message", x => x.sequence_number);
                table.ForeignKey(
                    name: "fk_outbox_message_inbox_state_inbox_message_id_inbox_consumer_",
                    columns: x => new { x.inbox_message_id, x.inbox_consumer_id },
                    principalSchema: "reservation",
                    principalTable: "inbox_state",
                    principalColumns: new[] { "message_id", "consumer_id" });
                table.ForeignKey(
                    name: "fk_outbox_message_outbox_state_outbox_id",
                    column: x => x.outbox_id,
                    principalSchema: "reservation",
                    principalTable: "outbox_state",
                    principalColumn: "outbox_id");
            });

        migrationBuilder.CreateIndex(
            name: "ix_seat_allocations_hold_id_generation",
            schema: "reservation",
            table: "seat_allocations",
            columns: new[] { "hold_id", "generation" });

        migrationBuilder.CreateIndex(
            name: "ix_hold_quotes_event_id_state",
            schema: "reservation",
            table: "hold_quotes",
            columns: new[] { "event_id", "state" });

        migrationBuilder.CreateIndex(
            name: "ix_hold_quotes_order_id",
            schema: "reservation",
            table: "hold_quotes",
            column: "order_id",
            unique: true,
            filter: "order_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_hold_quotes_owner_user_id_state",
            schema: "reservation",
            table: "hold_quotes",
            columns: new[] { "owner_user_id", "state" });

        migrationBuilder.AddCheckConstraint(
            name: "ck_hold_quotes_lifecycle",
            schema: "reservation",
            table: "hold_quotes",
            sql: "(state = 'Active' AND order_id IS NULL AND terminal_at IS NULL) OR (state = 'Consumed' AND order_id IS NOT NULL AND terminal_at IS NOT NULL) OR (state IN ('Expired', 'Invalidated') AND order_id IS NULL AND terminal_at IS NOT NULL)");

        migrationBuilder.CreateIndex(
            name: "ix_inbox_state_delivered",
            schema: "reservation",
            table: "inbox_state",
            column: "delivered");

        migrationBuilder.CreateIndex(
            name: "ix_outbox_message_enqueue_time",
            schema: "reservation",
            table: "outbox_message",
            column: "enqueue_time");

        migrationBuilder.CreateIndex(
            name: "ix_outbox_message_expiration_time",
            schema: "reservation",
            table: "outbox_message",
            column: "expiration_time");

        migrationBuilder.CreateIndex(
            name: "ix_outbox_message_inbox_message_id_inbox_consumer_id_sequence_",
            schema: "reservation",
            table: "outbox_message",
            columns: new[] { "inbox_message_id", "inbox_consumer_id", "sequence_number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_outbox_message_outbox_id_sequence_number",
            schema: "reservation",
            table: "outbox_message",
            columns: new[] { "outbox_id", "sequence_number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_outbox_state_created",
            schema: "reservation",
            table: "outbox_state",
            column: "created");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "outbox_message",
            schema: "reservation");

        migrationBuilder.DropTable(
            name: "inbox_state",
            schema: "reservation");

        migrationBuilder.DropTable(
            name: "outbox_state",
            schema: "reservation");

        migrationBuilder.DropIndex(
            name: "ix_seat_allocations_hold_id_generation",
            schema: "reservation",
            table: "seat_allocations");

        migrationBuilder.DropIndex(
            name: "ix_hold_quotes_event_id_state",
            schema: "reservation",
            table: "hold_quotes");

        migrationBuilder.DropIndex(
            name: "ix_hold_quotes_order_id",
            schema: "reservation",
            table: "hold_quotes");

        migrationBuilder.DropIndex(
            name: "ix_hold_quotes_owner_user_id_state",
            schema: "reservation",
            table: "hold_quotes");

        migrationBuilder.DropCheckConstraint(
            name: "ck_hold_quotes_lifecycle",
            schema: "reservation",
            table: "hold_quotes");

        migrationBuilder.DropColumn(
            name: "request_fingerprint",
            schema: "reservation",
            table: "reservation_operation_receipts");

        migrationBuilder.DropColumn(
            name: "generation",
            schema: "reservation",
            table: "hold_quotes");

        migrationBuilder.DropColumn(
            name: "order_id",
            schema: "reservation",
            table: "hold_quotes");

        migrationBuilder.DropColumn(
            name: "state",
            schema: "reservation",
            table: "hold_quotes");

        migrationBuilder.DropColumn(
            name: "terminal_at",
            schema: "reservation",
            table: "hold_quotes");

        migrationBuilder.DropSequence(
            name: "seat_allocation_generation",
            schema: "reservation");

        migrationBuilder.Sql(
            """
            ALTER TABLE reservation.reservation_operation_receipts
            ALTER COLUMN outcome TYPE character varying(100)
            USING LEFT(outcome, 100);
            """);

        migrationBuilder.AlterColumn<string>(
            name: "business_key",
            schema: "reservation",
            table: "reservation_operation_receipts",
            type: "character varying(200)",
            maxLength: 200,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(300)",
            oldMaxLength: 300);
    }
}
