using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectAuditMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "PowersOfAttorney",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "PowersOfAttorney",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "PowersOfAttorney",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "PowersOfAttorney",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "PowersOfAttorney",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "PowersOfAttorney",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "PowersOfAttorney",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "PowersOfAttorney",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "Payments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "Payments",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "Payments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "Payments",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Payments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "Payments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "Payments",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "MoneyOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "MoneyOperations",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "MoneyOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "MoneyOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "MoneyOperations",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "MoneyOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "MoneyOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "MoneyOperations",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "LegalEntities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "LegalEntities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "LegalEntities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "LegalEntities",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "LegalEntities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "LegalEntities",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "Invoices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "Invoices",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "Invoices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "Invoices",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "Invoices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "Invoices",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "InvoiceLineItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "InvoiceLineItems",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "InvoiceLineItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "InvoiceLineItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "InvoiceLineItems",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "InvoiceLineItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "InvoiceLineItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "InvoiceLineItems",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "ExternalContacts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "ExternalContacts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ExternalContacts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "ExternalContacts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "ExternalContacts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ExternalContacts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "ExternalContacts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "ExternalContacts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "Contracts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "Contracts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Contracts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "Contracts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "Contracts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Contracts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "Contracts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "Contracts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "Clients",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "Clients",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "Clients",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "ClientPassports",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "ClientPassports",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ClientPassports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "ClientPassports",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "ClientPassports",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ClientPassports",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "ClientPassports",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "ClientPassports",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "ClientCases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "ClientCases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ClientCases",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "ClientCases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "ClientCases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "ClientCases",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "AddressRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "AddressRecords",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "AddressRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "AddressRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "AddressRecords",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "AddressRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "AddressRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "AddressRecords",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "Acts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "Acts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Acts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedByUserId",
                table: "Acts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserName",
                table: "Acts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Acts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "Acts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserName",
                table: "Acts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PowerOfAttorney_DeletedAt_Audit",
                table: "PowersOfAttorney",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PowerOfAttorney_IsDeleted",
                table: "PowersOfAttorney",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_PowerOfAttorney_UpdatedAt_Audit",
                table: "PowersOfAttorney",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_DeletedAt_Audit",
                table: "Payments",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_IsDeleted",
                table: "Payments",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_UpdatedAt_Audit",
                table: "Payments",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperation_DeletedAt_Audit",
                table: "MoneyOperations",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperation_IsDeleted",
                table: "MoneyOperations",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOperation_UpdatedAt_Audit",
                table: "MoneyOperations",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntity_DeletedAt_Audit",
                table: "LegalEntities",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntity_IsDeleted",
                table: "LegalEntities",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntity_UpdatedAt_Audit",
                table: "LegalEntities",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_DeletedAt_Audit",
                table: "Invoices",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_IsDeleted",
                table: "Invoices",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_UpdatedAt_Audit",
                table: "Invoices",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLineItem_DeletedAt_Audit",
                table: "InvoiceLineItems",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLineItem_IsDeleted",
                table: "InvoiceLineItems",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLineItem_UpdatedAt_Audit",
                table: "InvoiceLineItems",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContact_DeletedAt_Audit",
                table: "ExternalContacts",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContact_IsDeleted",
                table: "ExternalContacts",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalContact_UpdatedAt_Audit",
                table: "ExternalContacts",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Contract_DeletedAt_Audit",
                table: "Contracts",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Contract_IsDeleted",
                table: "Contracts",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_Contract_UpdatedAt_Audit",
                table: "Contracts",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Client_DeletedAt_Audit",
                table: "Clients",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Client_IsDeleted",
                table: "Clients",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_Client_UpdatedAt_Audit",
                table: "Clients",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassport_DeletedAt_Audit",
                table: "ClientPassports",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassport_IsDeleted",
                table: "ClientPassports",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPassport_UpdatedAt_Audit",
                table: "ClientPassports",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCase_DeletedAt_Audit",
                table: "ClientCases",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCase_IsDeleted",
                table: "ClientCases",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCase_UpdatedAt_Audit",
                table: "ClientCases",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecord_DeletedAt_Audit",
                table: "AddressRecords",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecord_IsDeleted",
                table: "AddressRecords",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_AddressRecord_UpdatedAt_Audit",
                table: "AddressRecords",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Act_DeletedAt_Audit",
                table: "Acts",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Act_IsDeleted",
                table: "Acts",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_Act_UpdatedAt_Audit",
                table: "Acts",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PowerOfAttorney_DeletedAt_Audit",
                table: "PowersOfAttorney");

            migrationBuilder.DropIndex(
                name: "IX_PowerOfAttorney_IsDeleted",
                table: "PowersOfAttorney");

            migrationBuilder.DropIndex(
                name: "IX_PowerOfAttorney_UpdatedAt_Audit",
                table: "PowersOfAttorney");

            migrationBuilder.DropIndex(
                name: "IX_Payment_DeletedAt_Audit",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payment_IsDeleted",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payment_UpdatedAt_Audit",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOperation_DeletedAt_Audit",
                table: "MoneyOperations");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOperation_IsDeleted",
                table: "MoneyOperations");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOperation_UpdatedAt_Audit",
                table: "MoneyOperations");

            migrationBuilder.DropIndex(
                name: "IX_LegalEntity_DeletedAt_Audit",
                table: "LegalEntities");

            migrationBuilder.DropIndex(
                name: "IX_LegalEntity_IsDeleted",
                table: "LegalEntities");

            migrationBuilder.DropIndex(
                name: "IX_LegalEntity_UpdatedAt_Audit",
                table: "LegalEntities");

            migrationBuilder.DropIndex(
                name: "IX_Invoice_DeletedAt_Audit",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoice_IsDeleted",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoice_UpdatedAt_Audit",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLineItem_DeletedAt_Audit",
                table: "InvoiceLineItems");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLineItem_IsDeleted",
                table: "InvoiceLineItems");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLineItem_UpdatedAt_Audit",
                table: "InvoiceLineItems");

            migrationBuilder.DropIndex(
                name: "IX_ExternalContact_DeletedAt_Audit",
                table: "ExternalContacts");

            migrationBuilder.DropIndex(
                name: "IX_ExternalContact_IsDeleted",
                table: "ExternalContacts");

            migrationBuilder.DropIndex(
                name: "IX_ExternalContact_UpdatedAt_Audit",
                table: "ExternalContacts");

            migrationBuilder.DropIndex(
                name: "IX_Contract_DeletedAt_Audit",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Contract_IsDeleted",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Contract_UpdatedAt_Audit",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Client_DeletedAt_Audit",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Client_IsDeleted",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Client_UpdatedAt_Audit",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_ClientPassport_DeletedAt_Audit",
                table: "ClientPassports");

            migrationBuilder.DropIndex(
                name: "IX_ClientPassport_IsDeleted",
                table: "ClientPassports");

            migrationBuilder.DropIndex(
                name: "IX_ClientPassport_UpdatedAt_Audit",
                table: "ClientPassports");

            migrationBuilder.DropIndex(
                name: "IX_ClientCase_DeletedAt_Audit",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCase_IsDeleted",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCase_UpdatedAt_Audit",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_AddressRecord_DeletedAt_Audit",
                table: "AddressRecords");

            migrationBuilder.DropIndex(
                name: "IX_AddressRecord_IsDeleted",
                table: "AddressRecords");

            migrationBuilder.DropIndex(
                name: "IX_AddressRecord_UpdatedAt_Audit",
                table: "AddressRecords");

            migrationBuilder.DropIndex(
                name: "IX_Act_DeletedAt_Audit",
                table: "Acts");

            migrationBuilder.DropIndex(
                name: "IX_Act_IsDeleted",
                table: "Acts");

            migrationBuilder.DropIndex(
                name: "IX_Act_UpdatedAt_Audit",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "PowersOfAttorney");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "PowersOfAttorney");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "PowersOfAttorney");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "PowersOfAttorney");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "PowersOfAttorney");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "PowersOfAttorney");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "PowersOfAttorney");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "PowersOfAttorney");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "MoneyOperations");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "LegalEntities");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "InvoiceLineItems");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "InvoiceLineItems");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "InvoiceLineItems");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "InvoiceLineItems");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "InvoiceLineItems");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "InvoiceLineItems");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "InvoiceLineItems");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "InvoiceLineItems");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "ExternalContacts");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "ClientPassports");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "ClientCases");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "AddressRecords");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "AddressRecords");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "AddressRecords");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "AddressRecords");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "AddressRecords");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "AddressRecords");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "AddressRecords");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "AddressRecords");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "DeletedByUserName",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "Acts");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserName",
                table: "Acts");
        }
    }
}
