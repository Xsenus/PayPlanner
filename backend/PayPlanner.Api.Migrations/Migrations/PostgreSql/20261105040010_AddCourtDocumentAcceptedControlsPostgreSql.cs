using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCourtDocumentAcceptedControlsPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcceptedControlsJson",
                table: "CourtDocumentSources",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "CourtDocumentSources") THEN
                        RAISE EXCEPTION 'Accepted court sources require their control-proof schema' USING ERRCODE = '23514';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "AcceptedControlsJson",
                table: "CourtDocumentSources");
        }
    }
}
