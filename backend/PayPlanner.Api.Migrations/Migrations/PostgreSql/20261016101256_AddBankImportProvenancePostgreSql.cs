using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddBankImportProvenancePostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MoneyAccounts_Source_Name",
                table: "MoneyAccounts");

            migrationBuilder.AddColumn<string>(
                name: "SourceMovementKey",
                table: "MoneyOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankBik",
                table: "MoneyAccounts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BankConnectionAccountId",
                table: "MoneyAccounts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "MoneyAccounts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BankStatementOperationId",
                table: "BankStatementImportRows",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "BankStatementImportRows",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalOperationId",
                table: "BankStatementImportRows",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBooked",
                table: "BankStatementImportRows",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MovementKey",
                table: "BankStatementImportRows",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginRowId",
                table: "BankStatementImportRows",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RowIdentityKey",
                table: "BankStatementImportRows",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BankConnectionAccountId",
                table: "BankStatementImportBatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "BankStatementImportBatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "BankStatementImportBatches",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImportedByUserId",
                table: "BankStatementImportBatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MoneyAccountId",
                table: "BankStatementImportBatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageKey",
                table: "BankStatementImportBatches",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParentBatchId",
                table: "BankStatementImportBatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PeriodFrom",
                table: "BankStatementImportBatches",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PeriodTo",
                table: "BankStatementImportBatches",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResponsibleUserId",
                table: "BankStatementImportBatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "BankStatementImportBatches",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceResultJson",
                table: "BankStatementImportBatches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Trigger",
                table: "BankStatementImportBatches",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperations_SourceMovementKey",
                table: "MoneyOperations",
                column: "SourceMovementKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoneyAccounts_BankConnectionAccountId",
                table: "MoneyAccounts",
                column: "BankConnectionAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoneyAccounts_Company_Source_Name",
                table: "MoneyAccounts",
                columns: new[] { "CompanyId", "Source", "Name" },
                unique: true,
                filter: "\"CompanyId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyAccounts_Source_Name",
                table: "MoneyAccounts",
                columns: new[] { "Source", "Name" },
                unique: true,
                filter: "\"CompanyId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportRows_BankStatementOperationId",
                table: "BankStatementImportRows",
                column: "BankStatementOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportRows_Batch_RowIdentity",
                table: "BankStatementImportRows",
                columns: new[] { "BatchId", "RowIdentityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportRows_MovementKey",
                table: "BankStatementImportRows",
                column: "MovementKey");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportRows_OriginRowId",
                table: "BankStatementImportRows",
                column: "OriginRowId");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportBatches_BankConnectionAccountId",
                table: "BankStatementImportBatches",
                column: "BankConnectionAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportBatches_Company_Created_Id",
                table: "BankStatementImportBatches",
                columns: new[] { "CompanyId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportBatches_MoneyAccountId",
                table: "BankStatementImportBatches",
                column: "MoneyAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportBatches_PackageKey",
                table: "BankStatementImportBatches",
                column: "PackageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportBatches_ParentBatchId",
                table: "BankStatementImportBatches",
                column: "ParentBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementImportBatches_Responsible_Created_Id",
                table: "BankStatementImportBatches",
                columns: new[] { "ResponsibleUserId", "CreatedAt", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_BankStatementImportBatches_BankConnectionAccounts_BankConne~",
                table: "BankStatementImportBatches",
                column: "BankConnectionAccountId",
                principalTable: "BankConnectionAccounts",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BankStatementImportBatches_BankStatementImportBatches_Paren~",
                table: "BankStatementImportBatches",
                column: "ParentBatchId",
                principalTable: "BankStatementImportBatches",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BankStatementImportBatches_LegalEntities_CompanyId",
                table: "BankStatementImportBatches",
                column: "CompanyId",
                principalTable: "LegalEntities",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BankStatementImportBatches_MoneyAccounts_MoneyAccountId",
                table: "BankStatementImportBatches",
                column: "MoneyAccountId",
                principalTable: "MoneyAccounts",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BankStatementImportRows_BankStatementImportRows_OriginRowId",
                table: "BankStatementImportRows",
                column: "OriginRowId",
                principalTable: "BankStatementImportRows",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BankStatementImportRows_BankStatementOperations_BankStateme~",
                table: "BankStatementImportRows",
                column: "BankStatementOperationId",
                principalTable: "BankStatementOperations",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MoneyAccounts_BankConnectionAccounts_BankConnectionAccountId",
                table: "MoneyAccounts",
                column: "BankConnectionAccountId",
                principalTable: "BankConnectionAccounts",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MoneyAccounts_LegalEntities_CompanyId",
                table: "MoneyAccounts",
                column: "CompanyId",
                principalTable: "LegalEntities",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankStatementImportBatches_BankConnectionAccounts_BankConne~",
                table: "BankStatementImportBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_BankStatementImportBatches_BankStatementImportBatches_Paren~",
                table: "BankStatementImportBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_BankStatementImportBatches_LegalEntities_CompanyId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_BankStatementImportBatches_MoneyAccounts_MoneyAccountId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_BankStatementImportRows_BankStatementImportRows_OriginRowId",
                table: "BankStatementImportRows");

            migrationBuilder.DropForeignKey(
                name: "FK_BankStatementImportRows_BankStatementOperations_BankStateme~",
                table: "BankStatementImportRows");

            migrationBuilder.DropForeignKey(
                name: "FK_MoneyAccounts_BankConnectionAccounts_BankConnectionAccountId",
                table: "MoneyAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_MoneyAccounts_LegalEntities_CompanyId",
                table: "MoneyAccounts");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOperations_SourceMovementKey",
                table: "MoneyOperations");

            migrationBuilder.DropIndex(
                name: "IX_MoneyAccounts_BankConnectionAccountId",
                table: "MoneyAccounts");

            migrationBuilder.DropIndex(
                name: "IX_MoneyAccounts_Company_Source_Name",
                table: "MoneyAccounts");

            migrationBuilder.DropIndex(
                name: "IX_MoneyAccounts_Source_Name",
                table: "MoneyAccounts");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementImportRows_BankStatementOperationId",
                table: "BankStatementImportRows");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementImportRows_Batch_RowIdentity",
                table: "BankStatementImportRows");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementImportRows_MovementKey",
                table: "BankStatementImportRows");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementImportRows_OriginRowId",
                table: "BankStatementImportRows");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementImportBatches_BankConnectionAccountId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementImportBatches_Company_Created_Id",
                table: "BankStatementImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementImportBatches_MoneyAccountId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementImportBatches_PackageKey",
                table: "BankStatementImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementImportBatches_ParentBatchId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_BankStatementImportBatches_Responsible_Created_Id",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "SourceMovementKey",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "BankBik",
                table: "MoneyAccounts");

            migrationBuilder.DropColumn(
                name: "BankConnectionAccountId",
                table: "MoneyAccounts");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "MoneyAccounts");

            migrationBuilder.DropColumn(
                name: "BankStatementOperationId",
                table: "BankStatementImportRows");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "BankStatementImportRows");

            migrationBuilder.DropColumn(
                name: "ExternalOperationId",
                table: "BankStatementImportRows");

            migrationBuilder.DropColumn(
                name: "IsBooked",
                table: "BankStatementImportRows");

            migrationBuilder.DropColumn(
                name: "MovementKey",
                table: "BankStatementImportRows");

            migrationBuilder.DropColumn(
                name: "OriginRowId",
                table: "BankStatementImportRows");

            migrationBuilder.DropColumn(
                name: "RowIdentityKey",
                table: "BankStatementImportRows");

            migrationBuilder.DropColumn(
                name: "BankConnectionAccountId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "ImportedByUserId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "MoneyAccountId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "PackageKey",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "ParentBatchId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "PeriodFrom",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "PeriodTo",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "ResponsibleUserId",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "SourceResultJson",
                table: "BankStatementImportBatches");

            migrationBuilder.DropColumn(
                name: "Trigger",
                table: "BankStatementImportBatches");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyAccounts_Source_Name",
                table: "MoneyAccounts",
                columns: new[] { "Source", "Name" },
                unique: true);
        }
    }
}
