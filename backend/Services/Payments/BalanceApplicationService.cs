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
                .Select(r => new BalanceApplicationResultDto
                {
                    RequestId = r.RequestId,
                    AppliedAmount = r.AppliedAmount,
                    Balance = r.Balance,
                    IsReplay = true,
                }).SingleOrDefaultAsync(ct);
            var status = new BalanceApplicationInspectionDto { RequestId = key, PaymentId = paymentId, Result = receipt };
            if (receipt is null)
            {
                // Absence is NOT cancellation: a delayed original request may still arrive.
                return new AcceptedResult((string?)null, status);
            }
            status.State = "Committed";
            return new OkObjectResult(status);
        }
        catch (Exception error) when (BalanceApplicationLease.IsConflict(error))
        {
            return new ConflictObjectResult(new { message = FinancialConcurrencyGuard.ConflictMessage });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

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
        if (actorId is not > 0) return new UnauthorizedResult();
        if (request.RequestId is not { } key || key == Guid.Empty || request.PaymentId <= 0 || request.Amount <= 0
            || request.SourcePaymentId is <= 0 || request.Description?.Length > 500)
            return new BadRequestObjectResult(new { message = "Проверьте идентификатор и параметры зачёта." });
        var hash = CommandHash(clientId, legalEntityId, request);
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct))
                return new UnauthorizedResult();
            if (await authorize(ct) is { } denied) return denied;
            // Never acquire an identity-only lock on an inaccessible/foreign payment.
            if (!await db.Payments.AsNoTracking().AnyAsync(p => p.Id == request.PaymentId && p.ClientId == clientId && p.LegalEntityId == legalEntityId, ct))
                return new NotFoundResult();
            await lease.LockOwnedPaymentAsync(request.PaymentId, clientId, legalEntityId, ct);
            var receipt = await db.BalanceApplicationReceipts.AsNoTracking()
                .SingleOrDefaultAsync(r => r.RequestId == key, ct);
            if (receipt is not null)
            {
                if (receipt.ActorUserId != actorId.Value) return new NotFoundResult();
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
            var result = clientId.HasValue
                ? await ClientBalanceService.ApplyCreditAsync(db, clientId.Value, request.PaymentId, request.Amount, DateTime.UtcNow, context, ct,
                    description: request.Description, sourcePaymentId: request.SourcePaymentId, externalKey: externalKey)
                : await ClientBalanceService.ApplyLegalEntityCreditAsync(db, legalEntityId!.Value, request.PaymentId, request.Amount, DateTime.UtcNow, context, ct,
                    description: request.Description, sourcePaymentId: request.SourcePaymentId, externalKey: externalKey);
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
