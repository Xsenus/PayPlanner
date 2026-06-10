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
    [MemberData(nameof(CreateContexts))]
    public void ClientDeletion_UsesExpectedDeleteBehavior(DbContext context)
    {
        using (context)
        {
            AssertDeleteBehavior<ClientCase, Client>(context, DeleteBehavior.Cascade, nameof(ClientCase.ClientId));
            AssertDeleteBehavior<ClientContract, Client>(context, DeleteBehavior.Cascade, nameof(ClientContract.ClientId));
            AssertDeleteBehavior<Payment, Client>(context, DeleteBehavior.SetNull, nameof(Payment.ClientId));
            AssertDeleteBehavior<Payment, Client>(context, DeleteBehavior.SetNull, nameof(Payment.ExpenseClientId));
        }
    }

    [Theory]
    [MemberData(nameof(CreateContexts))]
    public void CaseDeletion_CascadesCaseOwnedDocumentsAndKeepsLinkedPayments(DbContext context)
    {
        using (context)
        {
            AssertDeleteBehavior<ClientCaseDocument, ClientCase>(context, DeleteBehavior.Cascade, nameof(ClientCaseDocument.ClientCaseId));
            AssertDeleteBehavior<InvoiceClientCase, ClientCase>(context, DeleteBehavior.Cascade, nameof(InvoiceClientCase.ClientCaseId));
            AssertDeleteBehavior<Payment, ClientCase>(context, DeleteBehavior.SetNull, nameof(Payment.ClientCaseId));
        }
    }

    [Theory]
    [MemberData(nameof(CreateContexts))]
    public void PaymentDeletion_CascadesPaymentOwnedRowsAndKeepsDocumentsNullable(DbContext context)
    {
        using (context)
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
    [MemberData(nameof(CreateContexts))]
    public void DocumentDeletion_UsesExpectedDeleteBehavior(DbContext context)
    {
        using (context)
        {
            AssertDeleteBehavior<Payment, Invoice>(context, DeleteBehavior.SetNull, nameof(Payment.InvoiceId));
            AssertDeleteBehavior<CashOrder, Invoice>(context, DeleteBehavior.SetNull, nameof(CashOrder.InvoiceId));
            AssertDeleteBehavior<ClientBalanceTransaction, Invoice>(context, DeleteBehavior.SetNull, nameof(ClientBalanceTransaction.InvoiceId));
            AssertDeleteBehavior<ActLineItem, Act>(context, DeleteBehavior.Cascade, nameof(ActLineItem.ActId));
        }
    }

    [Theory]
    [MemberData(nameof(CreateContexts))]
    public void ContractDeletion_CascadesJoinRowsAndKeepsPaymentsNullable(DbContext context)
    {
        using (context)
        {
            AssertDeleteBehavior<ClientContract, Contract>(context, DeleteBehavior.Cascade, nameof(ClientContract.ContractId));
            AssertDeleteBehavior<ContractAct, Contract>(context, DeleteBehavior.Cascade, nameof(ContractAct.ContractId));
            AssertDeleteBehavior<ContractInvoice, Contract>(context, DeleteBehavior.Cascade, nameof(ContractInvoice.ContractId));
            AssertDeleteBehavior<ContractPayment, Contract>(context, DeleteBehavior.Cascade, nameof(ContractPayment.ContractId));
            AssertDeleteBehavior<Payment, Contract>(context, DeleteBehavior.SetNull, nameof(Payment.ContractId));
        }
    }

    [Theory]
    [MemberData(nameof(CreateContexts))]
    public void AttachmentDeletion_UsesExpectedDeleteBehavior(DbContext context)
    {
        using (context)
        {
            AssertDeleteBehavior<TaskJournalFile, TaskJournalItem>(context, DeleteBehavior.Cascade, nameof(TaskJournalFile.TaskJournalItemId));
            AssertDeleteBehavior<ClientCaseProceduralDeadline, ClientCaseDocument>(context, DeleteBehavior.SetNull, nameof(ClientCaseProceduralDeadline.ClientCaseDocumentId));
            AssertDeleteBehavior<ClientCaseCourtEvent, ClientCaseDocument>(context, DeleteBehavior.SetNull, nameof(ClientCaseCourtEvent.ClientCaseDocumentId));
        }
    }

    public static TheoryData<DbContext> CreateContexts()
    {
        var sqliteConnection = new SqliteConnection("Data Source=:memory:");
        sqliteConnection.Open();

        return new TheoryData<DbContext>
        {
            new PaymentContext(
                new DbContextOptionsBuilder<PaymentContext>()
                    .UseSqlite(sqliteConnection)
                    .Options,
                useObjectAuditModel: false),
            new PostgreSqlPaymentContext(
                new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                    .UseNpgsql("Host=localhost;Database=payplanner_delete_behavior_model;Username=postgres;Password=postgres")
                    .Options,
                new HttpContextAccessor(),
                useObjectAuditModel: false)
        };
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
