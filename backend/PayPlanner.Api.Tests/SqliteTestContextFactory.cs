using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Tests;

internal static class SqliteTestContextFactory
{
    private static readonly SemaphoreSlim TemplateGate = new(1, 1);
    private static readonly Dictionary<bool, SqliteConnection> Templates = [];

    public static async Task<PaymentContext> CreateAsync(
        bool useObjectAuditModel = true,
        IInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        try
        {
            await TemplateGate.WaitAsync();
            try
            {
                var template = await GetOrCreateTemplateAsync(useObjectAuditModel);
                template.BackupDatabase(connection);
            }
            finally
            {
                TemplateGate.Release();
            }

            var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection, contextOwnsConnection: true);
            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }

            return new PaymentContext(optionsBuilder.Options, useObjectAuditModel: useObjectAuditModel);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<SqliteConnection> GetOrCreateTemplateAsync(bool useObjectAuditModel)
    {
        if (Templates.TryGetValue(useObjectAuditModel, out var existing))
        {
            return existing;
        }

        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new PaymentContext(options, useObjectAuditModel: useObjectAuditModel);
        await context.Database.EnsureCreatedAsync();

        Templates.Add(useObjectAuditModel, connection);
        return connection;
    }
}
