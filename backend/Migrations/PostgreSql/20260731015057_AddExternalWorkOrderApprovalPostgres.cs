using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddExternalWorkOrderApprovalPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalWorkOrderApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientCaseWorkOrderId = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccessTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OtpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecipientName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RecipientEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    RecipientType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastSentAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DecisionComment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DecisionAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DecisionIpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DecisionUserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RevokedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalWorkOrderApprovalRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalWorkOrderApprovalRequests_ClientCaseWorkOrders_Clie~",
                        column: x => x.ClientCaseWorkOrderId,
                        principalTable: "ClientCaseWorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWorkOrderApprovalRequests_ExpiresAt",
                table: "ExternalWorkOrderApprovalRequests",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWorkOrderApprovalRequests_PublicId",
                table: "ExternalWorkOrderApprovalRequests",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWorkOrderApprovalRequests_WorkOrderId_Status",
                table: "ExternalWorkOrderApprovalRequests",
                columns: new[] { "ClientCaseWorkOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWorkOrderApprovalRequests_WorkOrderId_Version",
                table: "ExternalWorkOrderApprovalRequests",
                columns: new[] { "ClientCaseWorkOrderId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalWorkOrderApprovalRequests");
        }
    }
}
