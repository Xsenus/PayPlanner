using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PayPlanner.Api.Data;

public sealed class PostgreSqlPaymentContextDesignTimeFactory : IDesignTimeDbContextFactory<PostgreSqlPaymentContext>
{
    public PostgreSqlPaymentContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("PAYPLANNER_PG_MIGRATIONS_CONNECTION")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=127.0.0.1;Port=5432;Database=payplanner;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new PostgreSqlPaymentContext(options);
    }
}
