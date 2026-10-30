using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class FenceCancelledBalanceApplicationsPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BalanceApplicationReceipts_Outcome",
                table: "BalanceApplicationReceipts");

            migrationBuilder.AlterColumn<string>(
                name: "CommandSha256",
                table: "BalanceApplicationReceipts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "BalanceApplicationReceipts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Committed");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BalanceApplicationReceipts_Outcome",
                table: "BalanceApplicationReceipts",
                sql: "\"ActorUserId\" > 0 AND \"PaymentId\" > 0 AND ((\"State\" = 'Committed' AND \"AppliedAmount\" > 0 AND \"CommandSha256\" IS NOT NULL AND length(\"CommandSha256\") = 64) OR (\"State\" = 'Cancelled' AND \"AppliedAmount\" = 0 AND \"Balance\" = 0 AND \"CommandSha256\" IS NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BalanceApplicationReceipts_Outcome",
                table: "BalanceApplicationReceipts");

            migrationBuilder.DropColumn(
                name: "State",
                table: "BalanceApplicationReceipts");

            migrationBuilder.AlterColumn<string>(
                name: "CommandSha256",
                table: "BalanceApplicationReceipts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BalanceApplicationReceipts_Outcome",
                table: "BalanceApplicationReceipts",
                sql: "\"ActorUserId\" > 0 AND \"PaymentId\" > 0 AND \"AppliedAmount\" > 0 AND length(\"CommandSha256\") = 64");
        }
    }
}
