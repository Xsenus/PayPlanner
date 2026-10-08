using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DeleteBehaviorContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClientDeletion_UsesExpectedDeleteBehavior(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            AssertDeleteBehavior<ClientCase, Client>(context, DeleteBehavior.Cascade, nameof(ClientCase.ClientId));
            AssertDeleteBehavior<ClientContract, Client>(context, DeleteBehavior.Cascade, nameof(ClientContract.ClientId));
            AssertDeleteBehavior<Payment, Client>(context, DeleteBehavior.SetNull, nameof(Payment.ClientId));
            AssertDeleteBehavior<Payment, Client>(context, DeleteBehavior.SetNull, nameof(Payment.ExpenseClientId));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaseDeletion_CascadesCaseOwnedDocumentsAndKeepsLinkedPayments(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            AssertDeleteBehavior<ClientCaseDocument, ClientCase>(context, DeleteBehavior.Cascade, nameof(ClientCaseDocument.ClientCaseId));
            AssertDeleteBehavior<InvoiceClientCase, ClientCase>(context, DeleteBehavior.Cascade, nameof(InvoiceClientCase.ClientCaseId));
            AssertDeleteBehavior<Payment, ClientCase>(context, DeleteBehavior.SetNull, nameof(Payment.ClientCaseId));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaymentDeletion_CascadesPaymentOwnedRowsAndKeepsDocumentsNullable(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            AssertDeleteBehavior<PaymentResponsible, Payment>(context, DeleteBehavior.Cascade, nameof(PaymentResponsible.PaymentId));
            AssertDeleteBehavior<InvoiceLineItem, Payment>(context, DeleteBehavior.Cascade, nameof(InvoiceLineItem.PaymentId));
            AssertDeleteBehavior<ContractPayment, Payment>(context, DeleteBehavior.Cascade, nameof(ContractPayment.PaymentId));
            AssertDeleteBehavior<InvoiceClientCase, Payment>(context, DeleteBehavior.Cascade, nameof(InvoiceClientCase.PaymentId));
            AssertDeleteBehavior<CashOrder, Payment>(context, DeleteBehavior.SetNull, nameof(CashOrder.PaymentId));
            AssertDeleteBehavior<ClientBalanceTransaction, Payment>(context, DeleteBehavior.SetNull, nameof(ClientBalanceTransaction.PaymentId));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DocumentDeletion_UsesExpectedDeleteBehavior(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            AssertDeleteBehavior<Payment, Invoice>(context, DeleteBehavior.SetNull, nameof(Payment.InvoiceId));
            AssertDeleteBehavior<CashOrder, Invoice>(context, DeleteBehavior.SetNull, nameof(CashOrder.InvoiceId));
            AssertDeleteBehavior<ClientBalanceTransaction, Invoice>(context, DeleteBehavior.SetNull, nameof(ClientBalanceTransaction.InvoiceId));
            AssertDeleteBehavior<ActLineItem, Act>(context, DeleteBehavior.Cascade, nameof(ActLineItem.ActId));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContractDeletion_CascadesJoinRowsAndKeepsPaymentsNullable(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            AssertDeleteBehavior<ClientContract, Contract>(context, DeleteBehavior.Cascade, nameof(ClientContract.ContractId));
            AssertDeleteBehavior<ContractAct, Contract>(context, DeleteBehavior.Cascade, nameof(ContractAct.ContractId));
            AssertDeleteBehavior<ContractInvoice, Contract>(context, DeleteBehavior.Cascade, nameof(ContractInvoice.ContractId));
            AssertDeleteBehavior<ContractPayment, Contract>(context, DeleteBehavior.Cascade, nameof(ContractPayment.ContractId));
            AssertDeleteBehavior<Payment, Contract>(context, DeleteBehavior.SetNull, nameof(Payment.ContractId));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttachmentDeletion_UsesExpectedDeleteBehavior(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            AssertDeleteBehavior<TaskJournalFile, TaskJournalItem>(context, DeleteBehavior.Cascade, nameof(TaskJournalFile.TaskJournalItemId));
            AssertDeleteBehavior<ClientCaseProceduralDeadline, ClientCaseDocument>(context, DeleteBehavior.SetNull, nameof(ClientCaseProceduralDeadline.ClientCaseDocumentId));
            AssertDeleteBehavior<ClientCaseCourtEvent, ClientCaseDocument>(context, DeleteBehavior.SetNull, nameof(ClientCaseCourtEvent.ClientCaseDocumentId));
        }
    }

    private static DbContext CreateContext(bool postgres)
    {
        if (postgres)
            return new PostgreSqlPaymentContext(
                new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                    .UseNpgsql("Host=localhost;Database=payplanner_delete_behavior_model;Username=postgres;Password=postgres")
                    .Options,
                new HttpContextAccessor(),
                useObjectAuditModel: false);

        var sqliteConnection = new SqliteConnection("Data Source=:memory:");
        try
        {
            sqliteConnection.Open();
            return new PaymentContext(
                new DbContextOptionsBuilder<PaymentContext>()
                    .UseSqlite(sqliteConnection, contextOwnsConnection: true)
                    .Options,
                useObjectAuditModel: false);
        }
        catch
        {
            sqliteConnection.Dispose();
            throw;
        }
    }

    private static void AssertDeleteBehavior<TDependent, TPrincipal>(
        DbContext context,
        DeleteBehavior expected,
        params string[] foreignKeyPropertyNames)
    {
        var dependentType = context.Model.FindEntityType(typeof(TDependent))
            ?? throw new InvalidOperationException($"Entity {typeof(TDependent).Name} was not found.");

        var foreignKey = dependentType.GetForeignKeys()
            .Single(candidate =>
                candidate.PrincipalEntityType.ClrType == typeof(TPrincipal)
                && candidate.Properties.Select(property => property.Name).SequenceEqual(foreignKeyPropertyNames));

        Assert.Equal(expected, foreignKey.DeleteBehavior);
    }
}
