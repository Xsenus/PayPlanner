using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBalanceApplicationReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BalanceApplicationReceipts",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CommandSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CommandVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    LegalEntityId = table.Column<int>(type: "INTEGER", nullable: true),
                    PaymentId = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BalanceApplicationReceipts", x => x.RequestId);
                    table.CheckConstraint("CK_BalanceApplicationReceipts_Outcome", "\"ActorUserId\" > 0 AND \"PaymentId\" > 0 AND \"AppliedAmount\" > 0 AND length(\"CommandSha256\") = 64");
                    table.CheckConstraint("CK_BalanceApplicationReceipts_Owner", "(\"ClientId\" IS NOT NULL AND \"LegalEntityId\" IS NULL) OR (\"ClientId\" IS NULL AND \"LegalEntityId\" IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BalanceApplicationReceipts_ActorUserId",
                table: "BalanceApplicationReceipts",
                column: "ActorUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BalanceApplicationReceipts");
        }
    }
}
