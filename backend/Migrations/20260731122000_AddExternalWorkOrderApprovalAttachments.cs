using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalWorkOrderApprovalAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalWorkOrderApprovalAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalWorkOrderApprovalRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientCaseDocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalWorkOrderApprovalAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalWorkOrderApprovalAttachments_ClientCaseDocuments_ClientCaseDocumentId",
                        column: x => x.ClientCaseDocumentId,
                        principalTable: "ClientCaseDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalWorkOrderApprovalAttachments_ExternalWorkOrderApprovalRequests_ExternalWorkOrderApprovalRequestId",
                        column: x => x.ExternalWorkOrderApprovalRequestId,
                        principalTable: "ExternalWorkOrderApprovalRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWorkOrderApprovalAttachments_ClientCaseDocumentId",
                table: "ExternalWorkOrderApprovalAttachments",
                column: "ClientCaseDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWorkOrderApprovalAttachments_PublicId",
                table: "ExternalWorkOrderApprovalAttachments",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalWorkOrderApprovalAttachments_RequestId_DocumentId",
                table: "ExternalWorkOrderApprovalAttachments",
                columns: new[] { "ExternalWorkOrderApprovalRequestId", "ClientCaseDocumentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalWorkOrderApprovalAttachments");
        }
    }
}
