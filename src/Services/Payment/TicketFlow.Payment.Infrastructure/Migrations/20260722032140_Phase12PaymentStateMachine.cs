using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketFlow.Payment.Infrastructure.Migrations
{
    /// <summary>
    /// Additive only. Making <c>order_id</c> UNIQUE would normally be a risky tightening, but
    /// Commit 2 created payment_attempts/payment_refunds as expand-only schema with no writer at
    /// all — the tables are empty, so there is no existing data that could violate it and no
    /// backfill to guess at. The same fact makes the PaymentAttemptState/PaymentRefundState value
    /// renames (Succeeded→Authorized, Failed→Declined, plus the new Unknown) safe: no persisted
    /// string can carry a retired value.
    /// </summary>
    public partial class Phase12PaymentStateMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payment_attempts_order_id",
                schema: "payment",
                table: "payment_attempts");

            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                schema: "payment",
                table: "payment_refunds",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_reconciled_at",
                schema: "payment",
                table: "payment_refunds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reconcile_attempts",
                schema: "payment",
                table: "payment_refunds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                schema: "payment",
                table: "payment_attempts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_reconciled_at",
                schema: "payment",
                table: "payment_attempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reconcile_attempts",
                schema: "payment",
                table: "payment_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_order_id",
                schema: "payment",
                table: "payment_refunds",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_state",
                schema: "payment",
                table: "payment_refunds",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_order_id",
                schema: "payment",
                table: "payment_attempts",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_state",
                schema: "payment",
                table: "payment_attempts",
                column: "state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payment_refunds_order_id",
                schema: "payment",
                table: "payment_refunds");

            migrationBuilder.DropIndex(
                name: "ix_payment_refunds_state",
                schema: "payment",
                table: "payment_refunds");

            migrationBuilder.DropIndex(
                name: "ix_payment_attempts_order_id",
                schema: "payment",
                table: "payment_attempts");

            migrationBuilder.DropIndex(
                name: "ix_payment_attempts_state",
                schema: "payment",
                table: "payment_attempts");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                schema: "payment",
                table: "payment_refunds");

            migrationBuilder.DropColumn(
                name: "last_reconciled_at",
                schema: "payment",
                table: "payment_refunds");

            migrationBuilder.DropColumn(
                name: "reconcile_attempts",
                schema: "payment",
                table: "payment_refunds");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                schema: "payment",
                table: "payment_attempts");

            migrationBuilder.DropColumn(
                name: "last_reconciled_at",
                schema: "payment",
                table: "payment_attempts");

            migrationBuilder.DropColumn(
                name: "reconcile_attempts",
                schema: "payment",
                table: "payment_attempts");

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_order_id",
                schema: "payment",
                table: "payment_attempts",
                column: "order_id");
        }
    }
}
