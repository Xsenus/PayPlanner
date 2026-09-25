using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCasePositionTemplatesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CasePositionTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    OwnerUserId = table.Column<int>(type: "integer", nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CasePositionTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CasePositionTemplates_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CasePositionTemplate_UpdatedAt_Audit",
                table: "CasePositionTemplates",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CasePositionTemplates_IsArchived_Title",
                table: "CasePositionTemplates",
                columns: new[] { "IsArchived", "Title" });

            migrationBuilder.CreateIndex(
                name: "IX_CasePositionTemplates_OwnerUserId_IsArchived_Title",
                table: "CasePositionTemplates",
                columns: new[] { "OwnerUserId", "IsArchived", "Title" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CasePositionTemplates");
        }
    }
}
