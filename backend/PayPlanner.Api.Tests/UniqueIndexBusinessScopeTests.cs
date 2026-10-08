using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public class UniqueIndexBusinessScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusinessDocumentNumbers_HaveExpectedUniquenessScope(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            AssertIndex<Invoice>(context, isUnique: false, nameof(Invoice.Number), nameof(Invoice.Date));
            AssertIndex<Act>(context, isUnique: false, nameof(Act.Number));
            AssertIndex<Contract>(context, isUnique: false, nameof(Contract.Number));
            AssertIndex<CashOrder>(context, isUnique: true, nameof(CashOrder.Number));
            AssertIndex<CashInventory>(context, isUnique: true, nameof(CashInventory.Number));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DictionaryCodes_HaveExpectedGlobalUniqueness(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            AssertIndex<OkinDictionaryItem>(context, isUnique: true, nameof(OkinDictionaryItem.Code));
            AssertIndex<OksoDictionaryItem>(context, isUnique: true, nameof(OksoDictionaryItem.Code));
            AssertIndex<OkpdtrDictionaryItem>(context, isUnique: true, nameof(OkpdtrDictionaryItem.Code));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoftDeletedUniqueBusinessDocuments_UseGlobalUniquenessWithoutFilter(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            AssertGlobalUniqueIndex<CashOrder>(context, nameof(CashOrder.Number));
            AssertGlobalUniqueIndex<CashInventory>(context, nameof(CashInventory.Number));
        }
    }

    private static DbContext CreateContext(bool postgres)
    {
        if (postgres)
            return new PostgreSqlPaymentContext(
                new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                    .UseNpgsql("Host=localhost;Database=payplanner_unique_index_model;Username=postgres;Password=postgres")
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

    private static void AssertIndex<TEntity>(
        DbContext context,
        bool isUnique,
        params string[] propertyNames)
    {
        var index = FindIndex<TEntity>(context, propertyNames);
        Assert.Equal(isUnique, index.IsUnique);
    }

    private static void AssertGlobalUniqueIndex<TEntity>(DbContext context, params string[] propertyNames)
    {
        var index = FindIndex<TEntity>(context, propertyNames);
        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
    }

    private static IReadOnlyIndex FindIndex<TEntity>(DbContext context, params string[] propertyNames)
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"Entity {typeof(TEntity).Name} was not found.");

        var index = entityType.GetIndexes()
            .SingleOrDefault(candidate => candidate.Properties.Select(property => property.Name).SequenceEqual(propertyNames));

        return index ?? throw new InvalidOperationException(
            $"Index {typeof(TEntity).Name}({string.Join(", ", propertyNames)}) was not found.");
    }
}
