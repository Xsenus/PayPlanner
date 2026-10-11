using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ObjectAuditEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ObjectType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObjectAuditEvents_CreatedAt",
                table: "ObjectAuditEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectAuditEvents_Object_CreatedAt",
                table: "ObjectAuditEvents",
                columns: new[] { "ObjectType", "ObjectId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObjectAuditEvents");
        }
    }
}
