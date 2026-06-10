using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Data;

public sealed class PaymentContextDesignTimeFactory : IDesignTimeDbContextFactory<PaymentContext>
{
    public PaymentContext CreateDbContext(string[] args)
    {
        var rawConnectionString =
            Environment.GetEnvironmentVariable("PAYPLANNER_SQLITE_MIGRATIONS_CONNECTION")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Data Source=payplanner.db";

        var connectionString = DatabaseProviderResolver.NormalizeSqliteConnection(rawConnectionString);
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connectionString)
            .UsePayPlannerSqliteMigrations()
            .Options;

        return new PaymentContext(options);
    }
}
