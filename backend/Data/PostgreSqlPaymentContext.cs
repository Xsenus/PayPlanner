using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;

namespace PayPlanner.Api.Data;

public sealed class PostgreSqlPaymentContext : PaymentContext
{
    public PostgreSqlPaymentContext(
        DbContextOptions<PostgreSqlPaymentContext> options,
        IHttpContextAccessor? httpContextAccessor = null,
        bool useObjectAuditModel = true)
        : base(options, httpContextAccessor, useObjectAuditModel)
    {
    }
}
