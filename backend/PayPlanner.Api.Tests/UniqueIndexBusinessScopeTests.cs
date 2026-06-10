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
    [MemberData(nameof(CreateContexts))]
    public void BusinessDocumentNumbers_HaveExpectedUniquenessScope(DbContext context)
    {
        using (context)
        {
            AssertIndex<Invoice>(context, isUnique: false, nameof(Invoice.Number), nameof(Invoice.Date));
            AssertIndex<Act>(context, isUnique: false, nameof(Act.Number));
            AssertIndex<Contract>(context, isUnique: false, nameof(Contract.Number));
            AssertIndex<CashOrder>(context, isUnique: true, nameof(CashOrder.Number));
            AssertIndex<CashInventory>(context, isUnique: true, nameof(CashInventory.Number));
        }
    }

    [Theory]
    [MemberData(nameof(CreateContexts))]
    public void DictionaryCodes_HaveExpectedGlobalUniqueness(DbContext context)
    {
        using (context)
        {
            AssertIndex<OkinDictionaryItem>(context, isUnique: true, nameof(OkinDictionaryItem.Code));
            AssertIndex<OksoDictionaryItem>(context, isUnique: true, nameof(OksoDictionaryItem.Code));
            AssertIndex<OkpdtrDictionaryItem>(context, isUnique: true, nameof(OkpdtrDictionaryItem.Code));
        }
    }

    [Theory]
    [MemberData(nameof(CreateContexts))]
    public void SoftDeletedUniqueBusinessDocuments_UseGlobalUniquenessWithoutFilter(DbContext context)
    {
        using (context)
        {
            AssertGlobalUniqueIndex<CashOrder>(context, nameof(CashOrder.Number));
            AssertGlobalUniqueIndex<CashInventory>(context, nameof(CashInventory.Number));
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
                    .UseNpgsql("Host=localhost;Database=payplanner_unique_index_model;Username=postgres;Password=postgres")
                    .Options,
                new HttpContextAccessor(),
                useObjectAuditModel: false)
        };
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
