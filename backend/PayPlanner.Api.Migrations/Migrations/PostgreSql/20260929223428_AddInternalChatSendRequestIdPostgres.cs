using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddInternalChatSendRequestIdPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientRequestId",
                table: "InternalChatMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalChatMessages_SendRequest",
                table: "InternalChatMessages",
                columns: new[] { "ThreadId", "AuthorUserId", "ClientRequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InternalChatMessages_SendRequest",
                table: "InternalChatMessages");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                table: "InternalChatMessages");
        }
    }
}
