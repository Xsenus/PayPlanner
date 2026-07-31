using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMaxInboundCorrespondence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaxInboundCorrespondenceMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InboundEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipientLinkId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientPortalAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientCaseId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalMessageId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProtectedText = table.Column<string>(type: "TEXT", maxLength: 12000, nullable: false),
                    BodyPreview = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RoutedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RoutedByUserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaxInboundCorrespondenceMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaxInboundCorrespondenceMessages_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaxInboundCorrespondenceMessages_ClientPortalAccounts_ClientPortalAccountId",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaxInboundCorrespondenceMessages_MaxInboundEvents_InboundEventId",
                        column: x => x.InboundEventId,
                        principalTable: "MaxInboundEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaxInboundCorrespondenceMessages_MaxRecipientLinks_RecipientLinkId",
                        column: x => x.RecipientLinkId,
                        principalTable: "MaxRecipientLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaxInboundCorrespondenceMessages_Users_RoutedByUserId",
                        column: x => x.RoutedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundCorrespondenceMessages_ClientCaseId_CreatedAt",
                table: "MaxInboundCorrespondenceMessages",
                columns: new[] { "ClientCaseId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundCorrespondenceMessages_ClientPortalAccountId",
                table: "MaxInboundCorrespondenceMessages",
                column: "ClientPortalAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundCorrespondenceMessages_ExpiresAt",
                table: "MaxInboundCorrespondenceMessages",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundCorrespondenceMessages_InboundEventId",
                table: "MaxInboundCorrespondenceMessages",
                column: "InboundEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundCorrespondenceMessages_PublicId",
                table: "MaxInboundCorrespondenceMessages",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundCorrespondenceMessages_RecipientLinkId_ExternalMessageId",
                table: "MaxInboundCorrespondenceMessages",
                columns: new[] { "RecipientLinkId", "ExternalMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundCorrespondenceMessages_RoutedByUserId",
                table: "MaxInboundCorrespondenceMessages",
                column: "RoutedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MaxInboundCorrespondenceMessages_Status_CreatedAt",
                table: "MaxInboundCorrespondenceMessages",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaxInboundCorrespondenceMessages");
        }
    }
}
