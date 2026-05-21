using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    public partial class AddClientCaseCourtDirectory : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseCourts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Instance = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Contacts = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ClientCaseCourts", x => x.Id));

            migrationBuilder.AddColumn<int>(
                name: "CourtId",
                table: "ClientCaseCourtEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex("IX_ClientCaseCourts_Active_Sort_Name", "ClientCaseCourts", new[] { "IsActive", "SortOrder", "Name" });
            migrationBuilder.CreateIndex("IX_ClientCaseCourts_Instance", "ClientCaseCourts", "Instance");
            migrationBuilder.CreateIndex("IX_ClientCaseCourts_IsActive", "ClientCaseCourts", "IsActive");
            migrationBuilder.CreateIndex("IX_ClientCaseCourts_Name", "ClientCaseCourts", "Name");
            migrationBuilder.CreateIndex("IX_ClientCaseCourtEvents_CourtId", "ClientCaseCourtEvents", "CourtId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientCaseCourtEvents_ClientCaseCourts_CourtId",
                table: "ClientCaseCourtEvents",
                column: "CourtId",
                principalTable: "ClientCaseCourts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey("FK_ClientCaseCourtEvents_ClientCaseCourts_CourtId", "ClientCaseCourtEvents");
            migrationBuilder.DropIndex("IX_ClientCaseCourtEvents_CourtId", "ClientCaseCourtEvents");
            migrationBuilder.DropColumn("CourtId", "ClientCaseCourtEvents");
            migrationBuilder.DropTable(name: "ClientCaseCourts");
        }
    }
}
