using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed class ContractResponsibleCandidate
{
    public int ContractId { get; set; }
    public int UserId { get; set; }
}

public sealed record ContractExpirationSource(int ContractId, int RecipientUserId, DateTime ExpirationDate, int Stage);

// This guard controls a new delivery. Historical inbox reads retain their existing ACL rules.
public sealed class ContractExpirationNotificationPolicy(PaymentContext db, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private ContractExpirationRecipientSettings? _settings;

    public static int? StageForDays(int actualDays)
        => actualDays switch
        {
            >= 15 and <= 30 => 30,
            >= 8 and <= 14 => 14,
            >= 0 and <= 7 => 7,
            _ => null
        };

    public static string EventKey(int contractId, int recipientUserId, DateTime expirationDate, int stage)
    {
        var dateKey = expirationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var legacy = $"contract:{contractId}:expiring:{recipientUserId}:{dateKey}";
        return stage == 7 ? legacy : $"{legacy}:stage:{stage}";
    }

    public static bool IsCandidate(string? eventKey)
        => eventKey?.StartsWith("contract:", StringComparison.Ordinal) == true
            && eventKey.Contains(":expiring:", StringComparison.Ordinal);

    public static bool TryReadSource(string? eventKey, out ContractExpirationSource? source)
    {
        source = null;
        if (!IsCandidate(eventKey)) return false;
        var parts = eventKey!.Split(':');
        if (parts.Length is not (5 or 7) || parts[0] != "contract" || parts[2] != "expiring"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var contractId) || contractId <= 0
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var recipientId) || recipientId <= 0
            || !DateTime.TryParseExact(parts[4], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return false;
        var stage = 7;
        if (parts.Length == 7 && (parts[5] != "stage"
            || !int.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out stage)
            || stage is not (14 or 30))) return false;
        if (EventKey(contractId, recipientId, date, stage) != eventKey) return false;
        source = new(contractId, recipientId, date.Date, stage);
        return true;
    }

    public static IQueryable<ContractResponsibleCandidate> ResponsibleCandidates(
        PaymentContext context, IReadOnlyCollection<int> contractIds)
    {
        var ids = contractIds.ToArray();
        var clients = context.ClientContracts.AsNoTracking()
            .Where(link => ids.Contains(link.ContractId) && link.Client.ResponsibleUserId.HasValue)
            .Select(link => new ContractResponsibleCandidate { ContractId = link.ContractId, UserId = link.Client.ResponsibleUserId!.Value });
        var organizations = context.LegalEntityContracts.AsNoTracking()
            .Where(link => ids.Contains(link.ContractId) && link.LegalEntity.ResponsibleUserId.HasValue)
            .Select(link => new ContractResponsibleCandidate { ContractId = link.ContractId, UserId = link.LegalEntity.ResponsibleUserId!.Value });
        var invoices = from link in context.ContractInvoices.AsNoTracking()
                       join responsible in context.PaymentResponsibles.AsNoTracking() on link.InvoiceId equals responsible.PaymentId
                       where ids.Contains(link.ContractId)
                       select new ContractResponsibleCandidate { ContractId = link.ContractId, UserId = responsible.UserId };
        var payments = from link in context.ContractPayments.AsNoTracking()
                       join responsible in context.PaymentResponsibles.AsNoTracking() on link.PaymentId equals responsible.PaymentId
                       where ids.Contains(link.ContractId)
                       select new ContractResponsibleCandidate { ContractId = link.ContractId, UserId = responsible.UserId };
        var acts = context.ContractActs.AsNoTracking()
            .Where(link => ids.Contains(link.ContractId) && link.Act.ResponsibleId.HasValue)
            .Select(link => new ContractResponsibleCandidate { ContractId = link.ContractId, UserId = link.Act.ResponsibleId!.Value });
        var directPayments = from payment in context.Payments.AsNoTracking()
                             join responsible in context.PaymentResponsibles.AsNoTracking() on payment.Id equals responsible.PaymentId
                             where payment.ContractId.HasValue && ids.Contains(payment.ContractId.Value)
                             select new ContractResponsibleCandidate { ContractId = payment.ContractId!.Value, UserId = responsible.UserId };
        return clients.Concat(organizations).Concat(invoices).Concat(payments).Concat(acts).Concat(directPayments).Distinct();
    }

    public async Task<bool> CanSendAsync(string eventKey, int? recipientUserId, CancellationToken ct,
        int? expectedContractId = null, bool requireSource = false)
    {
        // Opaque historical MAX templates retain their existing neutral behavior.
        if (!TryReadSource(eventKey, out var source)) return !requireSource && !IsCandidate(eventKey);
        if (recipientUserId is not > 0 || source!.RecipientUserId != recipientUserId
            || expectedContractId.HasValue && expectedContractId.Value != source.ContractId) return false;
        var zone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(db, recipientUserId.Value, ct);
        var today = CalendarTimeZoneHelper.ToUserLocal(_clock.GetUtcNow().UtcDateTime, zone).Date;
        if (StageForDays((source.ExpirationDate - today).Days) != source.Stage) return false;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, recipientUserId.Value.ToString(CultureInfo.InvariantCulture))], "contract-expiration"));
        var access = await db.ResolveSectionAccessAsync(principal, RolePermissionCatalog.Contracts, ct, requireApprovedUser: true);
        if (access?.Permission?.CanView != true) return false;
        var contracts = db.Contracts.AsNoTracking().ApplyContractVisibility(access.Visibility)
            .Where(contract => contract.Id == source.ContractId && !contract.IsOpenEnded
                && contract.ValidUntil.HasValue && contract.ValidUntil.Value.Date == source.ExpirationDate
                && contract.Status != ContractStatus.Terminated && contract.Status != ContractStatus.Cancelled);
        var responsible = ResponsibleCandidates(db, [source.ContractId]);
        var employees = from candidate in responsible
                        join employee in db.Users.AsNoTracking() on candidate.UserId equals employee.Id
                        select new { employee.DepartmentId, employee.Department };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var settings = _settings ??= await ContractExpirationRecipientSettings.LoadAsync(db, ct);
            var raw = settings.RawValue;
            var selectedUser = settings.UserIds.Contains(recipientUserId.Value);
            var roleIds = settings.RoleIds;
            var departmentIds = settings.DepartmentManagersEnabled
                ? settings.DepartmentManagers.Where(item => item.ManagerUserId == recipientUserId.Value)
                    .Select(item => item.DepartmentId).ToArray()
                : [];
            var proof = await contracts.Select(contract => new
            {
                Responsible = responsible.Any(candidate => candidate.ContractId == contract.Id && candidate.UserId == recipientUserId),
                Selected = selectedUser
                    || db.Users.Any(user => user.Id == recipientUserId && roleIds.Contains(user.RoleId)),
                ManagedDepartment = departmentIds.Length > 0 && db.Departments.Any(department => department.IsActive
                    && departmentIds.Contains(department.Id)
                    && employees.Any(employee => employee.DepartmentId == department.Id)),
                LegacyDepartment = departmentIds.Length > 0 && employees.Any(employee => !employee.DepartmentId.HasValue),
                ConfigurationCurrent = raw == null
                    ? !db.SystemSettings.Any(setting => setting.Key == ContractExpirationRecipientSettings.SettingKey)
                    : db.SystemSettings.Any(setting => setting.Key == ContractExpirationRecipientSettings.SettingKey && setting.Value == raw)
            }).SingleOrDefaultAsync(ct);
            if (proof is null) return false;
            if (proof.Responsible) return true;
            if (!proof.ConfigurationCurrent)
            {
                _settings = await ContractExpirationRecipientSettings.LoadAsync(db, ct);
                continue;
            }
            if (proof.Selected || proof.ManagedDepartment) return true;
            if (!proof.LegacyDepartment) return false;
            var routes = await DepartmentRoutesAsync(db, settings.DepartmentManagers
                .Where(item => item.ManagerUserId == recipientUserId.Value).ToArray(), ct);
            var legacyNames = routes.Where(item => item.LegacyNameUnique)
                .Select(item => item.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matchesDepartment = false;
            await foreach (var employee in employees.Where(employee => !employee.DepartmentId.HasValue)
                .Distinct().AsAsyncEnumerable().WithCancellation(ct))
            {
                matchesDepartment = !string.IsNullOrWhiteSpace(employee.Department)
                    && legacyNames.Contains(employee.Department.Trim());
                if (matchesDepartment) break;
            }
            if (!matchesDepartment) return false;
            // Revalidate source and raw configuration after the department queries.
            var stillCurrent = await contracts.AnyAsync(contract => raw == null
                ? !db.SystemSettings.Any(setting => setting.Key == ContractExpirationRecipientSettings.SettingKey)
                : db.SystemSettings.Any(setting => setting.Key == ContractExpirationRecipientSettings.SettingKey && setting.Value == raw), ct);
            if (stillCurrent) return true;
            _settings = await ContractExpirationRecipientSettings.LoadAsync(db, ct);
        }
        return false;
    }
    public static async Task<IReadOnlyList<ContractExpirationDepartmentRoute>> DepartmentRoutesAsync(
        PaymentContext context, IReadOnlyCollection<ContractExpirationDepartmentManager> mappings, CancellationToken ct)
    {
        var ids = mappings.Select(item => item.DepartmentId).Distinct().ToArray();
        if (ids.Length == 0) return [];
        var departments = await context.Departments.AsNoTracking()
            .Where(item => item.IsActive && ids.Contains(item.Id))
            .Select(item => new { item.Id, item.Name }).ToListAsync(ct);
        if (departments.Count == 0) return [];
        var counts = departments.Select(item => item.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name, _ => 0, StringComparer.OrdinalIgnoreCase);
        // SQLite's built-in lower() does not fold Cyrillic. Stream slim dictionary
        // names, retain only configured counters, and include duplicates outside the mapping.
        await foreach (var name in context.Departments.AsNoTracking().Where(item => item.IsActive)
            .Select(item => item.Name).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (counts.TryGetValue(name.Trim(), out var count)) counts[name.Trim()] = count + 1;
        }
        var byId = departments.ToDictionary(item => item.Id);
        return mappings.Where(item => byId.ContainsKey(item.DepartmentId))
            .Select(item => new ContractExpirationDepartmentRoute(item.DepartmentId, item.ManagerUserId,
                byId[item.DepartmentId].Name, !string.IsNullOrWhiteSpace(byId[item.DepartmentId].Name)
                    && counts[byId[item.DepartmentId].Name.Trim()] == 1)).ToArray();
    }

}

public sealed record ContractExpirationDepartmentRoute(int DepartmentId, int ManagerUserId, string Name, bool LegacyNameUnique);
