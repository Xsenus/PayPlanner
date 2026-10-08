using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public class FinancialPrecisionModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecimalProperties_HaveExplicitPrecisionAndScale(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            var decimalProperties = context.Model.GetEntityTypes()
                .SelectMany(entity => entity.GetProperties().Select(property => new { Entity = entity, Property = property }))
                .Where(item => IsDecimal(item.Property))
                .ToList();

            Assert.NotEmpty(decimalProperties);

            var missingPrecision = decimalProperties
                .Where(item => item.Property.GetPrecision() is null || item.Property.GetScale() is null)
                .Select(item => $"{item.Entity.ClrType.Name}.{item.Property.Name}")
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToList();

            Assert.Empty(missingPrecision);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinancialDecimalProperties_UseExpectedScale(bool postgres)
    {
        using (var context = CreateContext(postgres))
        {
            AssertPrecision<Payment>(context, nameof(Payment.Amount), precision: 18, scale: 2);
            AssertPrecision<Payment>(context, nameof(Payment.PaidAmount), precision: 18, scale: 2);
            AssertPrecision<MoneyOperation>(context, nameof(MoneyOperation.Amount), precision: 18, scale: 2);
            AssertPrecision<MoneyOperation>(context, nameof(MoneyOperation.VatRate), precision: 18, scale: 4);
            AssertPrecision<InvoiceLineItem>(context, nameof(InvoiceLineItem.Quantity), precision: 18, scale: 4);
            AssertPrecision<InvoiceLineItem>(context, nameof(InvoiceLineItem.Price), precision: 18, scale: 2);
        }
    }

    private static DbContext CreateContext(bool postgres)
    {
        if (postgres)
            return new PostgreSqlPaymentContext(
                new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                    .UseNpgsql("Host=localhost;Database=payplanner_precision_model;Username=postgres;Password=postgres")
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

    private static bool IsDecimal(IReadOnlyProperty property)
    {
        var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        return clrType == typeof(decimal);
    }

    private static void AssertPrecision<TEntity>(DbContext context, string propertyName, int precision, int scale)
    {
        var property = context.Model.FindEntityType(typeof(TEntity))?.FindProperty(propertyName)
            ?? throw new InvalidOperationException($"Property {typeof(TEntity).Name}.{propertyName} was not found.");

        Assert.Equal(precision, property.GetPrecision());
        Assert.Equal(scale, property.GetScale());
    }
}
