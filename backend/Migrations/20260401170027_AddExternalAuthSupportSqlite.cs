using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalAuthSupportSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_Employment_Range",
                table: "Users");

            migrationBuilder.CreateTable(
                name: "ExternalAuthRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ReturnUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConsumedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalAuthRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalAuthRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TelegramAuthRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    State = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    TelegramUserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConsumedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramAuthRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramAuthRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UserExternalIdentities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ProviderUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProviderEmail = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProviderUsername = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AvatarUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TelegramUserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserExternalIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserExternalIdentities_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_Employment_Range",
                table: "Users",
                sql: "(\"EmploymentStartDate\" IS NULL OR \"EmploymentEndDate\" IS NULL OR \"EmploymentStartDate\" <= \"EmploymentEndDate\")");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthRequests_Provider_Status",
                table: "ExternalAuthRequests",
                columns: new[] { "Provider", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthRequests_State",
                table: "ExternalAuthRequests",
                column: "State",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAuthRequests_UserId",
                table: "ExternalAuthRequests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramAuthRequests_State",
                table: "TelegramAuthRequests",
                column: "State",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramAuthRequests_Status_ExpiresAt",
                table: "TelegramAuthRequests",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramAuthRequests_UserId",
                table: "TelegramAuthRequests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserExternalIdentities_Provider_UserId",
                table: "UserExternalIdentities",
                columns: new[] { "Provider", "ProviderUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserExternalIdentities_User_Provider",
                table: "UserExternalIdentities",
                columns: new[] { "UserId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserExternalIdentities_UserId",
                table: "UserExternalIdentities",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalAuthRequests");

            migrationBuilder.DropTable(
                name: "TelegramAuthRequests");

            migrationBuilder.DropTable(
                name: "UserExternalIdentities");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_Employment_Range",
                table: "Users");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_Employment_Range",
                table: "Users",
                sql: "(EmploymentStartDate IS NULL OR EmploymentEndDate IS NULL OR EmploymentStartDate <= EmploymentEndDate)");
        }
    }
}
