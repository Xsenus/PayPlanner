using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    public sealed record CourtRecoveryPaymentDto(
        int PaymentId,
        DateTime Date,
        string Description,
        decimal PaidAmount,
        decimal? AllocatedAmount,
        decimal EffectiveAmount,
        string? Warning);

    public sealed record CourtRecoveryOverviewDto(
        decimal? AwardedAmount,
        decimal? ReceivedAmount,
        decimal? RemainingAmount,
        IReadOnlyList<CourtRecoveryPaymentDto> LinkedPayments,
        IReadOnlyList<CourtRecoveryPaymentDto> AvailablePayments,
        bool HasMoreAvailablePayments,
        bool HasCompletePaymentVisibility);

    public sealed record SetCourtRecoveryPaymentRequest(decimal Amount);

    [HttpGet("{id:int}/court-recoveries")]
    public async Task<ActionResult<CourtRecoveryOverviewDto>> GetCourtRecoveries(
        int id, [FromQuery] string? search, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanView, ct) is { } forbidden)
            return forbidden;
        if (!await CanViewCaseFinanceAsync(ct))
            return Forbid();

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
            return Unauthorized();
        var caseAmounts = await _db.ClientCases.ApplyClientCaseVisibility(visibility)
            .AsNoTracking().Where(item => item.Id == id)
            .Select(item => new { item.CourtAwardedAmount }).FirstOrDefaultAsync(ct);
        if (caseAmounts is null)
            return NotFound();

        var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
        if (paymentsVisibility is null)
            return Forbid();
        var visiblePayments = _db.Payments.AsNoTracking().ApplyPaymentVisibility(paymentsVisibility);

        var links = await _db.ClientCaseCourtRecoveryPayments.AsNoTracking()
            .Where(item => item.ClientCaseId == id && visiblePayments.Any(payment => payment.Id == item.PaymentId))
            .Include(item => item.Payment)
            .OrderByDescending(item => item.Payment.Date)
            .ToListAsync(ct);
        var linkedPaymentIds = links.Select(item => item.PaymentId).ToArray();
        var linkedCasePaymentIds = await _db.InvoiceClientCases.AsNoTracking()
            .Where(item => item.ClientCaseId == id && linkedPaymentIds.Contains(item.PaymentId))
            .Select(item => item.PaymentId).ToListAsync(ct);
        var validLinkedIds = linkedCasePaymentIds.ToHashSet();

        var linked = links.Select(link =>
        {
            var payment = link.Payment;
            var warning = CourtRecoveryPaymentWarning(payment, id, validLinkedIds.Contains(link.PaymentId), link.AllocatedAmount);
            return new CourtRecoveryPaymentDto(
                link.PaymentId, payment.Date, payment.Description, payment.PaidAmount,
                link.AllocatedAmount,
                warning is null ? link.AllocatedAmount : 0m,
                warning);
        }).ToArray();

        var availableQuery = visiblePayments
            .Where(payment => payment.Status != PaymentStatus.Cancelled &&
                payment.Type == PaymentType.Income && payment.DocumentKind == DocumentKind.Payment &&
                payment.PaidAmount > 0m &&
                (payment.ClientCaseId == id || payment.InvoiceClientCases.Any(item => item.ClientCaseId == id)) &&
                !_db.ClientCaseCourtRecoveryPayments.Any(link => link.PaymentId == payment.Id));
        var normalizedSearch = search?.Trim();
        if (!string.IsNullOrEmpty(normalizedSearch))
        {
            if (normalizedSearch.Length > 100)
                return BadRequest(new { message = "Слишком длинный поисковый запрос." });
            availableQuery = int.TryParse(normalizedSearch, out var searchedPaymentId)
                ? availableQuery.Where(payment => payment.Id == searchedPaymentId || payment.Description.Contains(normalizedSearch))
                : availableQuery.Where(payment => payment.Description.Contains(normalizedSearch));
        }
        var availablePage = await availableQuery
            .OrderByDescending(payment => payment.Date).ThenByDescending(payment => payment.Id)
            .Take(101)
            .Select(payment => new CourtRecoveryPaymentDto(
                payment.Id, payment.Date, payment.Description, payment.PaidAmount,
                null, 0m, null))
            .ToListAsync(ct);
        var available = availablePage.Take(100).ToArray();

        var received = linked.Sum(item => item.EffectiveAmount);
        return Ok(new CourtRecoveryOverviewDto(
            caseAmounts.CourtAwardedAmount,
            paymentsVisibility.SeesAll ? received : null,
            paymentsVisibility.SeesAll && caseAmounts.CourtAwardedAmount is decimal awarded
                ? Math.Max(awarded - received, 0m) : null,
            linked,
            available,
            availablePage.Count > 100,
            paymentsVisibility.SeesAll));
    }

    [HttpPut("{id:int}/court-recoveries/{paymentId:int}")]
    public Task<IActionResult> SetCourtRecoveryPayment(
        int id, int paymentId, [FromBody] SetCourtRecoveryPaymentRequest request, CancellationToken ct)
        => RunCourtRecoveryMutationAsync(async lease =>
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
            return forbidden;
        if (!await CanViewCaseFinanceAsync(ct))
            return Forbid();
        if (request.Amount <= 0m || request.Amount != Math.Round(request.Amount, 2))
            return BadRequest(new { message = "Укажите положительную сумму взыскания с точностью до копеек." });

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
            return Unauthorized();
        var clientCase = await _db.ClientCases.ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
            return NotFound();
        await LockCourtRecoveryCaseAsync(id, ct);
        if (clientCase.CourtAwardedAmount is null or <= 0m)
            return BadRequest(new { message = "Сначала укажите присуждённую судом сумму в карточке дела." });

        var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
        if (paymentsVisibility is null)
            return Forbid();

        var payment = await _db.Payments.ApplyPaymentVisibility(paymentsVisibility)
            .FirstOrDefaultAsync(item => item.Id == paymentId &&
                (item.ClientCaseId == id || item.InvoiceClientCases.Any(link => link.ClientCaseId == id)), ct);
        if (payment is null)
            return NotFound();
        await lease.LockPaymentAsync(paymentId, ct);
        if (payment.Type != PaymentType.Income || payment.DocumentKind != DocumentKind.Payment ||
            payment.Status == PaymentStatus.Cancelled || payment.PaidAmount <= 0m)
            return BadRequest(new { message = "Можно связать только фактически оплаченный приходный платёж по этому делу." });
        if (request.Amount > payment.PaidAmount)
            return BadRequest(new { message = "Сумма взыскания не может превышать уже оплаченную часть платежа." });

        var existing = await _db.ClientCaseCourtRecoveryPayments
            .FirstOrDefaultAsync(item => item.PaymentId == paymentId, ct);
        if (existing is not null && existing.ClientCaseId != id)
            return Conflict(new { message = "Этот платёж уже отмечен как взыскание по другому делу." });

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            _db.ClientCaseCourtRecoveryPayments.Add(new ClientCaseCourtRecoveryPayment
            {
                ClientCaseId = id,
                PaymentId = paymentId,
                AllocatedAmount = request.Amount,
                CreatedAt = now,
                CreatedByUserId = User.GetUserId(),
                UpdatedAt = now,
                UpdatedByUserId = User.GetUserId(),
            });
        }
        else
        {
            existing.AllocatedAmount = request.Amount;
            existing.UpdatedAt = now;
            existing.UpdatedByUserId = User.GetUserId();
        }

        _db.SetAuditReason($"Связь платежа #{paymentId} с взысканием по делу #{id}");
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }, ct);

    [HttpDelete("{id:int}/court-recoveries/{paymentId:int}")]
    public Task<IActionResult> RemoveCourtRecoveryPayment(int id, int paymentId, CancellationToken ct)
        => RunCourtRecoveryMutationAsync(async lease =>
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
            return forbidden;
        if (!await CanViewCaseFinanceAsync(ct))
            return Forbid();
        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
            return Unauthorized();
        if (!await _db.ClientCases.ApplyClientCaseVisibility(visibility).AsNoTracking().AnyAsync(item => item.Id == id, ct))
            return NotFound();

        await LockCourtRecoveryCaseAsync(id, ct);
        var paymentsVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
        if (paymentsVisibility is null)
            return Forbid();
        var visiblePayments = _db.Payments.ApplyPaymentVisibility(paymentsVisibility);

        var link = await _db.ClientCaseCourtRecoveryPayments
            .FirstOrDefaultAsync(item => item.ClientCaseId == id && item.PaymentId == paymentId &&
                visiblePayments.Any(payment => payment.Id == item.PaymentId), ct);
        if (link is null)
            return NotFound();
        await lease.LockPaymentAsync(paymentId, ct);
        _db.ClientCaseCourtRecoveryPayments.Remove(link);
        _db.SetAuditReason($"Удалена связь платежа #{paymentId} с взысканием по делу #{id}");
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }, ct);

    private async Task<IActionResult> RunCourtRecoveryMutationAsync(
        Func<BalanceApplicationLease, Task<IActionResult>> write, CancellationToken ct)
    {
        var actorId = User.GetUserId();
        if (User.Identity?.IsAuthenticated != true || actorId is not > 0)
            return Unauthorized();
        BalanceApplicationLease? lease = null;
        try
        {
            // Read permissions, ownership and paid amounts only inside the same
            // clean lease used by the payment writers. Never discard pending work.
            lease = await BalanceApplicationLease.BeginAsync(_db, actorId.Value, ct);
            if (_db.Database.IsNpgsql())
                await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
            if (!await _db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct))
                return Unauthorized();
            _casesAccessTask = null;
            _canViewCaseFinanceTask = null;
            var result = await write(lease);
            if (result is NoContentResult)
                await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested
            && (BalanceApplicationLease.IsConflict(error) || IsCourtRecoveryUniqueConflict(error))
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return Conflict(new { message = "Платёж или связь взыскания уже изменены либо сейчас заняты. Обновите список платежей и повторите действие." });
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    private async Task LockCourtRecoveryCaseAsync(int id, CancellationToken ct)
    {
        // Lock only a case already admitted by the current visibility check.
        // Keep its award and ownership stable until this mutation commits.
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {id} FOR SHARE NOWAIT", ct);
    }

    private static bool IsCourtRecoveryUniqueConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_ClientCaseCourtRecoveryPayments" }
                || current is SqliteException { SqliteErrorCode: 19 } sqlite
                && sqlite.Message.Contains("ClientCaseCourtRecoveryPayments.PaymentId", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string? CourtRecoveryPaymentWarning(Payment payment, int clientCaseId, bool linkedByInvoice, decimal amount)
    {
        if (payment.ClientCaseId != clientCaseId && !linkedByInvoice)
            return "Платёж больше не связан с этим делом; сумма не учитывается.";
        if (payment.Status == PaymentStatus.Cancelled || payment.Type != PaymentType.Income ||
            payment.DocumentKind != DocumentKind.Payment)
            return "Платёж больше не является действительным приходным платежом; сумма не учитывается.";
        if (payment.PaidAmount < amount)
            return "Оплаченная сумма платежа уменьшилась; проверьте и исправьте связь. Пока сумма не учитывается.";
        return null;
    }
}
