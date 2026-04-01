using Microsoft.EntityFrameworkCore;

namespace PayPlanner.Api.Data;

public sealed class PostgreSqlPaymentContext : PaymentContext
{
    public PostgreSqlPaymentContext(DbContextOptions<PostgreSqlPaymentContext> options)
        : base(options)
    {
    }
}
