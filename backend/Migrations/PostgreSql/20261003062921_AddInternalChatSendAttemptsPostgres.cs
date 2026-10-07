using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddInternalChatSendAttemptsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InternalChatSendAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ThreadId = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<int>(type: "integer", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ReplyToMessageId = table.Column<int>(type: "integer", nullable: true),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    TotalSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    RequestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PublishedMessageId = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    RecoveryLeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    RecoveryLeaseExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RecoveryNextAttemptAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified)),
                    RecoveryAttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RecoveryLastStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalChatSendAttempts", x => x.Id);
                    table.CheckConstraint("CK_InternalChatSendAttempts_Proof", "\"ActorId\" > 0 AND \"FileCount\" BETWEEN 1 AND 10 AND \"TotalSizeBytes\" BETWEEN 1 AND 209715200 AND length(\"Text\") <= 8000 AND length(\"RequestSha256\") = 64 AND \"Version\" > 0 AND (\"ReplyToMessageId\" IS NULL OR \"ReplyToMessageId\" > 0) AND (\"PublishedMessageId\" IS NULL OR \"PublishedMessageId\" > 0) AND \"RecoveryAttemptCount\" >= 0 AND (\"PublishedMessageId\" IS NULL OR \"AcceptedAt\" IS NOT NULL) AND CAST(\"ClientRequestId\" AS TEXT) <> '00000000-0000-0000-0000-000000000000' AND ((\"RecoveryLeaseToken\" IS NULL AND \"RecoveryLeaseExpiresAt\" IS NULL) OR (\"RecoveryLeaseToken\" IS NOT NULL AND \"RecoveryLeaseExpiresAt\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_InternalChatSendAttempts_InternalChatThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "InternalChatThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InternalChatSendAttemptFiles",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    StorageGeneration = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalChatSendAttemptFiles", x => new { x.AttemptId, x.Index });
                    table.CheckConstraint("CK_InternalChatSendAttemptFiles_Proof", "\"Index\" BETWEEN 0 AND 9 AND length(\"FileName\") BETWEEN 1 AND 260 AND length(\"ContentType\") BETWEEN 1 AND 160 AND length(\"Sha256\") = 64 AND \"SizeBytes\" BETWEEN 1 AND 20971520 AND ((\"StorageObjectId\" IS NULL AND \"StorageGeneration\" IS NULL) OR (\"StorageObjectId\" IS NOT NULL AND \"StorageGeneration\" IS NOT NULL AND \"StorageGeneration\" > 0))");
                    table.ForeignKey(
                        name: "FK_InternalChatSendAttemptFiles_InternalChatSendAttempts_Attem~",
                        column: x => x.AttemptId,
                        principalTable: "InternalChatSendAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InternalChatSendAttemptMentions",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalChatSendAttemptMentions", x => new { x.AttemptId, x.UserId });
                    table.CheckConstraint("CK_InternalChatSendAttemptMentions_User", "\"UserId\" > 0");
                    table.ForeignKey(
                        name: "FK_InternalChatSendAttemptMentions_InternalChatSendAttempts_At~",
                        column: x => x.AttemptId,
                        principalTable: "InternalChatSendAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatSendAttemptFiles_StorageObjectId",
                table: "InternalChatSendAttemptFiles",
                column: "StorageObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatSendAttempts_Recovery",
                table: "InternalChatSendAttempts",
                columns: new[] { "PublishedMessageId", "RecoveryNextAttemptAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatSendAttempts_ThreadId_ActorId_ClientRequestId",
                table: "InternalChatSendAttempts",
                columns: new[] { "ThreadId", "ActorId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatSendAttempts_ThreadId_ActorId_CreatedAt_Id",
                table: "InternalChatSendAttempts",
                columns: new[] { "ThreadId", "ActorId", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "InternalChatSendAttempts") THEN
                        RAISE EXCEPTION 'Cannot downgrade while frozen chat send attempts exist';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "InternalChatSendAttemptFiles");

            migrationBuilder.DropTable(
                name: "InternalChatSendAttemptMentions");

            migrationBuilder.DropTable(
                name: "InternalChatSendAttempts");
        }
    }
}
