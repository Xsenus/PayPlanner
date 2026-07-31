using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddMaxInboundCorrespondencePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaxInboundCorrespondenceMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    InboundEventId = table.Column<int>(type: "integer", nullable: false),
                    RecipientLinkId = table.Column<int>(type: "integer", nullable: false),
                    ClientPortalAccountId = table.Column<int>(type: "integer", nullable: false),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: true),
                    ExternalMessageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProtectedText = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    BodyPreview = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RoutedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RoutedByUserId = table.Column<int>(type: "integer", nullable: true)
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
                        name: "FK_MaxInboundCorrespondenceMessages_ClientPortalAccounts_Clien~",
                        column: x => x.ClientPortalAccountId,
                        principalTable: "ClientPortalAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaxInboundCorrespondenceMessages_MaxInboundEvents_InboundEv~",
                        column: x => x.InboundEventId,
                        principalTable: "MaxInboundEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaxInboundCorrespondenceMessages_MaxRecipientLinks_Recipien~",
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
                name: "IX_MaxInboundCorrespondenceMessages_RecipientLinkId_ExternalMe~",
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
