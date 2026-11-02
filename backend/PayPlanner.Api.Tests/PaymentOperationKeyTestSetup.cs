using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Tests;

// Fixture metadata for pre-existing financial/permission tests. Registration
// itself and unkeyed calls are exercised by LegacyPaymentOperationKeyTests.
internal static class PaymentOperationKeyTestSetup
{
    internal static async Task PrepareAsync(ControllerBase controller, PaymentContext db, Payment model)
    {
        var key = Guid.NewGuid();
        controller.Request.Headers["Idempotency-Key"] = key.ToString("D");
        var actor = controller.User.GetUserId();
        // Dirty/external-context tests must retain their original pending work
        // and transaction. A syntactically valid key reaches that admission guard.
        if (actor is not > 0 || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) return;
        var now = DateTimeOffset.UtcNow;
        // Registration is fixture setup before the financial action. Use a
        // separate context without the writer's fault/hold interceptors so its
        // save counter/barrier still observes the actual payment transaction.
        await using PaymentContext metadata = db.Database.IsNpgsql()
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(db.Database.GetDbConnection()).Options, useObjectAuditModel: false)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(db.Database.GetDbConnection(), contextOwnsConnection: false).Options, useObjectAuditModel: false);
        metadata.PaymentCreateAttempts.Add(new PaymentCreateAttempt
        {
            Id = key,
            ActorUserId = actor.Value,
            Operation = "primary",
            SellerCompanyId = model.SellerCompanyId,
            SellerBranchId = model.SellerBranchId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        });
        await metadata.SaveChangesAsync();
    }

    internal static async Task<IActionResult> CreateAsync(ControllerBase controller, PaymentContext db, Payment model, CancellationToken ct = default)
    {
        await PrepareAsync(controller, db, model);
        return controller switch
        {
            PaymentsController current => await current.Create(model, ct),
            PaymentsV1Controller v1 => await v1.Create(model, ct),
            PaymentsV2Controller v2 => await v2.Create(model, ct),
            _ => throw new ArgumentException("Unsupported payment fixture route.", nameof(controller)),
        };
    }
}
