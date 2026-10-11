using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientStatusSystemKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SystemKey",
                table: "ClientStatuses",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientStatuses_SystemKey",
                table: "ClientStatuses",
                column: "SystemKey");

            migrationBuilder.Sql("UPDATE \"ClientStatuses\" SET \"SystemKey\" = 'client' WHERE \"Name\" = 'Клиент' AND (\"SystemKey\" IS NULL OR \"SystemKey\" = '')");
            migrationBuilder.Sql("UPDATE \"ClientStatuses\" SET \"SystemKey\" = 'supplier' WHERE \"Name\" = 'Поставщик' AND (\"SystemKey\" IS NULL OR \"SystemKey\" = '')");
            migrationBuilder.Sql("UPDATE \"ClientStatuses\" SET \"SystemKey\" = 'creditor' WHERE \"Name\" = 'Кредитор' AND (\"SystemKey\" IS NULL OR \"SystemKey\" = '')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientStatuses_SystemKey",
                table: "ClientStatuses");

            migrationBuilder.DropColumn(
                name: "SystemKey",
                table: "ClientStatuses");
        }
    }
}
