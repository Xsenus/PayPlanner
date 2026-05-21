using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    public partial class AddClientCaseCourtDirectoryPostgres : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseCourts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Instance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Contacts = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ClientCaseCourts", x => x.Id));

            migrationBuilder.AddColumn<int>(
                name: "CourtId",
                table: "ClientCaseCourtEvents",
                type: "integer",
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
