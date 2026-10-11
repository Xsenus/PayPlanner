using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    [Migration("20260521170130_AddAppReleasesPostgres")]
    [DbContext(typeof(PostgreSqlPaymentContext))]
    public partial class AddAppReleasesPostgres : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppReleases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReleaseId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "manual"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_AppReleases", x => x.Id));

            migrationBuilder.CreateTable(
                name: "AppReleaseItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppReleaseId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppReleaseItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppReleaseItems_AppReleases_AppReleaseId",
                        column: x => x.AppReleaseId,
                        principalTable: "AppReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppReleaseSeen",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppReleaseId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    SeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppReleaseSeen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppReleaseSeen_AppReleases_AppReleaseId",
                        column: x => x.AppReleaseId,
                        principalTable: "AppReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppReleaseSeen_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("IX_AppReleases_ReleaseId", "AppReleases", "ReleaseId", unique: true);
            migrationBuilder.CreateIndex("IX_AppReleases_IsActive_ReleasedAt", "AppReleases", new[] { "IsActive", "ReleasedAt" });
            migrationBuilder.CreateIndex("IX_AppReleaseItems_Release_SortOrder", "AppReleaseItems", new[] { "AppReleaseId", "SortOrder" });
            migrationBuilder.CreateIndex("IX_AppReleaseSeen_ReleaseId", "AppReleaseSeen", "AppReleaseId");
            migrationBuilder.CreateIndex("IX_AppReleaseSeen_User_Release", "AppReleaseSeen", new[] { "UserId", "AppReleaseId" }, unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("AppReleaseItems");
            migrationBuilder.DropTable("AppReleaseSeen");
            migrationBuilder.DropTable("AppReleases");
        }
    }
}
