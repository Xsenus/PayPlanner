using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddInternalChatPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InternalChatThreads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TaskJournalItemId = table.Column<int>(type: "integer", nullable: true),
                    DirectKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalChatThreads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InternalChatThreads_TaskJournalItems_TaskJournalItemId",
                        column: x => x.TaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InternalChatThreads_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "InternalChatMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ThreadId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "member"),
                    NotificationMode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "all"),
                    LastReadMessageId = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    JoinedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalChatMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InternalChatMembers_InternalChatThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "InternalChatThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InternalChatMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InternalChatMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ThreadId = table.Column<int>(type: "integer", nullable: false),
                    AuthorUserId = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "user"),
                    Text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ReplyToMessageId = table.Column<int>(type: "integer", nullable: true),
                    CreatedTaskJournalItemId = table.Column<int>(type: "integer", nullable: true),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false),
                    PinnedByUserId = table.Column<int>(type: "integer", nullable: true),
                    PinnedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    EditedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InternalChatMessages_InternalChatMessages_ReplyToMessageId",
                        column: x => x.ReplyToMessageId,
                        principalTable: "InternalChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InternalChatMessages_InternalChatThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "InternalChatThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InternalChatMessages_TaskJournalItems_CreatedTaskJournalIte~",
                        column: x => x.CreatedTaskJournalItemId,
                        principalTable: "TaskJournalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InternalChatMessages_Users_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InternalChatMessages_Users_PinnedByUserId",
                        column: x => x.PinnedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "InternalChatAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    StoredFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    UploadedByUserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalChatAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InternalChatAttachments_InternalChatMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "InternalChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InternalChatAttachments_Users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InternalChatMentions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalChatMentions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InternalChatMentions_InternalChatMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "InternalChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InternalChatMentions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InternalChatReactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Emoji = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalChatReactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InternalChatReactions_InternalChatMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "InternalChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InternalChatReactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatAttachments_MessageId",
                table: "InternalChatAttachments",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatAttachments_UploadedByUserId",
                table: "InternalChatAttachments",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMembers_Thread_User",
                table: "InternalChatMembers",
                columns: new[] { "ThreadId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMembers_User_Active_Thread",
                table: "InternalChatMembers",
                columns: new[] { "UserId", "IsActive", "ThreadId" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMentions_Message_User",
                table: "InternalChatMentions",
                columns: new[] { "MessageId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMentions_User_Message",
                table: "InternalChatMentions",
                columns: new[] { "UserId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMessages_AuthorUserId",
                table: "InternalChatMessages",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMessages_CreatedTaskJournalItemId",
                table: "InternalChatMessages",
                column: "CreatedTaskJournalItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMessages_PinnedByUserId",
                table: "InternalChatMessages",
                column: "PinnedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMessages_ReplyToMessageId",
                table: "InternalChatMessages",
                column: "ReplyToMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMessages_Thread_Id",
                table: "InternalChatMessages",
                columns: new[] { "ThreadId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMessages_Thread_Pinned",
                table: "InternalChatMessages",
                columns: new[] { "ThreadId", "IsPinned", "PinnedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatReactions_Message_User_Emoji",
                table: "InternalChatReactions",
                columns: new[] { "MessageId", "UserId", "Emoji" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatReactions_UserId",
                table: "InternalChatReactions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatThreads_Archived_LastMessageAt",
                table: "InternalChatThreads",
                columns: new[] { "IsArchived", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatThreads_CreatedByUserId",
                table: "InternalChatThreads",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatThreads_DirectKey",
                table: "InternalChatThreads",
                column: "DirectKey",
                unique: true,
                filter: "\"DirectKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatThreads_TaskJournalItemId",
                table: "InternalChatThreads",
                column: "TaskJournalItemId",
                unique: true,
                filter: "\"TaskJournalItemId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InternalChatAttachments");

            migrationBuilder.DropTable(
                name: "InternalChatMembers");

            migrationBuilder.DropTable(
                name: "InternalChatMentions");

            migrationBuilder.DropTable(
                name: "InternalChatReactions");

            migrationBuilder.DropTable(
                name: "InternalChatMessages");

            migrationBuilder.DropTable(
                name: "InternalChatThreads");
        }
    }
}
