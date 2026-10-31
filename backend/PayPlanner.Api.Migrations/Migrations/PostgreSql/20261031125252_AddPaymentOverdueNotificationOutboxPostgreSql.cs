using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddPaymentOverdueNotificationOutboxPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OverdueNotificationToken",
                table: "Payments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentOverdueNotificationOutboxes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PaymentId = table.Column<int>(type: "integer", nullable: false),
                    EventKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TransitionToken = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DocumentKind = table.Column<int>(type: "integer", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LeaseToken = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentOverdueNotificationOutboxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentOverdueNotificationOutboxes_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOverdueOutbox_Due",
                table: "PaymentOverdueNotificationOutboxes",
                columns: new[] { "CompletedAt", "LeaseUntil", "LastAttemptAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOverdueOutbox_EventKey",
                table: "PaymentOverdueNotificationOutboxes",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOverdueOutbox_Payment",
                table: "PaymentOverdueNotificationOutboxes",
                columns: new[] { "PaymentId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentOverdueNotificationOutboxes");

            migrationBuilder.DropColumn(
                name: "OverdueNotificationToken",
                table: "Payments");
        }
    }
}
