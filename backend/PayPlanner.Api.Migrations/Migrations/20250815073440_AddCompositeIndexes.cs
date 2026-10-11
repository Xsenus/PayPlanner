using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_ClientCaseId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ClientId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_ClientId",
                table: "ClientCases");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentStatuses_IsActive",
                table: "PaymentStatuses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentStatuses_IsActive_Name",
                table: "PaymentStatuses",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentStatuses_Name",
                table: "PaymentStatuses",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSources_IsActive",
                table: "PaymentSources",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSources_IsActive_Name",
                table: "PaymentSources",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSources_Name",
                table: "PaymentSources",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ClientCaseId_Date",
                table: "Payments",
                columns: new[] { "ClientCaseId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ClientId_Date",
                table: "Payments",
                columns: new[] { "ClientId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CreatedAt",
                table: "Payments",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Date",
                table: "Payments",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IsPaid_Date",
                table: "Payments",
                columns: new[] { "IsPaid", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IsPaid_Status",
                table: "Payments",
                columns: new[] { "IsPaid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTypes_IsActive",
                table: "IncomeTypes",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTypes_IsActive_Name",
                table: "IncomeTypes",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTypes_Name",
                table: "IncomeTypes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_DealTypes_IsActive",
                table: "DealTypes",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_DealTypes_IsActive_Name",
                table: "DealTypes",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_DealTypes_Name",
                table: "DealTypes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_CreatedAt",
                table: "Clients",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Email",
                table: "Clients",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_IsActive",
                table: "Clients",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_IsActive_Name",
                table: "Clients",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Name",
                table: "Clients",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_ClientId_CreatedAt",
                table: "ClientCases",
                columns: new[] { "ClientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_ClientId_Status",
                table: "ClientCases",
                columns: new[] { "ClientId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_CreatedAt",
                table: "ClientCases",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_Status",
                table: "ClientCases",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentStatuses_IsActive",
                table: "PaymentStatuses");

            migrationBuilder.DropIndex(
                name: "IX_PaymentStatuses_IsActive_Name",
                table: "PaymentStatuses");

            migrationBuilder.DropIndex(
                name: "IX_PaymentStatuses_Name",
                table: "PaymentStatuses");

            migrationBuilder.DropIndex(
                name: "IX_PaymentSources_IsActive",
                table: "PaymentSources");

            migrationBuilder.DropIndex(
                name: "IX_PaymentSources_IsActive_Name",
                table: "PaymentSources");

            migrationBuilder.DropIndex(
                name: "IX_PaymentSources_Name",
                table: "PaymentSources");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ClientCaseId_Date",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ClientId_Date",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_CreatedAt",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Date",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_IsPaid_Date",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_IsPaid_Status",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_IncomeTypes_IsActive",
                table: "IncomeTypes");

            migrationBuilder.DropIndex(
                name: "IX_IncomeTypes_IsActive_Name",
                table: "IncomeTypes");

            migrationBuilder.DropIndex(
                name: "IX_IncomeTypes_Name",
                table: "IncomeTypes");

            migrationBuilder.DropIndex(
                name: "IX_DealTypes_IsActive",
                table: "DealTypes");

            migrationBuilder.DropIndex(
                name: "IX_DealTypes_IsActive_Name",
                table: "DealTypes");

            migrationBuilder.DropIndex(
                name: "IX_DealTypes_Name",
                table: "DealTypes");

            migrationBuilder.DropIndex(
                name: "IX_Clients_CreatedAt",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Clients_Email",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Clients_IsActive",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Clients_IsActive_Name",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Clients_Name",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_ClientId_CreatedAt",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_ClientId_Status",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_CreatedAt",
                table: "ClientCases");

            migrationBuilder.DropIndex(
                name: "IX_ClientCases_Status",
                table: "ClientCases");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ClientCaseId",
                table: "Payments",
                column: "ClientCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ClientId",
                table: "Payments",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCases_ClientId",
                table: "ClientCases",
                column: "ClientId");
        }
    }
}
