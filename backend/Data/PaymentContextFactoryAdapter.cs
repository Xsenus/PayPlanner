using Microsoft.EntityFrameworkCore;

namespace PayPlanner.Api.Data;

public sealed class PaymentContextFactoryAdapter<TContext> : IDbContextFactory<PaymentContext>
    where TContext : PaymentContext
{
    private readonly IDbContextFactory<TContext> _innerFactory;

    public PaymentContextFactoryAdapter(IDbContextFactory<TContext> innerFactory)
    {
        _innerFactory = innerFactory;
    }

    public PaymentContext CreateDbContext()
        => _innerFactory.CreateDbContext();

    public async Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => await _innerFactory.CreateDbContextAsync(cancellationToken);
}
