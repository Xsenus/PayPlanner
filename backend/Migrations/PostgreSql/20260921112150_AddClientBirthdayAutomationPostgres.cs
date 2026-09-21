using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddClientBirthdayAutomationPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BirthDate",
                table: "Clients",
                type: "date",
                nullable: true);

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    client_row record;
                    birth_date_text text;
                BEGIN
                    FOR client_row IN
                        SELECT "Id", "Notes"
                        FROM "Clients"
                        WHERE "Notes" LIKE '__PP_CLIENT_PROFILE_V2__:%'
                    LOOP
                        BEGIN
                            birth_date_text := (substring(client_row."Notes" from 26)::jsonb ->> 'birthDate');
                            IF birth_date_text ~ '^\d{4}-\d{2}-\d{2}$' THEN
                                UPDATE "Clients"
                                SET "BirthDate" = birth_date_text::date
                                WHERE "Id" = client_row."Id";
                            END IF;
                        EXCEPTION WHEN others THEN
                            NULL;
                        END;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Clients_BirthDate",
                table: "Clients",
                column: "BirthDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Clients_BirthDate",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "BirthDate",
                table: "Clients");
        }
    }
}
