using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddPaymentRemainderTransfersPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentRemainderTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourcePaymentId = table.Column<int>(type: "integer", nullable: false),
                    RemainderPaymentId = table.Column<int>(type: "integer", nullable: false),
                    SourceOriginalAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    RemainderAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    RemainderDate = table.Column<DateTime>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRemainderTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentRemainderTransfers_Payments_RemainderPaymentId",
                        column: x => x.RemainderPaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentRemainderTransfers_Payments_SourcePaymentId",
                        column: x => x.SourcePaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRemainderTransfers_RemainderDate",
                table: "PaymentRemainderTransfers",
                column: "RemainderDate");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRemainderTransfers_RemainderPaymentId",
                table: "PaymentRemainderTransfers",
                column: "RemainderPaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRemainderTransfers_SourcePaymentId",
                table: "PaymentRemainderTransfers",
                column: "SourcePaymentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentRemainderTransfers");
        }
    }
}
