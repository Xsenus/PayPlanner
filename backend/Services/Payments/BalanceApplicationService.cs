using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Payments;

internal static class BalanceApplicationService
{
    public static async Task<IActionResult> ApplyLegacyAsync(PaymentContext db, int? clientId, int? legalEntityId,
        ApplyClientBalanceRequest request, HttpContext context, Func<CancellationToken, Task<IActionResult?>> authorize, CancellationToken ct)
    {
        var actorId = context.User.GetUserId();
        if (context.User.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
            if (await AuthorizeTargetAsync(db, actorId.Value, clientId, legalEntityId, request.PaymentId, authorize, ct) is { } denied) return denied;
            await lease.LockOwnedPaymentAsync(request.PaymentId, clientId, legalEntityId, ct);
            using var credit = new PaymentCreditInvariantScope(db);
            var result = await SpendAsync(db, clientId, legalEntityId, request, context, null, ct);
            if (!result.Success) return new BadRequestObjectResult(new { message = result.Error });
            await db.SaveChangesAsync(ct);
            if (!await credit.KeepsAppliedCreditAsync(ct)) return new BadRequestObjectResult(new { message = PaymentCreditInvariantScope.RejectionMessage });
            var balance = clientId.HasValue ? await ClientBalanceService.GetBalanceAsync(db, clientId.Value, ct)
                : await ClientBalanceService.GetLegalEntityBalanceAsync(db, legalEntityId!.Value, ct);
            await lease.CommitAsync(ct);
            return new OkObjectResult(new { appliedAmount = result.AppliedAmount, balance = Math.Round(balance, 2, MidpointRounding.AwayFromZero) });
        }
        catch (Exception error) when (BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        { return new ConflictObjectResult(new { message = FinancialConcurrencyGuard.ConflictMessage }); }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    private static async Task<IActionResult?> AuthorizeTargetAsync(PaymentContext db, int actorId, int? clientId, int? legalEntityId,
        int paymentId, Func<CancellationToken, Task<IActionResult?>> authorize, CancellationToken ct)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId && user.IsActive && user.IsApproved, ct)) return new UnauthorizedResult();
        if (await authorize(ct) is { } denied) return denied;
        return await db.Payments.AsNoTracking().ApplyUnambiguousInvoiceLinks()
            .AnyAsync(p => p.Id == paymentId && p.ClientId == clientId && p.LegalEntityId == legalEntityId, ct) ? null : new NotFoundResult();
    }

    private static Task<(bool Success, string? Error, decimal AppliedAmount)> SpendAsync(PaymentContext db,
        int? clientId, int? legalEntityId, ApplyClientBalanceRequest request, HttpContext context, string? externalKey, CancellationToken ct)
        => clientId.HasValue
            ? ClientBalanceService.ApplyCreditAsync(db, clientId.Value, request.PaymentId, request.Amount, DateTime.UtcNow, context, ct,
                description: request.Description, sourcePaymentId: request.SourcePaymentId, externalKey: externalKey)
            : ClientBalanceService.ApplyLegalEntityCreditAsync(db, legalEntityId!.Value, request.PaymentId, request.Amount, DateTime.UtcNow, context, ct,
                description: request.Description, sourcePaymentId: request.SourcePaymentId, externalKey: externalKey);

    public static async Task<IActionResult> InspectAsync(PaymentContext db, int? clientId, int? legalEntityId,
        Guid key, int paymentId, HttpContext context, Func<CancellationToken, Task<IActionResult?>> authorize, CancellationToken ct)
    {
        var actorId = context.User.GetUserId();
        if (actorId is not > 0) return new UnauthorizedResult();
        if (key == Guid.Empty || paymentId <= 0) return new BadRequestResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct))
                return new UnauthorizedResult();
            if (await authorize(ct) is { } denied) return denied;
            if (!await db.Payments.AsNoTracking().AnyAsync(p => p.Id == paymentId && p.ClientId == clientId && p.LegalEntityId == legalEntityId, ct))
                return new NotFoundResult();
            // A pending financial writer must finish before we report any observed state.
            await lease.LockOwnedPaymentAsync(paymentId, clientId, legalEntityId, ct);
            var receipt = await db.BalanceApplicationReceipts.AsNoTracking()
                .Where(r => r.RequestId == key && r.ActorUserId == actorId.Value && r.PaymentId == paymentId
                    && r.ClientId == clientId && r.LegalEntityId == legalEntityId)
                .Select(r => new BalanceApplicationReceipt
                {
                    RequestId = r.RequestId,
                    PaymentId = r.PaymentId,
                    State = r.State,
                    AppliedAmount = r.AppliedAmount,
                    Balance = r.Balance
                }).SingleOrDefaultAsync(ct);
            if (receipt is null)
            {
                // Absence is NOT cancellation: a delayed original request may still arrive.
                return new AcceptedResult((string?)null, new BalanceApplicationInspectionDto { RequestId = key, PaymentId = paymentId });
            }
            return new OkObjectResult(Outcome(receipt));
        }
        catch (Exception error) when (BalanceApplicationLease.IsConflict(error))
        {
            return new ConflictObjectResult(new { message = FinancialConcurrencyGuard.ConflictMessage });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    public static async Task<IActionResult> CancelAsync(PaymentContext db, int? clientId, int? legalEntityId,
        Guid key, int paymentId, HttpContext context, Func<CancellationToken, Task<IActionResult?>> authorize, CancellationToken ct)
    {
        var actorId = context.User.GetUserId();
        if (actorId is not > 0) return new UnauthorizedResult();
        if (key == Guid.Empty || paymentId <= 0) return new BadRequestResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct))
                return new UnauthorizedResult();
            if (await authorize(ct) is { } denied) return denied;
            if (!await db.Payments.AsNoTracking().AnyAsync(p => p.Id == paymentId && p.ClientId == clientId && p.LegalEntityId == legalEntityId, ct))
                return new NotFoundResult();
            await lease.LockOwnedPaymentAsync(paymentId, clientId, legalEntityId, ct);
            var receipt = await db.BalanceApplicationReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.RequestId == key, ct);
            if (receipt is not null)
            {
                if (receipt.ActorUserId != actorId.Value || receipt.ClientId != clientId || receipt.LegalEntityId != legalEntityId || receipt.PaymentId != paymentId)
                    return new NotFoundResult();
                // Cancellation never reverses money or rewrites an existing outcome.
                return new OkObjectResult(Outcome(receipt));
            }
            receipt = new BalanceApplicationReceipt
            {
                RequestId = key,
                ActorUserId = actorId.Value,
                ClientId = clientId,
                LegalEntityId = legalEntityId,
                PaymentId = paymentId,
                State = BalanceApplicationState.Cancelled,
                CommandVersion = Version,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.BalanceApplicationReceipts.Add(receipt);
            await db.SaveChangesAsync(ct);
            await lease.CommitAsync(ct);
            return new OkObjectResult(Outcome(receipt));
        }
        catch (Exception error) when (BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = FinancialConcurrencyGuard.ConflictMessage });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    private static BalanceApplicationInspectionDto Outcome(BalanceApplicationReceipt receipt)
        => new()
        {
            RequestId = receipt.RequestId,
            PaymentId = receipt.PaymentId,
            State = receipt.State switch
            {
                BalanceApplicationState.Committed => "Committed",
                BalanceApplicationState.Cancelled => "Cancelled",
                _ => throw new InvalidOperationException("Unknown balance-application outcome."),
            },
            Result = receipt.State == BalanceApplicationState.Committed ? new BalanceApplicationResultDto
            {
                RequestId = receipt.RequestId,
                AppliedAmount = receipt.AppliedAmount,
                Balance = receipt.Balance,
                IsReplay = true,
            } : null,
        };

    private const string Version = "balance-apply-v1";
    internal static string CommandHash(int? clientId, int? legalEntityId, ApplyClientBalanceRequest request)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Version,
            ClientId = clientId,
            LegalEntityId = legalEntityId,
            request.PaymentId,
            Amount = request.Amount.ToString("G29", CultureInfo.InvariantCulture),
            Description = request.Description?.Trim() ?? string.Empty,
            request.SourcePaymentId,
        }))));

    public static async Task<IActionResult> ApplyAsync(PaymentContext db, int? clientId, int? legalEntityId,
        ApplyClientBalanceRequest request, HttpContext context, Func<CancellationToken, Task<IActionResult?>> authorize, CancellationToken ct)
    {
        var actorId = context.User.GetUserId();
        if (context.User.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        if (request.RequestId is not { } key || key == Guid.Empty || request.PaymentId <= 0 || request.Amount <= 0
            || request.SourcePaymentId is <= 0 || request.Description?.Length > 500)
            return new BadRequestObjectResult(new { message = "Проверьте идентификатор и параметры зачёта." });
        var hash = CommandHash(clientId, legalEntityId, request);
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
            if (await AuthorizeTargetAsync(db, actorId.Value, clientId, legalEntityId, request.PaymentId, authorize, ct) is { } denied) return denied;
            await lease.LockOwnedPaymentAsync(request.PaymentId, clientId, legalEntityId, ct);
            var receipt = await db.BalanceApplicationReceipts.AsNoTracking()
                .SingleOrDefaultAsync(r => r.RequestId == key, ct);
            if (receipt is not null)
            {
                if (receipt.ActorUserId != actorId.Value) return new NotFoundResult();
                if (receipt.State == BalanceApplicationState.Cancelled)
                    return new ConflictObjectResult(new { message = "Эта попытка зачёта отменена. Поздний повтор не выполнит списание." });
                if (receipt.State != BalanceApplicationState.Committed)
                    return new ConflictObjectResult(new { message = "Результат зачёта не подтверждён. Обновите проверку." });
                if (receipt.CommandVersion != Version || receipt.CommandSha256 != hash
                    || receipt.ClientId != clientId || receipt.LegalEntityId != legalEntityId || receipt.PaymentId != request.PaymentId)
                    return new ConflictObjectResult(new { message = "Параметры этого зачёта уже зафиксированы. Проверьте результат перед новой операцией." });
                return new OkObjectResult(new BalanceApplicationResultDto
                {
                    RequestId = key,
                    AppliedAmount = receipt.AppliedAmount,
                    Balance = receipt.Balance,
                    IsReplay = true,
                });
            }

            // Source availability is derived from the existing ledger prefix. Keep that
            // accounting identity while replacing the timestamp suffix with a stable key.
            var externalKey = request.SourcePaymentId is { } sourceId
                ? $"{ClientBalanceService.BuildUnallocatedAdvanceApplicationKeyPrefix(sourceId)}{request.PaymentId}:{key:N}"
                : $"balance-apply:{actorId.Value}:{key:N}";
            using var credit = new PaymentCreditInvariantScope(db);
            var result = await SpendAsync(db, clientId, legalEntityId, request, context, externalKey, ct);
            if (!result.Success) return new BadRequestObjectResult(new { message = result.Error });
            await db.SaveChangesAsync(ct);
            var balance = clientId.HasValue ? await ClientBalanceService.GetBalanceAsync(db, clientId.Value, ct)
                : await ClientBalanceService.GetLegalEntityBalanceAsync(db, legalEntityId!.Value, ct);
            balance = Math.Round(balance, 2, MidpointRounding.AwayFromZero);
            db.BalanceApplicationReceipts.Add(new BalanceApplicationReceipt
            {
                ActorUserId = actorId.Value,
                RequestId = key,
                ClientId = clientId,
                LegalEntityId = legalEntityId,
                PaymentId = request.PaymentId,
                CommandVersion = Version,
                CommandSha256 = hash,
                AppliedAmount = result.AppliedAmount,
                Balance = balance,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            if (!await credit.KeepsAppliedCreditAsync(ct)) return new BadRequestObjectResult(new { message = PaymentCreditInvariantScope.RejectionMessage });
            await lease.CommitAsync(ct);
            return new OkObjectResult(new BalanceApplicationResultDto
            {
                RequestId = key,
                AppliedAmount = result.AppliedAmount,
                Balance = balance,
                IsReplay = false,
            });
        }
        catch (Exception error) when (BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = FinancialConcurrencyGuard.ConflictMessage });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}
