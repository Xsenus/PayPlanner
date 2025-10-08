using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class ExtendUserProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MiddleName",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateOfBirth",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoUrl",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhatsApp",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Telegram",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Instagram",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Messenger",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Viber",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEmployee",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmploymentStartDate",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmploymentEndDate",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "FirstName", table: "Users");
            migrationBuilder.DropColumn(name: "LastName", table: "Users");
            migrationBuilder.DropColumn(name: "MiddleName", table: "Users");
            migrationBuilder.DropColumn(name: "DateOfBirth", table: "Users");
            migrationBuilder.DropColumn(name: "PhotoUrl", table: "Users");
            migrationBuilder.DropColumn(name: "PhoneNumber", table: "Users");
            migrationBuilder.DropColumn(name: "WhatsApp", table: "Users");
            migrationBuilder.DropColumn(name: "Telegram", table: "Users");
            migrationBuilder.DropColumn(name: "Instagram", table: "Users");
            migrationBuilder.DropColumn(name: "Messenger", table: "Users");
            migrationBuilder.DropColumn(name: "Viber", table: "Users");
            migrationBuilder.DropColumn(name: "IsEmployee", table: "Users");
            migrationBuilder.DropColumn(name: "EmploymentStartDate", table: "Users");
            migrationBuilder.DropColumn(name: "EmploymentEndDate", table: "Users");
        }
    }
}
