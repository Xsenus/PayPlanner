using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddPaymentCreateAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentCreateAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SellerCompanyId = table.Column<int>(type: "integer", nullable: true),
                    SellerBranchId = table.Column<int>(type: "integer", nullable: true),
                    PrimaryPaymentId = table.Column<int>(type: "integer", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IdentityVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayloadIdentity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CanonicalPaymentId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentCreateAttempts", x => x.Id);
                    table.CheckConstraint("CK_PaymentCreateAttempts_Operation", "(\"Operation\" = 'primary' AND \"PrimaryPaymentId\" IS NULL) OR (\"Operation\" = 'remainder' AND \"PrimaryPaymentId\" IS NOT NULL AND \"PrimaryPaymentId\" > 0)");
                    table.CheckConstraint("CK_PaymentCreateAttempts_Outcome", "(\"State\" = 'Committed' AND \"CanonicalPaymentId\" IS NOT NULL AND \"CanonicalPaymentId\" > 0 AND \"PayloadIdentity\" IS NOT NULL AND length(\"PayloadIdentity\") = 64 AND \"CommittedAtUtc\" IS NOT NULL AND \"CancelledAtUtc\" IS NULL) OR (\"State\" = 'Draft' AND \"CanonicalPaymentId\" IS NULL AND \"PayloadIdentity\" IS NULL AND \"CommittedAtUtc\" IS NULL AND \"CancelledAtUtc\" IS NULL) OR (\"State\" = 'Cancelled' AND \"CanonicalPaymentId\" IS NULL AND \"PayloadIdentity\" IS NULL AND \"CommittedAtUtc\" IS NULL AND \"CancelledAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_PaymentCreateAttempts_Ownership", "\"ActorUserId\" > 0 AND (\"SellerCompanyId\" IS NULL OR \"SellerCompanyId\" > 0) AND (\"SellerBranchId\" IS NULL OR (\"SellerBranchId\" > 0 AND \"SellerCompanyId\" IS NOT NULL))");
                    table.CheckConstraint("CK_PaymentCreateAttempts_State", "\"State\" IN ('Draft','Committed','Cancelled')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCreateAttempts_ActorUserId",
                table: "PaymentCreateAttempts",
                column: "ActorUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentCreateAttempts");
        }
    }
}
