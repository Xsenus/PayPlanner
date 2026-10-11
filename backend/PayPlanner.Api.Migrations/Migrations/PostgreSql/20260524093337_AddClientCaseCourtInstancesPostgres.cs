using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientCaseCourtInstancesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCaseCourtInstances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    Instance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CourtId = table.Column<int>(type: "integer", nullable: true),
                    CourtName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CourtCaseNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CourtUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    JudgeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Courtroom = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SecretaryName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseCourtInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtInstances_ClientCaseCourts_CourtId",
                        column: x => x.CourtId,
                        principalTable: "ClientCaseCourts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtInstances_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientCaseCourtComplaints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientCaseId = table.Column<int>(type: "integer", nullable: false),
                    CourtInstanceId = table.Column<int>(type: "integer", nullable: false),
                    ComplaintType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetInstance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FiledAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClientCaseDocumentId = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCaseCourtComplaints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtComplaints_ClientCaseCourtInstances_CourtIns~",
                        column: x => x.CourtInstanceId,
                        principalTable: "ClientCaseCourtInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtComplaints_ClientCaseDocuments_ClientCaseDoc~",
                        column: x => x.ClientCaseDocumentId,
                        principalTable: "ClientCaseDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientCaseCourtComplaints_ClientCases_ClientCaseId",
                        column: x => x.ClientCaseId,
                        principalTable: "ClientCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseCourtComplaints_Case_Target_Archived",
                table: "ClientCaseCourtComplaints",
                columns: new[] { "ClientCaseId", "TargetInstance", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseCourtComplaints_ClientCaseId",
                table: "ClientCaseCourtComplaints",
                column: "ClientCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseCourtComplaints_CourtInstanceId",
                table: "ClientCaseCourtComplaints",
                column: "CourtInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseCourtComplaints_DocumentId",
                table: "ClientCaseCourtComplaints",
                column: "ClientCaseDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseCourtInstances_Case_Instance",
                table: "ClientCaseCourtInstances",
                columns: new[] { "ClientCaseId", "Instance" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseCourtInstances_ClientCaseId",
                table: "ClientCaseCourtInstances",
                column: "ClientCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseCourtInstances_CourtId",
                table: "ClientCaseCourtInstances",
                column: "CourtId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCaseCourtInstances_Instance",
                table: "ClientCaseCourtInstances",
                column: "Instance");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ClientCaseCourtComplaints");
            migrationBuilder.DropTable(name: "ClientCaseCourtInstances");
        }
    }
}
