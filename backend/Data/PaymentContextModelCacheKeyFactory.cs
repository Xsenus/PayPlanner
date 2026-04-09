using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace PayPlanner.Api.Data;

public sealed class PaymentContextModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        return context is PaymentContext paymentContext
            ? (context.GetType(), paymentContext.UseObjectAuditModel, designTime)
            : (object)(context.GetType(), designTime);
    }
}
