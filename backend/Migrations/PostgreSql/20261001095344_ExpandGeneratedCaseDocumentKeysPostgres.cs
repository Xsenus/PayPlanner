using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class ExpandGeneratedCaseDocumentKeysPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DocumentKey",
                table: "ClientCaseDocuments",
                type: "character varying(73)",
                maxLength: 73,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$
                DECLARE payload text; document_key text;
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ClientCaseDocuments" WHERE char_length("DocumentKey") > 64) THEN
                        RAISE EXCEPTION 'Cannot downgrade document keys while long identities remain.' USING ERRCODE = '23514';
                    END IF;
                    FOR payload IN
                        SELECT "ProductContextJson" FROM "StorageCapacityReservations"
                            WHERE "Status" = 'pending' AND "ProductContextJson" IS NOT NULL
                                AND "ProductContextJson" LIKE '%caseId%'
                        UNION ALL
                        SELECT op."PayloadJson" FROM "StorageOperations" AS op
                            JOIN "StorageObjects" AS obj ON obj."Id" = op."StorageObjectId"
                            WHERE op."OperationType" = 'commit' AND obj."DataClass" = 'case-document'
                                AND op."PayloadJson" IS NOT NULL
                    LOOP
                        BEGIN
                            document_key := payload::jsonb ->> 'documentKey';
                        EXCEPTION WHEN invalid_text_representation THEN
                            RAISE EXCEPTION 'Cannot downgrade document keys with unproven publication metadata.' USING ERRCODE = '23514';
                        END;
                        IF char_length(document_key) > 64 THEN
                            RAISE EXCEPTION 'Cannot downgrade document keys while long publication identities remain.' USING ERRCODE = '23514';
                        END IF;
                    END LOOP;
                END $guard$;
                """);
            migrationBuilder.AlterColumn<string>(
                name: "DocumentKey",
                table: "ClientCaseDocuments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(73)",
                oldMaxLength: 73);
        }
    }
}
